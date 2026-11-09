using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AzulGroove
{
    sealed class YtStreamHit
    {
        public string Url { get; set; }
        public DateTime At { get; set; }
    }

    sealed class PlayStat
    {
        public int Count { get; set; }
        public long LastTicks { get; set; }
        public int[] Hours { get; set; }   // quantas vezes tocou em cada hora do dia (0-23)
    }

    // Contagem de reproduções, playlists automáticas, exportar .m3u, editor de tags/capa e "Ouvir" do YouTube sem baixar
    public partial class MainWindow
    {
        // ---------- Contagem de reproduções (conta quando a faixa passa de 30 s, ou da metade se for curta) ----------
        static readonly string PlaysFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove", "playcounts.json");
        Dictionary<string, PlayStat> plays = new Dictionary<string, PlayStat>(StringComparer.OrdinalIgnoreCase);
        Item countItem;   // faixa "armada" para contar (vira null depois de contada)

        void LoadPlays()
        {
            try
            {
                if (File.Exists(PlaysFile))
                {
                    var stored = JsonSerializer.Deserialize<Dictionary<string, PlayStat>>(File.ReadAllText(PlaysFile));
                    if (stored != null) plays = new Dictionary<string, PlayStat>(stored, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not load play counts: " + ex.Message); }
        }

        void ApplyPlayCounts()
        {
            foreach (Item it in lib)
                if (plays.TryGetValue(it.FilePath, out PlayStat st) && st.Count > 0) it.SetPlays(st.Count);
        }

        long PlayCountAfterMs(Item it)
        {
            long half = (long)(it.Duration.TotalMilliseconds / 2);
            return half > 0 ? Math.Min(30000, half) : 30000;
        }

        void CountPlay(Item it)
        {
            if (it == null || it.IsStream) return;
            if (!plays.TryGetValue(it.FilePath, out PlayStat st)) plays[it.FilePath] = st = new PlayStat();
            st.Count++; st.LastTicks = DateTime.UtcNow.Ticks;
            if (st.Hours == null || st.Hours.Length != 24) st.Hours = new int[24];
            st.Hours[DateTime.Now.Hour]++;
            it.SetPlays(st.Count);
            string json = JsonSerializer.Serialize(plays);
            _ = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PlaysFile));
                    string temp = PlaysFile + ".tmp";
                    File.WriteAllText(temp, json);
                    File.Move(temp, PlaysFile, true);
                }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not save play counts: " + ex.Message); }
            });
        }

        // Chamado pelo evento de tempo do VLC (fora da thread da tela)
        void OnPlayTime(long timeMs)
        {
            Item ci = countItem;
            if (ci == null || timeMs < PlayCountAfterMs(ci)) return;
            if (Interlocked.CompareExchange(ref countItem, null, ci) == ci) UI(() => CountPlay(ci));
        }

        // ---------- Playlists automáticas: "Mais tocadas" e "Adicionadas recentemente" ----------
        void RefreshSmartPlaylists()
        {
            try
            {
                List<string> top = lib
                    .Where(i => !i.IsVideo && plays.TryGetValue(i.FilePath, out PlayStat st) && st.Count > 0)
                    .OrderByDescending(i => plays[i.FilePath].Count).ThenByDescending(i => plays[i.FilePath].LastTicks)
                    .Take(50).Select(i => i.FilePath).ToList();
                List<string> recentAdded = lib
                    .Where(i => !i.IsVideo)
                    .OrderByDescending(i => i.FileDate)
                    .Take(50).Select(i => i.FilePath).ToList();
                long monthAgo = (DateTime.UtcNow - TimeSpan.FromDays(30)).Ticks;
                List<string> lastPlayed = lib
                    .Where(i => !i.IsVideo && plays.TryGetValue(i.FilePath, out PlayStat st) && st.Count > 0)
                    .OrderByDescending(i => plays[i.FilePath].LastTicks)
                    .Take(50).Select(i => i.FilePath).ToList();
                List<string> forgotten = lib   // já tocou pelo menos 2 vezes, mas não toca há mais de 30 dias
                    .Where(i => !i.IsVideo && plays.TryGetValue(i.FilePath, out PlayStat st) && st.Count >= 2 && st.LastTicks < monthAgo)
                    .OrderByDescending(i => plays[i.FilePath].Count)
                    .Take(50).Select(i => i.FilePath).ToList();
                List<string> never = lib
                    .Where(i => !i.IsVideo && !(plays.TryGetValue(i.FilePath, out PlayStat st) && st.Count > 0))
                    .OrderByDescending(i => i.FileDate)
                    .Take(50).Select(i => i.FilePath).ToList();
                UpsertSmart("top", "★ Mais tocadas", top, 0);
                UpsertSmart("played", "★ Tocadas recentemente", lastPlayed, 1);
                UpsertSmart("recent", "★ Adicionadas recentemente", recentAdded, 2);
                UpsertSmart("forgotten", "★ Esquecidas (30+ dias)", forgotten, 3);
                UpsertSmart("never", "★ Nunca tocadas", never, 4);
                List<string> auto = new List<string>();   // músicas baixadas sozinhas pela fila inteligente
                if (!string.IsNullOrWhiteSpace(ytFolder))
                {
                    string autoPrefix = System.IO.Path.Combine(ytFolder, "Audio", "Automatico") + System.IO.Path.DirectorySeparatorChar;
                    auto = lib.Where(i => i.FilePath.StartsWith(autoPrefix, StringComparison.OrdinalIgnoreCase))
                              .OrderByDescending(i => i.FileDate).Select(i => i.FilePath).ToList();
                }
                UpsertSmart("auto", "★ Músicas automáticas", auto, 5, prefs.SmartQueue);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not refresh smart playlists: " + ex.Message); }
        }

        void UpsertSmart(string kind, string name, List<string> files, int position, bool allowEmpty = false)
        {
            SavedPlaylist pl = playlists.FirstOrDefault(p => p.Smart == kind);
            if (pl == null)
            {
                if (files.Count == 0 && !allowEmpty) return;
                AddPlaylistSorted(new SavedPlaylist { Name = name, Auto = true, Smart = kind, Files = files });   // as ★ ficam por último
                return;
            }
            if (pl.Files != null && pl.Files.SequenceEqual(files, StringComparer.OrdinalIgnoreCase)) return;
            pl.Files = files;
            if (page == 5 && ReferenceEquals(PlaylistList.SelectedItem, pl)) RefreshPlaylistTracks();
        }

        // ---------- Exportar playlist como .m3u (backup) ----------
        void ExportPlaylist_Click(object s, RoutedEventArgs e) => ExportPlaylist(PlaylistList.SelectedItem as SavedPlaylist);
        void ExportPlaylistMenu_Click(object s, RoutedEventArgs e) => ExportPlaylist(PlaylistOf(s));

        void ExportPlaylist(SavedPlaylist pl)
        {
            if (pl == null) { Toast("Selecione uma playlist para exportar"); return; }
            List<string> files = (pl.Files ?? new List<string>()).Where(File.Exists).ToList();
            if (files.Count == 0) { Toast("Esta playlist não tem arquivos para exportar"); return; }
            string safe = string.Concat(pl.Name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim('★', ' ');
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Exportar playlist",
                Filter = "Playlist M3U8 (UTF-8)|*.m3u8|Playlist M3U|*.m3u",
                DefaultExt = ".m3u8",
                FileName = safe.Length > 0 ? safe : "playlist"
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var sb = new StringBuilder("#EXTM3U\r\n");
                foreach (string path in files)
                {
                    if (itemsByPath.TryGetValue(path, out Item it))
                    {
                        int secs = it.Duration > TimeSpan.Zero ? (int)Math.Round(it.Duration.TotalSeconds) : -1;
                        string label = string.IsNullOrWhiteSpace(it.Artist) ? it.Name : it.Artist + " - " + it.Name;
                        sb.Append("#EXTINF:").Append(secs).Append(',').Append(label.Replace('\r', ' ').Replace('\n', ' ')).Append("\r\n");
                    }
                    sb.Append(path).Append("\r\n");
                }
                File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(false));
                Toast("Playlist exportada (" + files.Count + " faixas)");
            }
            catch (Exception ex)
            {
                App.LogError("Exportar playlist", ex);
                Toast("Não foi possível exportar a playlist");
            }
        }

        // ---------- Editor de informações e capa (botão direito na música → "Editar informações…") ----------
        void EditTags_Click(object s, RoutedEventArgs e) => EditTags(MenuItemOf(s));

        void EditTags(Item it)
        {
            if (it == null) return;
            if (it.IsVideo || it.IsStream) { Toast("Só dá para editar músicas do computador"); return; }
            if (!File.Exists(it.FilePath)) { Toast("Arquivo não encontrado"); return; }

            string title, artist, album, genre, year;
            try
            {
                using (var f = TagLib.File.Create(it.FilePath))
                {
                    title = f.Tag.Title ?? string.Empty;
                    artist = f.Tag.Performers?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
                    album = f.Tag.Album ?? string.Empty;
                    genre = f.Tag.Genres?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
                    year = f.Tag.Year > 0 ? f.Tag.Year.ToString() : string.Empty;
                }
            }
            catch (Exception ex)
            {
                App.LogError("Ler tags", ex);
                Toast("Não foi possível ler as informações deste arquivo");
                return;
            }

            var dialog = new Window
            {
                Title = "Editar informações",
                Owner = this,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = (System.Windows.Media.Brush)FindResource("Abyss"),
                Foreground = (System.Windows.Media.Brush)FindResource("Foam")
            };
            var layout = new StackPanel { Margin = new Thickness(18) };
            TextBox Row(string label, string value)
            {
                layout.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
                var box = new TextBox { Padding = new Thickness(7), Text = value };
                layout.Children.Add(box);
                return box;
            }
            TextBox tTitle = Row("Título", title), tArtist = Row("Artista", artist), tAlbum = Row("Álbum", album), tGenre = Row("Gênero", genre), tYear = Row("Ano", year);

            string coverPath = null;
            var coverInfo = new TextBlock { Text = "Capa: manter a atual", Margin = new Thickness(0, 12, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis };
            layout.Children.Add(coverInfo);
            var coverRow = new StackPanel { Orientation = Orientation.Horizontal };
            var pick = new Button { Content = "Escolher imagem…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 10, 0) };
            var remove = new CheckBox { Content = "Remover capa", VerticalAlignment = VerticalAlignment.Center, Foreground = dialog.Foreground };
            pick.Click += (a, b) =>
            {
                var open = new Microsoft.Win32.OpenFileDialog { Title = "Escolher capa", Filter = "Imagens (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png" };
                if (open.ShowDialog(dialog) != true) return;
                coverPath = open.FileName; remove.IsChecked = false;
                coverInfo.Text = "Capa nova: " + System.IO.Path.GetFileName(coverPath);
            };
            remove.Checked += (a, b) => { coverPath = null; coverInfo.Text = "Capa: será removida"; };
            remove.Unchecked += (a, b) => { if (coverPath == null) coverInfo.Text = "Capa: manter a atual"; };
            coverRow.Children.Add(pick); coverRow.Children.Add(remove);
            layout.Children.Add(coverRow);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var save = new Button { Content = "Salvar", IsDefault = true, MinWidth = 76, Margin = new Thickness(0, 0, 8, 0) };
            save.Click += (a, b) => dialog.DialogResult = true;
            actions.Children.Add(save);
            actions.Children.Add(new Button { Content = "Cancelar", IsCancel = true, MinWidth = 76 });
            layout.Children.Add(actions);
            dialog.Content = layout;
            dialog.Loaded += (a, b) => { tTitle.Focus(); tTitle.SelectAll(); };
            if (dialog.ShowDialog() != true) return;

            DateTime mtime = File.GetLastWriteTimeUtc(it.FilePath);
            try
            {
                using (var f = TagLib.File.Create(it.FilePath))
                {
                    f.Tag.Title = tTitle.Text.Trim();
                    f.Tag.Performers = tArtist.Text.Trim().Length > 0 ? new[] { tArtist.Text.Trim() } : new string[0];
                    f.Tag.Album = tAlbum.Text.Trim();
                    f.Tag.Genres = tGenre.Text.Trim().Length > 0 ? new[] { tGenre.Text.Trim() } : new string[0];
                    f.Tag.Year = uint.TryParse(tYear.Text.Trim(), out uint y) && y <= 9999 ? y : 0;
                    if (remove.IsChecked == true) f.Tag.Pictures = new TagLib.IPicture[0];
                    else if (coverPath != null)
                    {
                        string ext = System.IO.Path.GetExtension(coverPath).ToLowerInvariant();
                        var pic = new TagLib.Picture(new TagLib.ByteVector(File.ReadAllBytes(coverPath)))
                        {
                            Type = TagLib.PictureType.FrontCover,
                            MimeType = ext == ".png" ? "image/png" : "image/jpeg",
                            Description = "Cover"
                        };
                        f.Tag.Pictures = new TagLib.IPicture[] { pic };
                    }
                    f.Save();
                }
                try { File.SetLastWriteTimeUtc(it.FilePath, mtime); }   // mantém a data original (não vira "adicionada recentemente")
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not restore file date: " + ex.Message); }
            }
            catch (IOException)
            {
                Toast("Arquivo em uso: pare a música e tente de novo");
                return;
            }
            catch (Exception ex)
            {
                App.LogError("Salvar tags", ex);
                Toast("Não foi possível salvar as informações");
                return;
            }

            it.CopyFrom(new Item(it.FilePath, false));
            Save();
            Refresh();
            if (page == 7 || page == 8) RefreshGroups();
            RefreshQueue(); RefreshPlaylistTracks();
            if (ReferenceEquals(cur, it)) UpdateNow();
            Toast("Informações salvas");
        }

        // ---------- Ouvir do YouTube sem baixar ----------
        readonly Dictionary<string, YtStreamHit> ytStreamCache = new Dictionary<string, YtStreamHit>();
        int ytPrefetchGen;

        // Pré-carregamento: depois de uma busca, resolve em segundo plano o endereço de áudio dos 3 primeiros resultados (um por vez).
        // Quando o usuário clica em "Ouvir", o endereço já está pronto e a música toca quase na hora.
        async void YtPrefetch(IEnumerable<string> ids)
        {
            int gen = ++ytPrefetchGen;
            try
            {
                if (!File.Exists(System.IO.Path.Combine(ToolsDir, "yt-dlp.exe"))) return;
                CancellationToken ct = ytDlCts.Token;
                foreach (string id in ids.Take(3).ToList())
                {
                    if (gen != ytPrefetchGen || ytBusy || ct.IsCancellationRequested) return;   // nova busca, sincronização ou app fechando
                    lock (ytStreamCache)
                        if (ytStreamCache.TryGetValue(id, out YtStreamHit hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(90)) continue;
                    string url = null;
                    var args = new[] { "-f", "bestaudio[ext=m4a]/bestaudio/best", "-g", "--no-playlist", "--no-warnings", "--no-mark-watched", "--ignore-config", "--socket-timeout", "10", "--extractor-retries", "1", "https://www.youtube.com/watch?v=" + id };
                    await RunYtAsync(args, line =>
                    {
                        if (url == null && line.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = line.Trim();
                    }, null, ct);
                    if (!string.IsNullOrEmpty(url))
                        lock (ytStreamCache) ytStreamCache[id] = new YtStreamHit { Url = url, At = DateTime.UtcNow };
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("YouTube prefetch failed: " + ex.Message); }
        }
        void YtResultListen_Click(object s, RoutedEventArgs e) => _ = YtListenAsync((s as FrameworkElement)?.DataContext as YtResult);

        static TimeSpan ParseYtDuration(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return TimeSpan.Zero;
            string[] parts = text.Trim().Split(':');
            long total = 0;
            foreach (string p in parts)
            {
                if (!long.TryParse(p, out long n)) return TimeSpan.Zero;
                total = total * 60 + n;
            }
            return TimeSpan.FromSeconds(total);
        }

        async Task YtListenAsync(YtResult r)
        {
            if (r == null || !r.CanListen) return;
            if (_mp == null) { Toast("O player ainda está carregando"); return; }
            if (ytBusy) { Toast("Aguarde a sincronização do YouTube terminar"); return; }
            r.CanListen = false; r.State = "Carregando...";
            try
            {
                CancellationToken ct = ytDlCts.Token;
                if (!File.Exists(System.IO.Path.Combine(ToolsDir, "yt-dlp.exe"))) await EnsureYtToolsAsync(ct, false);

                string direct = null;
                string watch = "https://www.youtube.com/watch?v=" + r.Id;
                // O endereço do áudio vale por horas: ouvir de novo a mesma faixa toca na hora, sem rodar o yt-dlp (que leva vários segundos)
                lock (ytStreamCache)
                    if (ytStreamCache.TryGetValue(r.Id, out YtStreamHit hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(90)) direct = hit.Url;
                if (direct == null)
                {
                    r.State = "Buscando o áudio...";
                    var args = new[] { "-f", "bestaudio[ext=m4a]/bestaudio/best", "-g", "--no-playlist", "--no-warnings", "--no-mark-watched", "--ignore-config", "--socket-timeout", "10", "--extractor-retries", "1", watch };
                    await RunYtAsync(args, line =>
                    {
                        if (direct == null && line.StartsWith("http", StringComparison.OrdinalIgnoreCase)) direct = line.Trim();
                    }, YtErrLine, ct);
                    if (!string.IsNullOrEmpty(direct))
                        lock (ytStreamCache) ytStreamCache[r.Id] = new YtStreamHit { Url = direct, At = DateTime.UtcNow };
                }
                if (string.IsNullOrEmpty(direct))
                {
                    r.State = "Não foi possível tocar";
                    Toast("Não foi possível tocar este vídeo (o yt-dlp pode precisar de atualização)");
                    return;
                }
                PlayStream(new Item(watch, false, r.Title, r.Channel, "YouTube", ParseYtDuration(r.Duration), DateTime.Now), direct);
                r.State = "Tocando ▶";
            }
            catch (OperationCanceledException) { r.State = string.Empty; }
            catch (Exception ex)
            {
                App.LogError("Ouvir do YouTube", ex);
                r.State = "Falhou";
            }
            finally { r.CanListen = true; }
        }

        // Toca um endereço direto (stream) sem passar pela biblioteca
        void PlayStream(Item it, string url)
        {
            if (_mp == null) return;
            if (cur != null) cur.IsPlaying = false;
            cur = it; it.IsPlaying = true;
            countItem = null;
            pendingSeek = -1;
            ChipRes.Visibility = Visibility.Collapsed;
            UpdateNow();
            Volatile.Write(ref curTime, 0); Volatile.Write(ref curLen, (long)it.Duration.TotalMilliseconds);
            StartMedia(url, true);
        }
    }
}
