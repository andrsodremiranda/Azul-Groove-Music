using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AzulGroove
{
    sealed class SmartState
    {
        public Dictionary<string, int> Skips { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // faixa -> vezes que foi pulada logo no começo
        public List<string> Seen { get; set; } = new List<string>();                                                          // ids do YouTube já sugeridos (nunca repete)
    }

    // Fila inteligente: a cada troca de música o app completa a fila sozinho, aprendendo com o seu uso
    // (quanto você ouve cada faixa/artista, quão recente, em que hora do dia, o que você pula).
    // Opcional: de vez em quando baixa 1 música nova parecida (mix do YouTube) e coloca na fila.
    public partial class MainWindow
    {
        static readonly string SmartFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove", "smartqueue.json");
        const int SmartMaxFiles = 60;                          // limite de músicas baixadas automaticamente
        static readonly TimeSpan SmartFetchEvery = TimeSpan.FromMinutes(2);   // intervalo mínimo entre downloads
        const int SmartMaxPending = 3;                         // não baixa mais se já há 3 baixadas que você ainda não ouviu
        static readonly Regex SmartBadTitle = new Regex(@"full album|playlist|compilation|\bhours?\b|reaction|tutorial|karaoke|podcast|audiobook|nightcore|slowed|sped up|8d audio|\bmix\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        SmartState smart;
        readonly Random smartRnd = new Random();
        bool smartFetching;
        DateTime smartLastFetch = DateTime.MinValue;

        void LoadSmart()
        {
            if (smart != null) return;
            try { if (File.Exists(SmartFile)) smart = JsonSerializer.Deserialize<SmartState>(File.ReadAllText(SmartFile)); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not load smart queue state: " + ex.Message); }
            smart = smart ?? new SmartState();
            smart.Skips = new Dictionary<string, int>(smart.Skips ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
            smart.Seen = smart.Seen ?? new List<string>();
        }

        void SaveSmart()
        {
            if (smart == null) return;
            while (smart.Seen.Count > 2000) smart.Seen.RemoveAt(0);
            string json = JsonSerializer.Serialize(smart);
            _ = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SmartFile));
                    string temp = SmartFile + ".tmp";
                    File.WriteAllText(temp, json);
                    File.Move(temp, SmartFile, true);
                }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not save smart queue state: " + ex.Message); }
            });
        }

        // Pular uma faixa antes de ela contar como reprodução (30 s) é sinal de que você não quis ouvir aquilo
        void SmartNoteSkip()
        {
            if (!prefs.SmartQueue || cur == null || !ReferenceEquals(countItem, cur)) return;
            LoadSmart();
            smart.Skips.TryGetValue(cur.FilePath, out int n);
            smart.Skips[cur.FilePath] = n + 1;
            SaveSmart();
        }

        static double SmartDecay(long ticks)   // meia-vida de 30 dias: o que você ouviu semana passada pesa mais que o de meses atrás
        {
            double days = Math.Max(0, (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerDay);
            return Math.Pow(0.5, days / 30.0);
        }

        // Chamado a cada troca de faixa: mantém a fila com pelo menos "SmartAhead" músicas escolhidas pelo seu gosto
        void SmartQueueFill()
        {
            try
            {
                if (!prefs.SmartQueue || cur == null || _mp == null || repeat == 2) return;
                LoadSmart();
                int target = Math.Max(1, Math.Min(10, prefs.SmartAhead));
                int guard = 0;
                bool added = false;
                while (queue.Count < target && guard++ < 12)
                {
                    Item pick = SmartPick();
                    if (pick == null) break;
                    queue.Add(pick); added = true;
                }
                if (added) RefreshQueue();
                if (prefs.SmartDownload) SmartMaybeFetch();
            }
            catch (Exception ex) { App.LogError("Fila inteligente", ex); }
        }

        Item SmartPick()
        {
            HashSet<Item> recentSet = new HashSet<Item>(recent.Take(15));
            Func<Item, bool> ok = i => !i.IsVideo && !i.IsStream && !ReferenceEquals(i, cur) && !queue.Contains(i);
            List<Item> cand = lib.Where(i => ok(i) && !recentSet.Contains(i)).ToList();
            if (cand.Count == 0) cand = lib.Where(ok).ToList();   // biblioteca pequena: deixa repetir
            if (cand.Count == 0) return null;

            // afinidade por artista (reproduções com peso maior para as recentes) e artistas que você mais pula
            var artist = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var artistSkips = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Item it in lib)
            {
                if (string.IsNullOrWhiteSpace(it.Artist)) continue;
                if (plays.TryGetValue(it.FilePath, out PlayStat st) && st.Count > 0)
                {
                    artist.TryGetValue(it.Artist, out double a);
                    artist[it.Artist] = a + st.Count * SmartDecay(st.LastTicks);
                }
                if (smart.Skips.TryGetValue(it.FilePath, out int sk))
                {
                    artistSkips.TryGetValue(it.Artist, out int b);
                    artistSkips[it.Artist] = b + sk;
                }
            }
            double maxArtist = artist.Count > 0 ? artist.Values.Max() : 1;
            if (maxArtist <= 0) maxArtist = 1;
            int hour = DateTime.Now.Hour;

            var scored = new List<KeyValuePair<Item, double>>(cand.Count);
            foreach (Item it in cand)
            {
                double s = 0;
                if (plays.TryGetValue(it.FilePath, out PlayStat st) && st.Count > 0)
                {
                    s += 1.2 * Math.Log(1 + st.Count) * (0.4 + 0.6 * SmartDecay(st.LastTicks));   // o que você já gosta
                    if (st.Hours != null && st.Hours.Length == 24)                                  // e costuma ouvir nesta hora do dia
                    {
                        int total = st.Hours.Sum();
                        if (total > 0) s += 2.0 * (st.Hours[hour] + 0.5 * (st.Hours[(hour + 23) % 24] + st.Hours[(hour + 1) % 24])) / total;
                    }
                }
                else
                {
                    s += 0.6;                                                  // nunca tocada: chance de ser descoberta
                    if (it.FileDate > DateTime.Now.AddDays(-14)) s += 0.4;     // e as novas na biblioteca ganham um empurrão
                }
                if (!string.IsNullOrWhiteSpace(it.Artist))
                {
                    if (artist.TryGetValue(it.Artist, out double aff)) s += 1.5 * aff / maxArtist;
                    if (artistSkips.TryGetValue(it.Artist, out int ask)) s -= 0.4 * ask;
                    if (cur != null && string.Equals(it.Artist, cur.Artist, StringComparison.OrdinalIgnoreCase)) s += 1.5;   // combina com a que está tocando
                }
                if (cur != null && !string.IsNullOrWhiteSpace(it.Album) && string.Equals(it.Album, cur.Album, StringComparison.OrdinalIgnoreCase)) s += 0.8;
                if (smart.Skips.TryGetValue(it.FilePath, out int skips)) s -= 1.5 * skips;      // você pulou: some por um tempo
                s += smartRnd.NextDouble() * 0.8;                                                // um pouco de acaso para não ficar repetitivo
                scored.Add(new KeyValuePair<Item, double>(it, s));
            }

            // sorteia entre as 6 melhores, com mais chance para as de nota maior
            var top = scored.OrderByDescending(p => p.Value).Take(6).ToList();
            double best = top[0].Value;
            double sum = top.Sum(p => Math.Exp(p.Value - best));
            double roll = smartRnd.NextDouble() * sum;
            foreach (var p in top)
            {
                roll -= Math.Exp(p.Value - best);
                if (roll <= 0) return p.Key;
            }
            return top[0].Key;
        }

        // ---------- Opcional: baixar 1 música nova parecida com a que está tocando ----------
        void SmartMaybeFetch()
        {
            if (smartFetching || ytBusy || ytDlLock.CurrentCount == 0) return;
            if (DateTime.UtcNow - smartLastFetch < SmartFetchEvery) return;
            if (cur == null || string.IsNullOrWhiteSpace(ytFolder)) return;
            // já tem músicas baixadas automaticamente esperando para serem ouvidas? Então não baixa mais por enquanto
            string prefix = System.IO.Path.Combine(ytFolder, "Audio", "Automatico") + System.IO.Path.DirectorySeparatorChar;
            int pending = lib.Count(i => i.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !(plays.TryGetValue(i.FilePath, out PlayStat st) && st.Count > 0));
            if (pending >= SmartMaxPending) return;
            Item seed = SmartPickSeed();
            if (seed == null) return;
            _ = SmartFetchAsync(seed);
        }

        // Faixa "semente" para pedir recomendações: metade das vezes a que está tocando, senão uma das que você mais ouve
        Item SmartPickSeed()
        {
            var liked = new List<KeyValuePair<Item, double>>();
            foreach (Item it in lib)
            {
                if (it.IsVideo || it.IsStream || !plays.TryGetValue(it.FilePath, out PlayStat st) || st.Count == 0) continue;
                if (smart.Skips.TryGetValue(it.FilePath, out int sk) && sk > 0) continue;
                liked.Add(new KeyValuePair<Item, double>(it, st.Count * SmartDecay(st.LastTicks)));
            }
            Item playing = cur != null && !cur.IsStream && !cur.IsVideo ? cur : null;
            if (liked.Count == 0 || smartRnd.NextDouble() < 0.5) return playing ?? (liked.Count > 0 ? liked[0].Key : null);
            var top = liked.OrderByDescending(p => p.Value).Take(10).ToList();
            double roll = smartRnd.NextDouble() * top.Sum(p => p.Value);
            foreach (var p in top) { roll -= p.Value; if (roll <= 0) return p.Key; }
            return top[0].Key;
        }

        async Task SmartFetchAsync(Item seed)
        {
            smartFetching = true; smartLastFetch = DateTime.UtcNow;
            CancellationToken ct = ytDlCts.Token;
            bool locked = false;
            try
            {
                string dir = System.IO.Path.Combine(ytFolder, "Audio", "Automatico");
                if (Directory.Exists(dir) && Directory.EnumerateFiles(dir).Count() >= SmartMaxFiles) return;   // limite de espaço
                await ytDlLock.WaitAsync(ct); locked = true;
                if (ytBusy) return;
                UI(() => Toast("Procurando música nova pelo seu gosto…"));
                await EnsureYtToolsAsync(ct);
                LoadSmart();

                // 1) id do YouTube da faixa de origem (se foi baixada de lá, já está no nome; senão busca)
                string seedId = null;
                Match m = YtIdInNameRx.Match(System.IO.Path.GetFileName(seed.FilePath));
                if (m.Success) seedId = m.Groups[1].Value;
                if (seedId == null)
                {
                    string q = (seed.Artist + " " + seed.Name).Trim();
                    string found = null;
                    await RunYtAsync(new[] { "ytsearch1:" + q, "--flat-playlist", "--no-warnings", "--ignore-errors", "--skip-download", "--ignore-config", "--socket-timeout", "10", "--print", "%(id)s" },
                        line => { if (found == null && YtIdRx.IsMatch(line.Trim())) found = line.Trim(); }, YtErrLine, ct);
                    seedId = found;
                }
                if (seedId == null) return;

                // 2) "Mix" do YouTube feito a partir dessa faixa = recomendações parecidas
                var cands = new List<string[]>();
                await RunYtAsync(new[]
                {
                    "https://www.youtube.com/watch?v=" + seedId + "&list=RD" + seedId, "--flat-playlist", "--yes-playlist", "--playlist-end", "25",
                    "--no-warnings", "--ignore-errors", "--skip-download", "--ignore-config", "--socket-timeout", "15",
                    "--print", "%(id)s\t%(title)s\t%(channel,uploader|)s\t%(duration|0)s"
                }, line => { string[] p = line.Split('\t'); if (p.Length >= 2) lock (cands) cands.Add(p); }, YtErrLine, ct);

                // 3) filtra: nada que já está na biblioteca, já foi sugerido, ou que não parece uma música
                var have = new HashSet<string>(StringComparer.Ordinal);
                var haveNames = new List<string>();
                foreach (Item it in lib)
                {
                    Match lm = YtIdInNameRx.Match(System.IO.Path.GetFileName(it.FilePath));
                    if (lm.Success) have.Add(lm.Groups[1].Value);
                    string n = SmartNorm(it.Name);
                    if (n.Length >= 6) haveNames.Add(n);
                }
                var seen = new HashSet<string>(smart.Seen, StringComparer.Ordinal);
                List<string[]> valid;
                lock (cands)
                {
                    valid = cands.Where(p =>
                    {
                        string id = p[0].Trim();
                        if (!YtIdRx.IsMatch(id) || id == seedId || have.Contains(id) || seen.Contains(id)) return false;
                        if (SmartBadTitle.IsMatch(p[1])) return false;
                        if (p.Length > 3 && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double dur) && dur > 0 && (dur < 60 || dur > 600)) return false;
                        string tn = SmartNorm(p[1]);
                        return !haveNames.Any(hn => tn.Contains(hn));
                    }).ToList();
                }
                if (valid.Count == 0) return;
                string[] chosen = valid[smartRnd.Next(Math.Min(5, valid.Count))];   // o começo do mix é o que o YouTube mais recomenda
                string chosenId = chosen[0].Trim();
                smart.Seen.Add(chosenId); SaveSmart();

                // 4) baixa em MP3 e coloca na fila
                Directory.CreateDirectory(dir);
                var args = new List<string>
                {
                    "--no-playlist", "--ignore-errors", "--no-warnings", "--newline", "--no-overwrites", "--continue",
                    "--socket-timeout", "30", "--retries", "5", "--fragment-retries", "5",
                    "--windows-filenames", "--embed-metadata", "--ffmpeg-location", ToolsDir,
                    "-o", System.IO.Path.Combine(dir, "%(title).100s [%(id)s].%(ext)s"),
                    "-f", "bestaudio/best", "-x", "--audio-format", "mp3", "--audio-quality", "0", "--embed-thumbnail", "--convert-thumbnails", "jpg",
                    "https://www.youtube.com/watch?v=" + chosenId
                };
                await RunYtAsync(args, null, YtErrLine, ct);
                Dictionary<string, string> byId = await Task.Run(() => IndexYtFiles(dir), ct);
                if (!byId.TryGetValue(chosenId, out string path)) return;
                await AddPathsAsync(new[] { path }, false);
                SyncYtFolderPlaylist();
                RefreshSmartPlaylists();   // atualiza a playlist "★ Músicas automáticas"
                if (itemsByPath.TryGetValue(path, out Item added) && !queue.Contains(added))
                {
                    queue.Insert(0, added); RefreshQueue();   // vai tocar logo em seguida
                    Notify("Azul Groove Music", "Música nova baixada pelo seu gosto: " + added.Name);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { App.LogError("Fila inteligente (download)", ex); }
            finally
            {
                if (locked) ytDlLock.Release();
                smartFetching = false;
            }
        }

        // Aviso: com o app em uso aparece o toast dentro da janela; com o app minimizado, na bandeja ou
        // com você em outro programa, aparece como notificação do Windows (canto da tela)
        bool balloonHooked;
        void Notify(string title, string text)
        {
            try
            {
                bool background = !IsVisible || WindowState == WindowState.Minimized || !IsActive;
                if (!background) { Toast(text); return; }
                EnsureTray();
                if (!balloonHooked) { trayIcon.BalloonTipClicked += (a, b) => RestoreFromTray(); balloonHooked = true; }
                bool wasVisible = trayIcon.Visible;
                trayIcon.Visible = true;
                trayIcon.ShowBalloonTip(6000, title, Clip(text, 120), System.Windows.Forms.ToolTipIcon.Info);
                if (!wasVisible)   // janela aberta: esconde o ícone da bandeja de novo depois que o aviso sumir
                {
                    var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
                    t.Tick += (a, b) => { t.Stop(); if (trayIcon != null && IsVisible && WindowState != WindowState.Minimized) trayIcon.Visible = false; };
                    t.Start();
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Notify failed: " + ex.Message); Toast(text); }
        }

        static string SmartNorm(string s) => Regex.Replace((s ?? string.Empty).ToLowerInvariant(), @"[^\p{L}\p{N}]+", string.Empty);

        // ---------- Configurações ----------
        static readonly int[] SmartAheadSteps = { 1, 2, 3, 5 };

        void UpdateSmartButton()
        {
            if (PlAutoBtn != null) PlAutoBtn.Content = "Música automática: " + (prefs.SmartQueue ? "ligada" : "desligada");
            if (PrefSmart != null) PrefSmart.IsChecked = prefs.SmartQueue;
        }

        // Botão da página Playlists: liga/desliga a música automática (fila inteligente + downloads)
        void PlAuto_Click(object s, RoutedEventArgs e)
        {
            prefs.SmartQueue = !prefs.SmartQueue;
            prefs.Save();
            UpdateSmartButton();
            RefreshSmartPlaylists();
            if (prefs.SmartQueue) SmartQueueFill();
            Toast(prefs.SmartQueue ? "Música automática ligada" : "Música automática desligada");
        }

        void PrefSmart_Click(object s, RoutedEventArgs e)
        {
            prefs.SmartQueue = ((System.Windows.Controls.CheckBox)s).IsChecked == true;
            prefs.Save();
            UpdateSmartButton();
            RefreshSmartPlaylists();
            if (prefs.SmartQueue) SmartQueueFill();
        }

        void PrefSmartDl_Click(object s, RoutedEventArgs e)
        {
            prefs.SmartDownload = ((System.Windows.Controls.CheckBox)s).IsChecked == true;
            prefs.Save();
            if (prefs.SmartDownload) { Toast("Baixando músicas novas pelo seu gosto"); SmartQueueFill(); }
        }

        void PrefSmartAhead_Click(object s, RoutedEventArgs e)
        {
            int i = Array.IndexOf(SmartAheadSteps, prefs.SmartAhead);
            prefs.SmartAhead = SmartAheadSteps[(i + 1) % SmartAheadSteps.Length];
            prefs.Save();
            PrefSmartAheadBtn.Content = "Músicas à frente: " + prefs.SmartAhead;
            SmartQueueFill();
        }
    }
}
