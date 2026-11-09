using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace AzulGroove
{
    sealed class YouTubeSettings
    {
        public string Links { get; set; } = "";
        public int Mode { get; set; }
        public string Folder { get; set; }
    }

    // Página "YouTube": sincroniza playlists do YouTube / YouTube Music usando o yt-dlp (e o ffmpeg para converter).
    // As ferramentas são baixadas uma única vez para %AppData%\AzulGroove\tools.
    public partial class MainWindow
    {
        const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";
        static readonly string ToolsDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove", "tools");
        static readonly string YtSettingsFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove", "youtube.json");
        static readonly HttpClient ytHttp = CreateYtHttp();
        static readonly Regex YtIdRx = new Regex(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
        static readonly Regex YtIdInNameRx = new Regex(@"\[([A-Za-z0-9_-]{11})\]\.[^.\\/]+$", RegexOptions.Compiled);
        static readonly Regex YtItemRx = new Regex(@"Downloading (?:item|video) (\d+) of (\d+)", RegexOptions.Compiled);
        static readonly Regex YtPctRx = new Regex(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);

        CancellationTokenSource ytCts;
        bool ytBusy;
        string ytFolder;
        int ytLinkIndex, ytLinkCount, ytItem, ytTotal;
        double ytPct;
        bool ytBarActive;
        string ytStatusPending;
        volatile string ytPhase = "parado";
        long ytLastPong, ytLastWarn, ytLastBeat, ytLastProgLog;
        readonly object ytLock = new object();
        readonly System.Collections.Concurrent.ConcurrentQueue<string> ytLogQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        System.Windows.Threading.DispatcherTimer ytUiTimer;
        System.Threading.Timer ytWatchdog;

        static HttpClient CreateYtHttp()
        {
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AzulGrooveMusic/1.0");
            return client;
        }

        // ---------- Configurações da página ----------
        void InitYouTube()
        {
            var settings = new YouTubeSettings();
            try
            {
                if (File.Exists(YtSettingsFile))
                    settings = JsonSerializer.Deserialize<YouTubeSettings>(File.ReadAllText(YtSettingsFile)) ?? settings;
            }
            catch (Exception ex) { App.LogError("YouTubeSettings", ex); }
            YtLinks.Text = settings.Links ?? string.Empty;
            YtMode.SelectedIndex = Math.Max(0, Math.Min(1, settings.Mode));
            ytFolder = string.IsNullOrWhiteSpace(settings.Folder)
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Azul Groove YouTube")
                : settings.Folder;
            YtFolder.Text = "Pasta: " + ytFolder;
        }

        void SaveYtSettings()
        {
            if (ytFolder == null) return;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(YtSettingsFile));
                var settings = new YouTubeSettings { Links = YtLinks.Text, Mode = Math.Max(0, YtMode.SelectedIndex), Folder = ytFolder };
                File.WriteAllText(YtSettingsFile, JsonSerializer.Serialize(settings));
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not save YouTube settings: " + ex.Message); }
        }

        void YtFolder_Click(object s, RoutedEventArgs e)
        {
            if (ytBusy) return;
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog() != true) return;
            ytFolder = dialog.FolderName;
            YtFolder.Text = "Pasta: " + ytFolder;
            SaveYtSettings();
            if (watchersOn) RebuildWatchers();
            SyncYtFolderPlaylist();
        }

        void YtCancel_Click(object s, RoutedEventArgs e)
        {
            try { ytCts?.Cancel(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not cancel sync: " + ex.Message); }
        }

        async void YtSync_Click(object s, RoutedEventArgs e)
        {
            if (ytBusy) return;
            var links = ReadYtLinks();
            if (links.Count == 0) { Toast("Cole ao menos um link de playlist (um por linha)"); return; }
            SaveYtSettings();
            int mode = Math.Max(0, YtMode.SelectedIndex);
            SetYtBusy(true);
            ytCts = new CancellationTokenSource();
            var total = Stopwatch.StartNew();
            AppLog.Write("YouTube", "=== SINCRONIZAÇÃO INICIADA | " + links.Count + " link(s) | modo " + (mode == 0 ? "áudio" : "vídeo") + " | pasta: " + ytFolder + " | " + AppLog.Resources());
            try { await RunYtSyncAsync(links, mode, ytCts.Token); AppLog.Write("YouTube", "=== SINCRONIZAÇÃO CONCLUÍDA em " + total.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s | " + AppLog.Resources()); }
            catch (OperationCanceledException) { YtSay("Sincronização cancelada."); AppLog.Write("YouTube", "=== SINCRONIZAÇÃO CANCELADA pelo usuário"); }
            catch (Exception ex)
            {
                App.LogError("YouTube (fase: " + ytPhase + ")", ex);
                YtSay("Não foi possível sincronizar: " + ex.Message);
                YtLogLine("Se o download das ferramentas falhar, coloque yt-dlp.exe, ffmpeg.exe e ffprobe.exe manualmente em: " + ToolsDir);
            }
            finally
            {
                ytCts.Dispose(); ytCts = null;
                SetYtBusy(false);
            }
        }

        List<string> ReadYtLinks()
        {
            return YtLinks.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Botão "Testar link": só consulta a playlist (não baixa nada) e mostra o nome e quantos itens ela tem
        async void YtTest_Click(object s, RoutedEventArgs e)
        {
            if (ytBusy) return;
            var links = ReadYtLinks();
            if (links.Count == 0) { Toast("Cole ao menos um link para testar"); return; }
            SaveYtSettings();
            SetYtBusy(true);
            ytCts = new CancellationTokenSource();
            CancellationToken ct = ytCts.Token;
            YtLog.Clear();
            AppLog.Write("YouTube", "=== TESTE DE LINK | " + links.Count + " link(s)");
            try
            {
                await EnsureYtToolsAsync(ct, false);
                foreach (string url in links)
                {
                    ct.ThrowIfCancellationRequested();
                    YtProgress(-1, "Testando o link...");
                    int count = 0;
                    string title = null;
                    var sample = new List<string>();
                    await RunYtAsync(new[] { "--flat-playlist", "--yes-playlist", "--no-warnings", "--ignore-errors", "--print", "%(playlist_title|)s\t%(id)s\t%(title)s", url },
                        line =>
                        {
                            string[] parts = line.Split('\t', 3);
                            if (parts.Length < 3 || !YtIdRx.IsMatch(parts[1].Trim())) return;
                            count++;
                            if (title == null && parts[0].Trim().Length > 0) title = parts[0].Trim();
                            if (sample.Count < 3) sample.Add(parts[2].Trim());
                        },
                        YtErrLine, ct);
                    if (count == 0)
                        YtLogLine("✗ " + url + " — nenhum vídeo encontrado (link inválido, playlist privada ou sem internet).");
                    else
                    {
                        YtLogLine("✓ " + (title ?? "Vídeo") + ": " + count + " item(ns)");
                        foreach (string name in sample) YtLogLine("    • " + name);
                    }
                }
                YtSay("Teste concluído. Se apareceu ✓, é só clicar em Sincronizar.");
            }
            catch (OperationCanceledException) { YtSay("Teste cancelado."); }
            catch (Exception ex)
            {
                App.LogError("YouTubeTest (fase: " + ytPhase + ")", ex);
                YtSay("Não foi possível testar: " + ex.Message);
            }
            finally
            {
                ytCts.Dispose(); ytCts = null;
                SetYtBusy(false);
            }
        }

        void SetYtBusy(bool busy)
        {
            ytBusy = busy;
            YtTestBtn.IsEnabled = !busy; YtTestBtn.Opacity = busy ? 0.5 : 1;
            YtSyncBtn.IsEnabled = !busy; YtSyncBtn.Opacity = busy ? 0.5 : 1;
            YtCancelBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            YtMode.IsEnabled = !busy;
            YtBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy)
            {
                YtBar.IsIndeterminate = true; YtBar.Value = 0;
                lock (ytLock) { ytBarActive = false; ytStatusPending = null; }
                Interlocked.Exchange(ref ytLastPong, Environment.TickCount64);
                ytLastWarn = ytLastBeat = Environment.TickCount64;
                if (ytUiTimer == null)
                {
                    ytUiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    ytUiTimer.Tick += (sender, args) => YtUiTick();
                }
                ytUiTimer.Start();
                ytWatchdog = new System.Threading.Timer(YtWatchdogTick, null, 1000, 1000);
            }
            else
            {
                ytUiTimer?.Stop();
                ytWatchdog?.Dispose(); ytWatchdog = null;
                while (!ytLogQueue.IsEmpty) FlushYtLog();
                lock (ytLock) { ytBarActive = false; }
                ytPhase = "parado";
            }
        }

        // ---------- Pequenos auxiliares de tela ----------
        void YtSay(string text) { YtStatus.Text = text; }

        // Pode ser chamado de qualquer thread: grava no arquivo na hora e põe na fila da tela (a tela lê a fila 4x por segundo)
        void YtLogLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            AppLog.Write("YouTube", line);
            ytLogQueue.Enqueue(line);
        }

        // Mensagens de erro/aviso que o próprio yt-dlp escreve
        void YtErrLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            AppLog.Write("yt-dlp stderr", line);
            ytLogQueue.Enqueue(line);
        }

        void YtPhase(string phase)
        {
            ytPhase = phase;
            AppLog.Write("YouTube", "Fase: " + phase + " | " + AppLog.Resources());
        }

        // Só na thread da tela: despeja a fila no quadro de log (em lotes, para não travar a interface)
        void FlushYtLog()
        {
            if (ytLogQueue.IsEmpty) return;
            var text = new StringBuilder();
            int taken = 0;
            while (taken < 60 && ytLogQueue.TryDequeue(out string next)) { text.AppendLine(next); taken++; }
            if (YtLog.Text.Length > 20000) YtLog.Text = YtLog.Text.Substring(10000);
            YtLog.AppendText(text.ToString());
            YtLog.ScrollToEnd();
        }

        void YtUiTick()
        {
            Interlocked.Exchange(ref ytLastPong, Environment.TickCount64);
            FlushYtLog();
            string status;
            bool bar;
            lock (ytLock) { status = ytStatusPending; ytStatusPending = null; bar = ytBarActive; }
            if (status != null) YtStatus.Text = status;
            if (bar) UpdateYtBar();
        }

        // Roda em outra thread: se a tela parar de responder, registra no log em que fase estava (e a cada 10 s um "sinal de vida")
        void YtWatchdogTick(object state)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() => Interlocked.Exchange(ref ytLastPong, Environment.TickCount64)));
                long now = Environment.TickCount64;
                long lag = now - Interlocked.Read(ref ytLastPong);
                if (lag > 3000 && now - ytLastWarn > 5000)
                {
                    ytLastWarn = now;
                    AppLog.Write("TRAVADO?", "Interface sem resposta há " + (lag / 1000) + " s | fase: " + ytPhase + " | " + AppLog.Resources());
                }
                if (now - ytLastBeat > 10000)
                {
                    ytLastBeat = now;
                    AppLog.Write("YouTube", "Em andamento | fase: " + ytPhase + " | " + AppLog.Resources());
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Watchdog failed: " + ex.Message); }
        }

        void YtOpenLogs_Click(object s, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AppLog.Dir);
                if (!File.Exists(AppLog.FilePath)) AppLog.Write("YouTube", "Log aberto pelo usuário");
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + AppLog.FilePath + "\"") { UseShellExecute = false });
            }
            catch (Exception ex) { App.LogError("AbrirLogs", ex); Toast("Não foi possível abrir a pasta de logs"); }
        }

        void YtCopyLog_Click(object s, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(AppLog.Tail(30000));
                Toast("Log copiado. É só colar na conversa.");
            }
            catch (Exception ex) { App.LogError("CopiarLog", ex); Toast("Não foi possível copiar. Abra a pasta de logs e envie o arquivo azulgroove.log"); }
        }

        void YtProgress(double percent, string text)
        {
            YtStatus.Text = text;
            if (percent < 0) { YtBar.IsIndeterminate = true; return; }
            YtBar.IsIndeterminate = false; YtBar.Value = Math.Max(0, Math.Min(100, percent));
        }

        void UpdateYtBar()
        {
            int item, total, linkIndex; double pct;
            lock (ytLock) { item = ytItem; total = ytTotal; pct = ytPct; linkIndex = ytLinkIndex; }
            double fraction = total > 0 ? (Math.Max(0, item - 1) + pct / 100.0) / total : 0;
            double overall = (linkIndex + Math.Min(1, fraction)) / Math.Max(1, ytLinkCount) * 100;
            YtBar.IsIndeterminate = false;
            YtBar.Value = Math.Max(0, Math.Min(100, overall));
        }

        // Linhas que o yt-dlp escreve durante o download. Roda em thread de fundo: não mexe na tela,
        // só guarda o estado (a tela lê 4x por segundo) e grava no arquivo de log.
        void ParseYtLine(string line)
        {
            Match item = YtItemRx.Match(line);
            if (item.Success)
            {
                int index = int.Parse(item.Groups[1].Value, CultureInfo.InvariantCulture);
                int total = int.Parse(item.Groups[2].Value, CultureInfo.InvariantCulture);
                lock (ytLock)
                {
                    ytItem = index; ytTotal = total; ytPct = 0; ytBarActive = true;
                    ytStatusPending = "Baixando faixa " + index + " de " + total + "...";
                }
                ytPhase = "baixando faixa " + index + " de " + total + " (playlist " + (ytLinkIndex + 1) + " de " + ytLinkCount + ")";
                AppLog.Write("yt-dlp", line);
                return;
            }
            Match percent = YtPctRx.Match(line);
            if (percent.Success)
            {
                double.TryParse(percent.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value);
                lock (ytLock) { ytPct = value; ytBarActive = true; }
                long now = Environment.TickCount64;
                if (now - ytLastProgLog > 5000) { ytLastProgLog = now; AppLog.Write("yt-dlp", line); }
                return;
            }
            const string destination = "[download] Destination:";
            if (line.StartsWith(destination, StringComparison.Ordinal))
                YtLogLine("▶ " + System.IO.Path.GetFileName(line.Substring(destination.Length).Trim()));
            else
                AppLog.Write("yt-dlp", line);
        }

        // ---------- Ferramentas (yt-dlp e ffmpeg) ----------
        async Task DownloadYtFileAsync(string url, string destination, string label, CancellationToken ct)
        {
            string part = destination + ".part";
            AppLog.Write("YouTube", "Baixando " + url);
            using (var response = await ytHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1;
                using (var source = await response.Content.ReadAsStreamAsync(ct))
                using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long done = 0, last = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                    {
                        await target.WriteAsync(buffer, 0, read, ct);
                        done += read;
                        long now = Environment.TickCount64;
                        if (now - last > 200)
                        {
                            last = now;
                            string size = total > 0 ? " (" + (done >> 20) + " de " + (total >> 20) + " MB)" : " (" + (done >> 20) + " MB)";
                            YtProgress(total > 0 ? done * 100.0 / total : -1, label + size);
                        }
                    }
                }
            }
            File.Move(part, destination, true);
            AppLog.Write("YouTube", "Download concluído: " + System.IO.Path.GetFileName(destination) + " (" + (new FileInfo(destination).Length >> 20) + " MB)");
        }

        async Task EnsureYtToolsAsync(CancellationToken ct, bool needFfmpeg = true)
        {
            YtPhase("preparando ferramentas");
            Directory.CreateDirectory(ToolsDir);
            string ytdlp = System.IO.Path.Combine(ToolsDir, "yt-dlp.exe");
            string ffmpeg = System.IO.Path.Combine(ToolsDir, "ffmpeg.exe");
            string ffprobe = System.IO.Path.Combine(ToolsDir, "ffprobe.exe");
            string stamp = System.IO.Path.Combine(ToolsDir, "last-update.txt");
            bool fresh = false;
            AppLog.Write("YouTube", "Ferramentas em " + ToolsDir + " | yt-dlp: " + (File.Exists(ytdlp) ? "ok" : "falta") + " | ffmpeg: " + (File.Exists(ffmpeg) ? "ok" : "falta") + " | ffprobe: " + (File.Exists(ffprobe) ? "ok" : "falta"));

            if (!File.Exists(ytdlp))
            {
                YtPhase("baixando yt-dlp");
                await DownloadYtFileAsync(YtDlpUrl, ytdlp, "Baixando yt-dlp", ct);
                fresh = true;
                File.WriteAllText(stamp, DateTime.UtcNow.ToString("o"));
            }
            if (needFfmpeg && (!File.Exists(ffmpeg) || !File.Exists(ffprobe)))
            {
                string zipPath = System.IO.Path.Combine(ToolsDir, "ffmpeg.zip");
                YtPhase("baixando ffmpeg");
                await DownloadYtFileAsync(FfmpegUrl, zipPath, "Baixando ffmpeg (só na primeira vez)", ct);
                YtPhase("extraindo ffmpeg");
                YtProgress(-1, "Extraindo ffmpeg...");
                await Task.Run(() =>
                {
                    using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                    {
                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            if (entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || entry.Name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                                entry.ExtractToFile(System.IO.Path.Combine(ToolsDir, entry.Name), true);
                        }
                    }
                }, ct);
                try { File.Delete(zipPath); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not delete ffmpeg.zip: " + ex.Message); }
                if (!File.Exists(ffmpeg)) throw new FileNotFoundException("ffmpeg.exe não foi encontrado no arquivo baixado.");
            }

            // O YouTube muda com frequência e o yt-dlp quebra: atualiza no máximo uma vez por dia
            bool due = !fresh && (!File.Exists(stamp) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp)).TotalHours > 24);
            if (due)
            {
                YtPhase("verificando atualização do yt-dlp");
                YtProgress(-1, "Verificando atualização do yt-dlp...");
                try
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(90));
                        await RunYtAsync(new[] { "-U" }, null, null, timeout.Token);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { System.Diagnostics.Trace.WriteLine("yt-dlp update timed out"); }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { System.Diagnostics.Trace.WriteLine("yt-dlp update failed: " + ex.Message); }
                try { File.WriteAllText(stamp, DateTime.UtcNow.ToString("o")); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not write update stamp: " + ex.Message); }
            }
        }

        async Task<int> RunYtAsync(IEnumerable<string> args, Action<string> onOut, Action<string> onErr, CancellationToken ct)
        {
            var info = new ProcessStartInfo(System.IO.Path.Combine(ToolsDir, "yt-dlp.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = ToolsDir
            };
            var argList = new List<string>(args);
            foreach (string arg in argList) info.ArgumentList.Add(arg);
            info.Environment["PYTHONIOENCODING"] = "utf-8";
            AppLog.Write("yt-dlp", "Executando: yt-dlp " + string.Join(" ", argList.Select(a => a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a)));
            var clock = Stopwatch.StartNew();
            using (var process = new Process { StartInfo = info })
            {
                // Um erro dentro destes eventos, numa thread de fundo, derrubaria o app inteiro: por isso o try/catch
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data == null) return;
                    try { onOut?.Invoke(e.Data); } catch (Exception ex) { App.LogError("yt-dlp saída", ex); }
                };
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data == null) return;
                    try { onErr?.Invoke(e.Data); } catch (Exception ex) { App.LogError("yt-dlp erros", ex); }
                };
                process.Start();
                AppLog.Write("yt-dlp", "Processo iniciado (PID " + process.Id + ")");
                // Prioridade baixa: a conversão de áudio/vídeo usa muita CPU e, sem isso, a interface do app fica pesada ou trava
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (Exception ex) { AppLog.Write("yt-dlp", "Não foi possível baixar a prioridade do processo: " + ex.Message); }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                using (ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not stop yt-dlp: " + ex.Message); } }))
                    await process.WaitForExitAsync();
                AppLog.Write("yt-dlp", "Processo terminou com código " + process.ExitCode + " em " + clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s" + (ct.IsCancellationRequested ? " (cancelado)" : string.Empty));
                ct.ThrowIfCancellationRequested();
                return process.ExitCode;
            }
        }

        // ---------- Sincronização ----------
        async Task RunYtSyncAsync(List<string> links, int mode, CancellationToken ct)
        {
            string modeDir = System.IO.Path.Combine(ytFolder, mode == 0 ? "Audio" : "Video");
            Directory.CreateDirectory(modeDir);
            YtLog.Clear();
            await EnsureYtToolsAsync(ct);
            lock (ytLock) { ytLinkCount = links.Count; }
            for (int i = 0; i < links.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                lock (ytLock) { ytLinkIndex = i; ytItem = 0; ytTotal = 0; ytPct = 0; ytBarActive = false; }
                YtPhase("lendo a playlist " + (i + 1) + " de " + links.Count);
                YtProgress(-1, "Lendo a playlist " + (i + 1) + " de " + links.Count + "...");
                YtLogLine("Playlist " + (i + 1) + ": " + links[i]);
                await SyncYtLinkAsync(links[i], mode, modeDir, ct);
            }
            YtBar.IsIndeterminate = false; YtBar.Value = 100;
            YtSay("Pronto! Tudo sincronizado.");
        }

        async Task SyncYtLinkAsync(string url, int mode, string modeDir, CancellationToken ct)
        {
            // 1) Lista os itens na ordem atual da playlist (sem baixar nada)
            var ids = new List<string>();
            string title = null;
            await RunYtAsync(new[] { "--flat-playlist", "--yes-playlist", "--no-warnings", "--ignore-errors", "--print", "%(playlist_title|)s\t%(id)s", url },
                line =>
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length != 2) return;
                    string id = parts[1].Trim();
                    if (!YtIdRx.IsMatch(id) || ids.Contains(id)) return;
                    if (title == null && parts[0].Trim().Length > 0) title = parts[0].Trim();
                    ids.Add(id);
                },
                YtErrLine, ct);
            if (ids.Count == 0)
            {
                YtLogLine("Nenhum vídeo encontrado (link inválido, playlist privada ou sem internet).");
                return;
            }
            YtLogLine((title ?? "YouTube") + ": " + ids.Count + " itens");
            AppLog.Write("YouTube", "Pasta de destino: " + modeDir);

            YtPhase("baixando playlist (" + ids.Count + " itens)");
            // 2) Baixa só o que ainda não foi baixado (o arquivo "archive" lembra o que já veio)
            string archive = System.IO.Path.Combine(ToolsDir, mode == 0 ? "archive-audio.txt" : "archive-video.txt");
            var args = new List<string>
            {
                "--yes-playlist", "--ignore-errors", "--no-warnings", "--newline", "--no-overwrites", "--continue",
                "--socket-timeout", "30", "--retries", "5", "--fragment-retries", "5",
                "--windows-filenames", "--embed-metadata",
                "--ffmpeg-location", ToolsDir,
                "--download-archive", archive,
                "-o", System.IO.Path.Combine(modeDir, "%(playlist_title|Avulsos)s", "%(title).100s [%(id)s].%(ext)s")
            };
            if (mode == 0)
                args.AddRange(new[] { "-f", "bestaudio/best", "-x", "--audio-format", "mp3", "--audio-quality", "0", "--embed-thumbnail", "--convert-thumbnails", "jpg" });
            else
                args.AddRange(new[] { "-f", "bv*[height<=1080][ext=mp4]+ba[ext=m4a]/b[height<=1080][ext=mp4]/bv*[height<=1080]+ba/b", "--merge-output-format", "mp4" });
            args.Add(url);

            await RunYtAsync(args,
                ParseYtLine,
                YtErrLine, ct);

            // 3) Acha os arquivos pelo [id] no nome, na ordem da playlist
            lock (ytLock) { ytBarActive = false; }
            YtPhase("organizando arquivos baixados");
            YtProgress(-1, "Organizando a playlist...");
            Dictionary<string, string> byId = await Task.Run(() => IndexYtFiles(modeDir), ct);
            var paths = new List<string>();
            foreach (string id in ids)
                if (byId.TryGetValue(id, out string path)) paths.Add(path);
            int missing = ids.Count - paths.Count;
            if (missing > 0) YtLogLine(missing + " item(ns) não baixado(s) (vídeo privado, removido ou indisponível).");
            if (paths.Count == 0) return;

            // 4) Entram na biblioteca e viram (ou atualizam) uma playlist do app
            YtPhase("adicionando " + paths.Count + " arquivos à biblioteca");
            await AddPathsAsync(paths, false);
            AppLog.Write("YouTube", "Biblioteca agora tem " + lib.Count + " itens | " + AppLog.Resources());
            YtPhase("salvando a playlist do app");
            string name = (title ?? "YouTube") + (mode == 1 ? " (vídeo)" : string.Empty);
            SavedPlaylist existing = playlists.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.Files = paths;
            else AddPlaylistSorted(new SavedPlaylist { Name = name, Files = paths });
            SavePlaylists();
            SyncYtFolderPlaylist();   // a pasta inteira também vira uma playlist ("Azul Groove YouTube")
            if (page == 5) RefreshPlaylistTracks();
            YtLogLine("Playlist \"" + name + "\" atualizada com " + paths.Count + " faixas.");
            YtPhase("concluído");
        }

        static Dictionary<string, string> IndexYtFiles(string root)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Directory.Exists(root)) return result;
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!IsSupportedMedia(file)) continue;
                Match match = YtIdInNameRx.Match(System.IO.Path.GetFileName(file));
                if (match.Success) result[match.Groups[1].Value] = file;
            }
            return result;
        }
    }
}
