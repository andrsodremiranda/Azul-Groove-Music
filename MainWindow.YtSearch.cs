using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AzulGroove
{
    // Um resultado da busca no YouTube (aparece acima dos resultados da biblioteca)
    sealed class YtResult : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        string state = string.Empty; bool canDownload = true, canListen = true;
        public string Id { get; set; }
        public string Title { get; set; }
        public string Channel { get; set; }
        public string Duration { get; set; }
        public string Subtitle => string.Join("  ·  ", new[] { Channel, Duration }.Where(x => !string.IsNullOrWhiteSpace(x)));
        public string State { get => state; set { if (state == value) return; state = value; Raise(nameof(State)); } }
        public bool CanDownload { get => canDownload; set { if (canDownload == value) return; canDownload = value; Raise(nameof(CanDownload)); } }
        public bool CanListen { get => canListen; set { if (canListen == value) return; canListen = value; Raise(nameof(CanListen)); } }
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    sealed class YtCacheItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Channel { get; set; }
        public string Duration { get; set; }
    }

    sealed class YtCacheEntry
    {
        public long Ticks { get; set; }
        public List<YtCacheItem> Items { get; set; } = new List<YtCacheItem>();
    }

    // Busca no YouTube pela barra de pesquisa + download para o aparelho (usa o mesmo yt-dlp/ffmpeg da página YouTube)
    public partial class MainWindow
    {
        readonly ObservableCollection<YtResult> ytResults = new ObservableCollection<YtResult>();
        readonly SemaphoreSlim ytDlLock = new SemaphoreSlim(1, 1);
        readonly CancellationTokenSource ytDlCts = new CancellationTokenSource();
        DispatcherTimer ytSearchTimer;
        CancellationTokenSource ytSearchCts;
        string ytSearchLast;

        // ---------- Cache das buscas (memória + disco, 24 h, até 40 buscas) ----------
        static readonly string YtSearchCacheFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove", "youtube-search-cache.json");
        static readonly TimeSpan YtCacheTtl = TimeSpan.FromHours(24);
        const int YtCacheMax = 40;
        Dictionary<string, YtCacheEntry> ytCache;

        static string YtKey(string q) => Regex.Replace(q.Trim().ToLowerInvariant(), @"\s+", " ");

        void LoadYtCache()
        {
            if (ytCache != null) return;
            try
            {
                if (File.Exists(YtSearchCacheFile))
                    ytCache = JsonSerializer.Deserialize<Dictionary<string, YtCacheEntry>>(File.ReadAllText(YtSearchCacheFile));
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not load YouTube search cache: " + ex.Message); }
            ytCache = ytCache ?? new Dictionary<string, YtCacheEntry>();
            long limit = (DateTime.UtcNow - YtCacheTtl).Ticks;
            foreach (string k in ytCache.Where(kv => kv.Value == null || kv.Value.Ticks < limit || kv.Value.Items == null || kv.Value.Items.Count == 0).Select(kv => kv.Key).ToList())
                ytCache.Remove(k);
        }

        bool TryYtCache(string q, out List<YtCacheItem> items)
        {
            items = null;
            LoadYtCache();
            if (!ytCache.TryGetValue(YtKey(q), out YtCacheEntry entry)) return false;
            if (entry.Ticks < (DateTime.UtcNow - YtCacheTtl).Ticks) { ytCache.Remove(YtKey(q)); return false; }
            items = entry.Items;
            return true;
        }

        void StoreYtCache(string q, List<YtResult> found)
        {
            if (found.Count == 0) return;
            LoadYtCache();
            ytCache[YtKey(q)] = new YtCacheEntry
            {
                Ticks = DateTime.UtcNow.Ticks,
                Items = found.Select(r => new YtCacheItem { Id = r.Id, Title = r.Title, Channel = r.Channel, Duration = r.Duration }).ToList()
            };
            while (ytCache.Count > YtCacheMax)
            {
                string oldest = ytCache.OrderBy(kv => kv.Value.Ticks).First().Key;
                ytCache.Remove(oldest);
            }
            string json = JsonSerializer.Serialize(ytCache);
            _ = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(YtSearchCacheFile));
                    string temp = YtSearchCacheFile + ".tmp";
                    File.WriteAllText(temp, json);
                    File.Move(temp, YtSearchCacheFile, true);
                }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not save YouTube search cache: " + ex.Message); }
            });
        }

        void PrefYtCacheClear_Click(object s, RoutedEventArgs e)
        {
            ytCache = new Dictionary<string, YtCacheEntry>();
            ytSearchLast = null;
            try { if (File.Exists(YtSearchCacheFile)) File.Delete(YtSearchCacheFile); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not delete YouTube search cache: " + ex.Message); }
            Toast("Cache da busca limpo");
        }

        void InitYtSearch()
        {
            YtResults.ItemsSource = ytResults;
        }

        void YtSearchCancel()
        {
            try { ytSearchCts?.Cancel(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not cancel YouTube search: " + ex.Message); }
        }

        // Chamado quando o texto da busca muda (ou quando a opção é ligada/desligada)
        void YtSearchSchedule()
        {
            if (YtSearchPanel == null) return;
            string q = Search.Text.Trim();
            if (!prefs.YtSearch || q.Length < 2)
            {
                YtSearchCancel();
                ytSearchTimer?.Stop();
                YtSearchPanel.Visibility = Visibility.Collapsed;
                ytResults.Clear();
                ytSearchLast = null;
                return;
            }
            if (ytSearchTimer == null)
            {
                ytSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };   // espera parar de digitar
                ytSearchTimer.Tick += async (a, b) => { ytSearchTimer.Stop(); await YtSearchRunAsync(); };
            }
            YtSearchPanel.Visibility = Visibility.Visible;
            if (q == ytSearchLast) return;
            if (TryYtCache(q, out List<YtCacheItem> cached))   // já buscou isso antes: aparece na hora, sem rodar o yt-dlp
            {
                YtSearchCancel();
                ytSearchTimer.Stop();
                ytResults.Clear();
                foreach (var c in cached) ytResults.Add(new YtResult { Id = c.Id, Title = c.Title, Channel = c.Channel, Duration = c.Duration });
                ytSearchLast = q;
                YtSearchStatus.Text = ytResults.Count + " resultados (salvos)";
                YtPrefetch(ytResults.Select(x => x.Id).ToList());
                return;
            }
            YtSearchStatus.Text = "Buscando no YouTube...";
            ytSearchTimer.Stop(); ytSearchTimer.Start();
        }

        async Task YtSearchRunAsync()
        {
            string q = Search.Text.Trim();
            if (!prefs.YtSearch || q.Length < 2 || q == ytSearchLast) return;
            if (ytBusy) { YtSearchStatus.Text = "Aguarde a sincronização do YouTube terminar"; return; }
            YtSearchCancel();
            var cts = ytSearchCts = CancellationTokenSource.CreateLinkedTokenSource(ytDlCts.Token);
            CancellationToken ct = cts.Token;
            YtSearchStatus.Text = "Buscando no YouTube...";
            try
            {
                string ytdlp = System.IO.Path.Combine(ToolsDir, "yt-dlp.exe");
                if (!File.Exists(ytdlp))   // só prepara as ferramentas na primeira vez (a atualização diária fica para o download)
                {
                    YtSearchStatus.Text = "Preparando o YouTube pela primeira vez (pode demorar)...";
                    await EnsureYtToolsAsync(ct, false);
                    ct.ThrowIfCancellationRequested();
                    YtSearchStatus.Text = "Buscando no YouTube...";
                }

                var found = new List<YtResult>();
                var seen = new HashSet<string>();
                bool first = true;   // só mexida na thread da tela
                var args = new[]
                {
                    "ytsearch10:" + q, "--flat-playlist", "--no-warnings", "--ignore-errors", "--skip-download", "--ignore-config",
                    "--socket-timeout", "10", "--extractor-retries", "1",
                    "--print", "%(id)s\t%(title)s\t%(channel,uploader|)s\t%(duration_string|)s"
                };
                await RunYtAsync(args, line =>
                {
                    string[] p = line.Split('\t');
                    if (p.Length < 2) return;
                    string id = p[0].Trim();
                    if (!YtIdRx.IsMatch(id)) return;
                    var r = new YtResult
                    {
                        Id = id,
                        Title = p[1].Trim(),
                        Channel = p.Length > 2 ? p[2].Trim() : string.Empty,
                        Duration = p.Length > 3 && p[3].Trim() != "NA" ? p[3].Trim() : string.Empty
                    };
                    lock (found) { if (!seen.Add(id)) return; found.Add(r); }
                    UI(() =>   // cada resultado aparece assim que o yt-dlp o entrega
                    {
                        if (ct.IsCancellationRequested || Search.Text.Trim() != q) return;
                        if (first) { first = false; ytResults.Clear(); }
                        ytResults.Add(r);
                        YtSearchStatus.Text = ytResults.Count + " resultados";
                    });
                }, YtErrLine, ct);
                ct.ThrowIfCancellationRequested();
                if (Search.Text.Trim() != q) return;   // o texto mudou durante a busca

                List<YtResult> final;
                lock (found) { final = found.ToList(); }
                if (ytResults.Count != final.Count || !ytResults.Select(x => x.Id).SequenceEqual(final.Select(x => x.Id)))
                {
                    ytResults.Clear();
                    foreach (var r in final) ytResults.Add(r);
                }
                ytSearchLast = q;
                StoreYtCache(q, final);
                YtSearchStatus.Text = final.Count == 0 ? "Nada encontrado no YouTube" : final.Count + " resultados";
                YtPrefetch(final.Select(x => x.Id));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.LogError("Busca no YouTube", ex);
                if (!ct.IsCancellationRequested) YtSearchStatus.Text = "Não foi possível buscar no YouTube (sem internet? tente atualizar pela página YouTube)";
            }
        }

        void YtResultAudio_Click(object s, RoutedEventArgs e) => _ = YtDownloadResultAsync((s as FrameworkElement)?.DataContext as YtResult, 0);
        void YtResultVideo_Click(object s, RoutedEventArgs e) => _ = YtDownloadResultAsync((s as FrameworkElement)?.DataContext as YtResult, 1);

        async Task YtDownloadResultAsync(YtResult r, int mode)
        {
            if (r == null || !r.CanDownload) return;
            if (ytBusy) { Toast("Aguarde a sincronização do YouTube terminar"); return; }
            r.CanDownload = false; r.State = "Na fila...";
            CancellationToken ct = ytDlCts.Token;
            await ytDlLock.WaitAsync();
            try
            {
                ct.ThrowIfCancellationRequested();
                r.State = "Preparando...";
                string modeDir = System.IO.Path.Combine(ytFolder, mode == 0 ? "Audio" : "Video");
                Directory.CreateDirectory(modeDir);
                await EnsureYtToolsAsync(ct);

                var args = new List<string>
                {
                    "--no-playlist", "--ignore-errors", "--no-warnings", "--newline", "--no-overwrites", "--continue",
                    "--socket-timeout", "30", "--retries", "5", "--fragment-retries", "5",
                    "--windows-filenames", "--embed-metadata",
                    "--ffmpeg-location", ToolsDir,
                    "-o", System.IO.Path.Combine(modeDir, "Avulsos", "%(title).100s [%(id)s].%(ext)s")
                };
                if (mode == 0)
                    args.AddRange(new[] { "-f", "bestaudio/best", "-x", "--audio-format", "mp3", "--audio-quality", "0", "--embed-thumbnail", "--convert-thumbnails", "jpg" });
                else
                    args.AddRange(new[] { "-f", "bv*[height<=1080][ext=mp4]+ba[ext=m4a]/b[height<=1080][ext=mp4]/bv*[height<=1080]+ba/b", "--merge-output-format", "mp4" });
                args.Add("https://www.youtube.com/watch?v=" + r.Id);

                int lastPct = -1;
                r.State = "Baixando...";
                await RunYtAsync(args, line =>
                {
                    Match m = YtPctRx.Match(line);
                    if (m.Success)
                    {
                        int pct = (int)double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        if (pct != lastPct) { lastPct = pct; UI(() => r.State = "Baixando " + pct + "%"); }
                    }
                    else if (line.Contains("[ExtractAudio]") || line.Contains("[Merger]") || line.Contains("[Metadata]"))
                        UI(() => r.State = "Convertendo...");
                }, YtErrLine, ct);

                Dictionary<string, string> byId = await Task.Run(() => IndexYtFiles(modeDir), ct);
                if (byId.TryGetValue(r.Id, out string path))
                {
                    await AddPathsAsync(new[] { path }, false);
                    SyncYtFolderPlaylist();
                    r.State = "Baixada ✓";
                    Toast("Baixada: " + r.Title);
                    if (itemsByPath.TryGetValue(path, out Item downloaded))
                        AutoEnqueue(new[] { downloaded }, "Baixada e no player: " + r.Title);
                }
                else
                {
                    r.State = "Falhou";
                    r.CanDownload = true;
                    Toast("Não foi possível baixar este vídeo");
                }
            }
            catch (OperationCanceledException) { r.State = "Cancelada"; r.CanDownload = true; }
            catch (Exception ex)
            {
                App.LogError("Download do YouTube", ex);
                r.State = "Falhou"; r.CanDownload = true;
            }
            finally { ytDlLock.Release(); }
        }
    }
}
