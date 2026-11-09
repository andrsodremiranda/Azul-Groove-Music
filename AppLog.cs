using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AzulGroove
{
    // Log em arquivo: %AppData%\AzulGroove\logs\azulgroove.log
    // Cada linha é gravada e enviada ao disco na hora, então mesmo se o app travar ou fechar,
    // a última linha mostra o que ele estava fazendo.
    static class AppLog
    {
        static readonly object gate = new object();
        static readonly string SessionId = Guid.NewGuid().ToString("N").Substring(0, 6);
        const long MaxBytes = 2 * 1024 * 1024;

        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove", "logs");
        public static readonly string FilePath = Path.Combine(Dir, "azulgroove.log");
        static readonly string FlagFile = Path.Combine(Dir, "session.running");

        public static void Write(string category, string message)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Dir);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        string old = FilePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + SessionId + "] [" + category + "] " + message + Environment.NewLine;
                    byte[] bytes = new UTF8Encoding(false).GetBytes(line);
                    using (var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        // Só força a gravação no disco (lenta) para erros e travamentos; o resto vai pelo cache do sistema (antes cada linha esperava o disco e atrasava a abertura e o YouTube)
                        stream.Flush(category.StartsWith("ERRO", StringComparison.Ordinal) || category.StartsWith("TRAVADO", StringComparison.Ordinal));
                    }
                }
            }
            catch (Exception ex) { Trace.WriteLine("Could not write log: " + ex.Message); }
        }

        // Memória, threads e handles do app agora (ajuda a ver se algo está "vazando" antes de travar)
        public static string Resources()
        {
            try
            {
                using (Process p = Process.GetCurrentProcess())
                    return "memória " + (p.WorkingSet64 >> 20) + " MB, threads " + p.Threads.Count + ", handles " + p.HandleCount;
            }
            catch (Exception ex) { return "recursos indisponíveis (" + ex.Message + ")"; }
        }

        public static void StartSession()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(FlagFile))
                    Write("SESSAO", "⚠ A sessão anterior terminou de forma inesperada (travou ou foi fechada sem aviso). Dados dela: " + File.ReadAllText(FlagFile).Trim());
                File.WriteAllText(FlagFile, "iniciada em " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ", PID " + Environment.ProcessId);
            }
            catch (Exception ex) { Trace.WriteLine("Could not mark session: " + ex.Message); }
            Write("SESSAO", "Iniciado | versão " + typeof(AppLog).Assembly.GetName().Version + " | " + RuntimeInformation.OSDescription +
                " | .NET " + Environment.Version + " | " + (Environment.Is64BitProcess ? "64" : "32") + " bits | " + Resources());
        }

        public static void EndSession(string how)
        {
            Write("SESSAO", how);
            try { File.Delete(FlagFile); } catch (Exception ex) { Trace.WriteLine("Could not clear session mark: " + ex.Message); }
        }

        public static string Tail(int maxChars)
        {
            try
            {
                lock (gate)
                {
                    if (!File.Exists(FilePath)) return string.Empty;
                    using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        stream.Seek(Math.Max(0, stream.Length - maxChars * 3L), SeekOrigin.Begin);
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            string text = reader.ReadToEnd();
                            return text.Length > maxChars ? text.Substring(text.Length - maxChars) : text;
                        }
                    }
                }
            }
            catch (Exception ex) { return "Não foi possível ler o log: " + ex.Message; }
        }
    }
}
