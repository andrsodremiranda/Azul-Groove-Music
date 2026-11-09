using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace AzulGroove
{
    public partial class App : Application
    {
        static readonly string Identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName)));
        static readonly string MutexName = "Local\\AzulGrooveMusic-" + Identity;
        static readonly string PipeName = "AzulGrooveMusic-" + Identity;

        // Modo seguro: abra com o argumento --safe (atalho "modo seguro" do instalador) se o app travar por causa da placa de vídeo
        internal static bool SafeMode { get; private set; }

        Mutex instanceMutex;
        bool isPrimary;
        CancellationTokenSource serverCancellation;

        static readonly string LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove", "error.log");

        // Registra o erro em %AppData%\AzulGroove\error.log (antes o app fechava ou travava sem dizer nada)
        internal static void LogError(string where, Exception ex)
        {
            AppLog.Write("ERRO " + where, ex?.ToString() ?? "(sem detalhes)");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
                var info = new FileInfo(LogFile);
                if (info.Exists && info.Length > 512 * 1024) File.Delete(LogFile);
                File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + where + "] " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }

        // Se uma cópia anterior do app ficou travada em segundo plano, ela impedia o app de abrir de novo. Fecha só a que não responde.
        static bool CloseFrozenInstances()
        {
            bool closed = false;
            try
            {
                int self = Environment.ProcessId;
                foreach (var process in System.Diagnostics.Process.GetProcessesByName(System.Diagnostics.Process.GetCurrentProcess().ProcessName))
                {
                    try
                    {
                        if (process.Id == self) continue;
                        if (!process.Responding) { process.Kill(true); process.WaitForExit(3000); closed = true; }
                    }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not inspect running instance: " + ex.Message); }
                    finally { process.Dispose(); }
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not list running instances: " + ex.Message); }
            return closed;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            SafeMode = e.Args.Any(a => string.Equals(a, "--safe", StringComparison.OrdinalIgnoreCase));
            string[] mediaArgs = e.Args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
            DispatcherUnhandledException += (s, a) => { LogError("UI", a.Exception); a.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, a) => LogError("Domain", a.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, a) => { LogError("Task", a.Exception); a.SetObserved(); };

            instanceMutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew && CloseFrozenInstances())
            {
                try { createdNew = instanceMutex.WaitOne(3000); }
                catch (AbandonedMutexException) { createdNew = true; }
            }
            if (!createdNew)
            {
                try
                {
                    SendToPrimary(mediaArgs);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Azul Groove Music já está aberto, mas não foi possível encaminhar o arquivo:\n" + ex.Message,
                        "Azul Groove Music", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                Shutdown();
                return;
            }

            isPrimary = true;
            AppLog.StartSession();
            bool invariantMode = AppContext.TryGetSwitch("System.Globalization.Invariant", out bool invariantOn) && invariantOn;
            AppLog.Write("SESSAO", "Idioma do sistema: " + System.Globalization.CultureInfo.CurrentCulture.Name + (invariantMode ? " | ⚠ GLOBALIZAÇÃO INVARIÁVEL ATIVA (o WPF não funciona assim)" : " | globalização normal"));
            AppDomain.CurrentDomain.ProcessExit += (s, a) => AppLog.Write("SESSAO", "ProcessExit: o processo está encerrando");
            SessionEnding += (s, a) => AppLog.Write("SESSAO", "Windows está encerrando a sessão: " + a.ReasonSessionEnding);
            Task.Run(RegisterOpenWith);
            if (SafeMode)
            {
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                AppLog.Write("SESSAO", "MODO SEGURO ativo: vídeo por software, sem animações de fundo e sem aceleração de hardware no VLC");
            }
            var window = new MainWindow(mediaArgs);
            MainWindow = window;
            serverCancellation = new CancellationTokenSource();
            _ = ListenForActivationAsync(window, serverCancellation.Token);
            window.Show();
        }

        static void SendToPrimary(string[] paths)
        {
            using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.None))
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!client.IsConnected && DateTime.UtcNow < deadline)
                {
                    try { client.Connect(250); }
                    catch (TimeoutException ex) { System.Diagnostics.Trace.WriteLine("Waiting for primary app instance: " + ex.Message); }
                }
                if (!client.IsConnected) throw new TimeoutException("A janela principal não respondeu.");
                using (var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, true))
                {
                    writer.WriteLine(JsonSerializer.Serialize(paths));
                    writer.Flush();
                }
            }
        }

        static async Task ListenForActivationAsync(MainWindow window, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
                    {
                        await server.WaitForConnectionAsync(cancellationToken);
                        using (var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true))
                        {
                            string message = await reader.ReadLineAsync();
                            string[] paths = string.IsNullOrWhiteSpace(message) ? Array.Empty<string>() :
                                JsonSerializer.Deserialize<string[]>(message) ?? Array.Empty<string>();
                            _ = window.Dispatcher.BeginInvoke(new Action(() => window.HandleActivation(paths)));
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("Single-instance pipe failed: " + ex.Message);
                    await Task.Delay(250);
                }
            }
        }

        static void RegisterOpenWith()
        {
            string processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath)) return;
            string command = "\"" + processPath + "\" \"%1\"";
            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                string entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrWhiteSpace(entryAssembly)) return;
                command = "\"" + processPath + "\" \"" + entryAssembly + "\" \"%1\"";
            }

            try
            {
                using (RegistryKey existing = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Applications\AzulGrooveMusic.exe\shell\open\command"))
                    if (string.Equals(existing?.GetValue(null) as string, command, StringComparison.OrdinalIgnoreCase)) return;
                string[] extensions = { ".mp3", ".flac", ".wav", ".ogg", ".opus", ".m4a", ".aac", ".wma",
                    ".aiff", ".ape", ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v", ".wmv", ".flv", ".ts", ".mpg", ".mpeg", ".3gp" };
                using (RegistryKey appKey = Registry.CurrentUser.CreateSubKey(
                    @"Software\Classes\Applications\AzulGrooveMusic.exe\shell\open\command"))
                    appKey?.SetValue(null, command);
                using (RegistryKey typesKey = Registry.CurrentUser.CreateSubKey(
                    @"Software\Classes\Applications\AzulGrooveMusic.exe\SupportedTypes"))
                {
                    foreach (string extension in extensions)
                        typesKey?.SetValue(extension, string.Empty);
                }
                using (RegistryKey capabilities = Registry.CurrentUser.CreateSubKey(@"Software\AzulGroove\Capabilities"))
                using (RegistryKey associations = capabilities?.CreateSubKey("FileAssociations"))
                {
                    capabilities?.SetValue("ApplicationName", "Azul Groove Music");
                    capabilities?.SetValue("ApplicationDescription", "Reproduza músicas e vídeos com Azul Groove Music");
                    foreach (string extension in extensions)
                    {
                        string progId = "AzulGrooveMusic" + extension.Replace(".", "_");
                        associations?.SetValue(extension, progId);
                        using (RegistryKey openCommand = Registry.CurrentUser.CreateSubKey(
                            @"Software\Classes\" + progId + @"\shell\open\command"))
                            openCommand?.SetValue(null, command);
                        using (RegistryKey icon = Registry.CurrentUser.CreateSubKey(
                            @"Software\Classes\" + progId + @"\DefaultIcon"))
                            icon?.SetValue(null, "\"" + processPath + "\",0");
                    }
                }
                using (RegistryKey registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                    registered?.SetValue("Azul Groove Music", @"Software\AzulGroove\Capabilities");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Could not register media file support: " + ex.Message);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (isPrimary) AppLog.EndSession("Encerrado normalmente (código " + e.ApplicationExitCode + ") | " + AppLog.Resources());
            serverCancellation?.Cancel();
            serverCancellation?.Dispose();
            if (instanceMutex != null)
            {
                try { instanceMutex.ReleaseMutex(); }
                catch (ApplicationException ex) { System.Diagnostics.Trace.WriteLine("Could not release app mutex: " + ex.Message); }
                instanceMutex.Dispose();
            }
            base.OnExit(e);
        }
    }
}
