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
        public List<string> Blocked { get; set; } = new List<string>();                                                       // faixas que você pediu para nunca sugerir
        public List<string> BlockedArtists { get; set; } = new List<string>();                                                // artistas que você pediu para nunca sugerir
    }

    // Fila inteligente: a cada troca de música o app completa a fila sozinho, aprendendo com o seu uso
    // (quanto você ouve cada faixa/artista, quão recente, em que hora do dia, o que você pula).
    // Opcional: de vez em quando baixa 1 música nova parecida (mix do YouTube) e coloca na fila.
    public partial class MainWindow
    {
        static readonly string SmartFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove", "smartqueue.json");
        static readonly TimeSpan SmartFetchEvery = TimeSpan.FromMinutes(StartupBoost.IsLowEnd ? 10 : 5);   // intervalo mínimo entre downloads
        const int SmartMaxPending = 3;                         // não baixa mais se já há 3 baixadas que você ainda não ouviu
        static readonly Regex SmartBadTitle = new Regex(@"full album|playlist|compilation|\bhours?\b|reaction|tutorial|karaoke|podcast|audiobook|nightcore|slowed|sped up|8d audio|\bmix\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        SmartState smart;
        readonly HashSet<string> smartBlockTracks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> smartBlockArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            smart.Blocked = smart.Blocked ?? new List<string>();
            smart.BlockedArtists = smart.BlockedArtists ?? new List<string>();
            foreach (string p in smart.Blocked) smartBlockTracks.Add(p);
            foreach (string a in smart.BlockedArtists) smartBlockArtists.Add(a);
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
                SmartCtx ctx = queue.Count < target ? SmartBuildCtx() : null;   // fila já cheia: nem gasta tempo calculando
                while (queue.Count < target && guard++ < 12)
                {
                    Item pick = SmartPick(ctx);
                    if (pick == null) break;
                    queue.Add(pick); added = true;
                }
                if (added) RefreshQueue();
                if (prefs.SmartDownload) SmartMaybeFetch();
            }
            catch (Exception ex) { App.LogError("Fila inteligente", ex); }
        }

        sealed class SmartCtx
        {
            public List<Item> Cand;
            public readonly Dictionary<string, double> Artist = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> ArtistSkips = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public double MaxArtist = 1;
            public int Hour;
        }

        // Cada posição é uma "razão" que soma nota; as duas maiores da faixa escolhida aparecem ao passar o mouse na fila
        static readonly string[] SmartReasons =
        {
            "você ouve bastante", "costuma ouvir nesta hora", "ainda não ouvida", "nova na biblioteca",
            "artista que você curte", "mesmo artista da faixa atual", "mesmo álbum da faixa atual", "está nas favoritas", "combina com os gostos que você marcou"
        };

        bool SmartBlocked(Item i) => smartBlockTracks.Contains(i.FilePath) || (!string.IsNullOrEmpty(i.Artist) && smartBlockArtists.Contains(i.Artist));

        // Tudo que não muda entre uma escolha e outra é calculado uma vez por preenchimento da fila
        // (antes a biblioteca inteira era varrida de novo a cada música escolhida, e mesmo com a fila já cheia)
        SmartCtx SmartBuildCtx()
        {
            var ctx = new SmartCtx { Hour = DateTime.Now.Hour };
            var recentSet = new HashSet<Item>(recent.Take(15));
            var inQueue = new HashSet<Item>(queue);
            bool Ok(Item i) => !i.IsVideo && !i.IsStream && !ReferenceEquals(i, cur) && !inQueue.Contains(i) && !SmartBlocked(i);
            ctx.Cand = lib.Where(i => Ok(i) && !recentSet.Contains(i)).ToList();
            if (ctx.Cand.Count == 0) ctx.Cand = lib.Where(Ok).ToList();   // biblioteca pequena: deixa repetir

            // afinidade por artista (reproduções com peso maior para as recentes) e artistas que você mais pula
            foreach (Item it in lib)
            {
                if (string.IsNullOrWhiteSpace(it.Artist)) continue;
                if (plays.TryGetValue(it.FilePath, out PlayStat st) && st.Count > 0)
                {
                    ctx.Artist.TryGetValue(it.Artist, out double a);
                    ctx.Artist[it.Artist] = a + st.Count * SmartDecay(st.LastTicks);
                }
                if (smart.Skips.TryGetValue(it.FilePath, out int sk))
                {
                    ctx.ArtistSkips.TryGetValue(it.Artist, out int b);
                    ctx.ArtistSkips[it.Artist] = b + sk;
                }
            }
            ctx.MaxArtist = ctx.Artist.Count > 0 ? ctx.Artist.Values.Max() : 1;
            if (ctx.MaxArtist <= 0) ctx.MaxArtist = 1;
            return ctx;
        }

        // Nota da faixa (sem o sorteio). Se "parts" vier preenchido, guarda quanto cada razão contribuiu.
        double SmartScore(Item it, SmartCtx ctx, double[] parts)
        {
            double s = 0;
            void Add(int reason, double v) { s += v; if (parts != null) parts[reason] = v; }
            if (plays.TryGetValue(it.FilePath, out PlayStat st) && st.Count > 0)
            {
                Add(0, 1.2 * Math.Log(1 + st.Count) * (0.4 + 0.6 * SmartDecay(st.LastTicks)));   // o que você já gosta
                if (st.Hours != null && st.Hours.Length == 24)                                    // e costuma ouvir nesta hora do dia
                {
                    int total = st.Hours.Sum();
                    int hour = ctx.Hour;
                    if (total > 0) Add(1, 2.0 * (st.Hours[hour] + 0.5 * (st.Hours[(hour + 23) % 24] + st.Hours[(hour + 1) % 24])) / total);
                }
            }
            else
            {
                Add(2, 0.6 * TasteNoveltyWeight);                                                          // nunca tocada: chance de ser descoberta
                if (it.FileDate > DateTime.Now.AddDays(-14)) Add(3, 0.4 * TasteNoveltyWeight);             // e as novas na biblioteca ganham um empurrão
            }
            if (!string.IsNullOrWhiteSpace(it.Artist))
            {
                if (ctx.Artist.TryGetValue(it.Artist, out double aff)) Add(4, 1.5 * aff / ctx.MaxArtist);
                if (ctx.ArtistSkips.TryGetValue(it.Artist, out int ask)) s -= 0.4 * ask;
                if (cur != null && string.Equals(it.Artist, cur.Artist, StringComparison.OrdinalIgnoreCase)) Add(5, 1.5);   // combina com a que está tocando
            }
            if (cur != null && !string.IsNullOrWhiteSpace(it.Album) && string.Equals(it.Album, cur.Album, StringComparison.OrdinalIgnoreCase)) Add(6, 0.8);
            if (smart.Skips.TryGetValue(it.FilePath, out int skips)) s -= 1.5 * skips;   // você pulou: some por um tempo
            if (favs.Contains(it.FilePath)) Add(7, 0.8);                                  // favoritas (♥) têm preferência
            double taste = TasteBonus(it, ctx.Artist.Count >= 5);                        // o que a pessoa marcou no assistente de gostos
            if (taste > 0) Add(8, taste);
            return s;
        }

        string SmartWhy(Item it, SmartCtx ctx)
        {
            var parts = new double[SmartReasons.Length];
            SmartScore(it, ctx, parts);
            List<string> labels = Enumerable.Range(0, parts.Length).Where(i => parts[i] >= 0.3).OrderByDescending(i => parts[i]).Take(2).Select(i => SmartReasons[i]).ToList();
            return labels.Count == 0 ? "Sorteada para variar a fila" : "Na fila porque: " + string.Join(" · ", labels);
        }

        Item SmartPick(SmartCtx ctx)
        {
            if (ctx == null || ctx.Cand.Count == 0) return null;
            // guarda só as 6 melhores (uma passada, sem ordenar a biblioteca inteira)
            const int N = 6;
            var topItem = new Item[N]; var topScore = new double[N]; int count = 0;
            foreach (Item it in ctx.Cand)
            {
                double sc = SmartScore(it, ctx, null) + smartRnd.NextDouble() * 0.8;   // um pouco de acaso para não ficar repetitivo
                if (count == N && sc <= topScore[N - 1]) continue;
                int pos = count < N ? count++ : N - 1;
                while (pos > 0 && topScore[pos - 1] < sc) { topScore[pos] = topScore[pos - 1]; topItem[pos] = topItem[pos - 1]; pos--; }
                topScore[pos] = sc; topItem[pos] = it;
            }
            // sorteia entre as melhores, com mais chance para as de nota maior
            double best = topScore[0], sum = 0;
            for (int i = 0; i < count; i++) sum += Math.Exp(topScore[i] - best);
            double roll = smartRnd.NextDouble() * sum;
            Item chosen = topItem[0];
            for (int i = 0; i < count; i++)
            {
                roll -= Math.Exp(topScore[i] - best);
                if (roll <= 0) { chosen = topItem[i]; break; }
            }
            ctx.Cand.Remove(chosen);
            chosen.Reason = SmartWhy(chosen, ctx);
            return chosen;
        }

        // ---------- Opcional: baixar 1 música nova parecida com a que está tocando ----------
        void SmartMaybeFetch()
        {
            if (smartFetching || smartBatchRunning || ytBusy || ytDlLock.CurrentCount == 0) return;
            if (DateTime.UtcNow - smartLastFetch < SmartFetchEvery) return;
            if (cur == null || string.IsNullOrWhiteSpace(ytFolder)) return;
            if (cur.IsVideo) return;   // assistindo vídeo: nada de download/conversão em segundo plano
            try { if (System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline) return; }   // notebook na bateria: não baixa
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not read power status: " + ex.Message); }
            // já tem músicas baixadas automaticamente esperando para serem ouvidas? Então não baixa mais por enquanto
            string prefix = System.IO.Path.Combine(ytFolder, "Audio", "Automatico") + System.IO.Path.DirectorySeparatorChar;
            int pending = lib.Count(i => i.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !(plays.TryGetValue(i.FilePath, out PlayStat st) && st.Count > 0));
            if (pending >= SmartMaxPending) return;
            Item seed = SmartPickSeed();
            if (seed == null && TasteSeedQuery() == null) return;   // sem histórico: usa os gostos marcados como ponto de partida
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

        async Task<bool> SmartFetchAsync(Item seed, bool batch = false)
        {
            smartFetching = true; smartLastFetch = DateTime.UtcNow;
            CancellationToken ct = ytDlCts.Token;
            bool locked = false;
            try
            {
                string dir = System.IO.Path.Combine(ytFolder, "Audio", "Automatico");
                if (Directory.Exists(dir) && Directory.EnumerateFiles(dir).Count() >= prefs.SmartMaxFiles) return false;   // limite de espaço
                await ytDlLock.WaitAsync(ct); locked = true;
                if (ytBusy) return false;
                if (!batch) UI(() => Toast("Procurando música nova pelo seu gosto…"));
                await EnsureYtToolsAsync(ct);
                LoadSmart();

                // 1) id do YouTube da faixa de origem (se foi baixada de lá, já está no nome; senão busca)
                string seedId = null;
                Match m = seed != null ? YtIdInNameRx.Match(System.IO.Path.GetFileName(seed.FilePath)) : Match.Empty;
                if (m.Success) seedId = m.Groups[1].Value;
                if (seedId == null)
                {
                    string q = seed != null ? (seed.Artist + " " + seed.Name).Trim() : TasteSeedQuery();
                    string found = null;
                    await RunYtAsync(new[] { "ytsearch1:" + q, "--flat-playlist", "--no-warnings", "--ignore-errors", "--skip-download", "--ignore-config", "--socket-timeout", "10", "--print", "%(id)s" },
                        line => { if (found == null && YtIdRx.IsMatch(line.Trim())) found = line.Trim(); }, YtErrLine, ct);
                    seedId = found;
                }
                if (seedId == null) return false;

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
                Item[] libSnap = lib.ToArray();
                await Task.Run(() =>   // biblioteca grande: esse laço travava a tela por um instante a cada download
                {
                    foreach (Item it in libSnap)
                    {
                        Match lm = YtIdInNameRx.Match(System.IO.Path.GetFileName(it.FilePath));
                        if (lm.Success) have.Add(lm.Groups[1].Value);
                        string n = SmartNorm(it.Name);
                        if (n.Length >= 6) haveNames.Add(n);
                    }
                }, ct);
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
                if (valid.Count == 0) return false;
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
                if (!byId.TryGetValue(chosenId, out string path)) return false;
                await AddPathsAsync(new[] { path }, false);
                SyncYtFolderPlaylist();
                RefreshSmartPlaylists();   // atualiza a playlist "★ Músicas automáticas"
                if (itemsByPath.TryGetValue(path, out Item added))
                {
                    if (batch) added.Reason = "Baixada em lote pelo seu gosto (mix de recomendações do YouTube)";   // o lote não mexe na fila nem avisa música por música
                    else if (!queue.Contains(added))
                    {
                        added.Reason = "Baixada agora pelo seu gosto (mix de recomendações do YouTube)";
                        queue.Insert(0, added); RefreshQueue();   // vai tocar logo em seguida
                        Notify("Azul Groove Music", "Música nova baixada pelo seu gosto: " + added.Name);
                    }
                }
                return true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { App.LogError("Fila inteligente (download)", ex); }
            finally
            {
                if (locked) ytDlLock.Release();
                smartFetching = false;
            }
            return false;
        }

        // ---------- Lote de hora em hora ----------
        bool smartBatchRunning;
        static readonly int[] SmartBatchSteps = { 10, 25, 50, 100 };
        static readonly TimeSpan SmartBatchEvery = TimeSpan.FromHours(1);

        // Fica de olho: a cada 2 min vê se já passou 1 hora desde o último lote (o horário fica salvo, reabrir o app não repete)
        async Task SmartBatchLoopAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(StartupBoost.IsLowEnd ? 90 : 45));
                while (!ytDlCts.IsCancellationRequested)
                {
                    if (SmartBatchDue()) await SmartBatchAsync();
                    await Task.Delay(TimeSpan.FromMinutes(2));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { App.LogError("Fila inteligente (lote)", ex); }
        }

        bool SmartBatchDue()
        {
            if (!prefs.SmartBatch || !prefs.SmartDownload || !prefs.SmartQueue || string.IsNullOrWhiteSpace(ytFolder)) return false;
            if (smartBatchRunning || smartFetching || ytBusy || ytDlLock.CurrentCount == 0) return false;
            if (prefs.SmartBatchLast > 0 && DateTime.UtcNow - new DateTime(prefs.SmartBatchLast, DateTimeKind.Utc) < SmartBatchEvery) return false;
            return SmartBackgroundOk();
        }

        // Sem vídeo tocando e sem notebook na bateria
        bool SmartBackgroundOk()
        {
            if (cur != null && cur.IsVideo) return false;
            try { if (System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline) return false; }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not read power status: " + ex.Message); }
            return true;
        }

        async Task SmartBatchAsync()
        {
            if (smartBatchRunning) return;
            smartBatchRunning = true;
            int target = Math.Max(1, prefs.SmartBatchCount), done = 0, fails = 0;
            try
            {
                prefs.SmartBatchLast = DateTime.UtcNow.Ticks; prefs.Save();
                string dir = System.IO.Path.Combine(ytFolder, "Audio", "Automatico");
                int have = Directory.Exists(dir) ? Directory.EnumerateFiles(dir).Count() : 0;
                if (have >= prefs.SmartMaxFiles)
                {
                    Notify("Azul Groove Music", "Lote não baixado: limite de músicas automáticas atingido (aumente em Configurações → Fila inteligente)");
                    return;
                }
                UI(() => Toast("Baixando um lote de músicas novas…"));
                AppLog.Write("Fila inteligente", "Lote de " + target + " músicas iniciado");
                while (done < target && fails < 3 && prefs.SmartBatch && prefs.SmartDownload && !ytDlCts.IsCancellationRequested)
                {
                    if (!SmartBackgroundOk()) break;
                    if (Directory.Exists(dir) && Directory.EnumerateFiles(dir).Count() >= prefs.SmartMaxFiles) break;
                    if (ytBusy) { await Task.Delay(TimeSpan.FromSeconds(20)); fails++; continue; }   // sincronização manual em andamento: espera um pouco
                    if (await SmartFetchAsync(SmartPickSeed(), true)) { done++; fails = 0; }
                    else fails++;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { App.LogError("Fila inteligente (lote)", ex); }
            finally
            {
                smartBatchRunning = false;
                AppLog.Write("Fila inteligente", "Lote terminou: " + done + " de " + target);
                if (done > 0)
                {
                    try { SmartQueueFill(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Smart fill after batch failed: " + ex.Message); }
                    Notify("Azul Groove Music", T.Get("Lote concluído: {0} músicas novas", done));
                }
            }
        }

        // Aviso: com o app em uso aparece o toast dentro da janela; com o app minimizado, na bandeja ou
        // com você em outro programa, aparece como notificação do Windows (canto da tela)
        bool balloonHooked;
        void Notify(string title, string text)
        {
            title = T.Get(title); text = T.Get(text);
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
            if (PlAutoBtn != null) PlAutoBtn.Content = T.Get(prefs.SmartQueue ? "Música automática: ligada" : "Música automática: desligada");
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
            PrefSmartAheadBtn.Content = T.Get("Músicas à frente: {0}", prefs.SmartAhead);
            SmartQueueFill();
        }

        void PrefSmartBatch_Click(object s, RoutedEventArgs e)
        {
            prefs.SmartBatch = ((System.Windows.Controls.CheckBox)s).IsChecked == true;
            if (prefs.SmartBatch) { prefs.SmartBatchLast = 0; if (!prefs.SmartDownload) Toast("Ligue também \"Baixar músicas novas do YouTube\" para o lote funcionar"); else Toast("Lote ligado: o primeiro começa em instantes e depois de hora em hora"); }
            prefs.Save();
        }

        void PrefSmartBatchCount_Click(object s, RoutedEventArgs e)
        {
            int i = Array.IndexOf(SmartBatchSteps, prefs.SmartBatchCount);
            prefs.SmartBatchCount = SmartBatchSteps[(i + 1) % SmartBatchSteps.Length];
            prefs.Save();
            PrefSmartBatchBtn.Content = T.Get("Músicas por lote: {0}", prefs.SmartBatchCount);
        }
    }
}
