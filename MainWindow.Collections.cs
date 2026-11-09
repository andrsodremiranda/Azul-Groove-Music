using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AzulGroove
{
    // Linha das páginas Artistas / Álbuns
    sealed class GroupEntry
    {
        public string Name { get; set; }
        public string Subtitle { get; set; }
        public List<Item> Items { get; set; }
        public CornerRadius ArtRadius { get; set; }
        // A capa só é carregada quando a linha aparece na tela
        public Brush Art => Items[0].Art;
    }

    sealed class LibraryDiff
    {
        public List<Item> Missing = new List<Item>();
        public List<KeyValuePair<Item, Item>> Changed = new List<KeyValuePair<Item, Item>>();
        public List<Item> Added = new List<Item>();
    }

    public partial class MainWindow
    {
        const string UnknownArtist = "Artista desconhecido";
        const string YtFolderPlaylistName = "Azul Groove YouTube";
        static readonly string FoldersFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove", "folders.json");
        // Arquivos temporários do yt-dlp (ex.: "título [id].f137.mp4") não entram na biblioteca
        static readonly Regex YtTempRx = new Regex(@"\.f\d{2,4}\.[A-Za-z0-9]+$|\.temp\.[A-Za-z0-9]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        readonly List<string> libraryRoots = new List<string>();
        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        DispatcherTimer watchTimer;
        bool watchersOn;
        int groupKind;          // 0 = nenhum, 1 = artista, 2 = álbum (lista de faixas filtrada na página de músicas)
        string groupName;

        // ---------- Pastas da biblioteca ----------
        void LoadRoots()
        {
            try
            {
                if (!File.Exists(FoldersFile)) return;
                var stored = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FoldersFile));
                if (stored == null) return;
                foreach (string root in stored)
                    if (!string.IsNullOrWhiteSpace(root) && !libraryRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) libraryRoots.Add(root);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not load library folders: " + ex.Message); }
        }

        void SaveRoots()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FoldersFile));
                string temp = FoldersFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(libraryRoots));
                File.Move(temp, FoldersFile, true);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not save library folders: " + ex.Message); }
        }

        static bool IsInside(string path, string root)
        {
            string r = System.IO.Path.TrimEndingDirectorySeparator(root);
            return path.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(r + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        // Toda pasta aberta/arrastada vira uma "pasta da biblioteca": é nela que se procura arquivo novo
        void RegisterRoots(IEnumerable<string> paths)
        {
            bool changed = false;
            foreach (string p in paths)
            {
                if (string.IsNullOrWhiteSpace(p) || !Directory.Exists(p)) continue;
                string full;
                try { full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(p)); }
                catch (Exception) { continue; }
                if (libraryRoots.Any(root => IsInside(full, root))) continue;
                libraryRoots.RemoveAll(root => IsInside(root, full));
                libraryRoots.Add(full);
                changed = true;
            }
            if (!changed) return;
            SaveRoots();
            if (watchersOn) RebuildWatchers();
        }

        // Pastas da biblioteca + a pasta do YouTube (sem repetir pastas contidas em outras)
        List<string> ScanRoots()
        {
            var all = new List<string>(libraryRoots);
            if (!string.IsNullOrWhiteSpace(ytFolder)) all.Add(ytFolder);
            var result = new List<string>();
            foreach (string root in all.Where(Directory.Exists))
            {
                string full;
                try { full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root)); }
                catch (Exception) { continue; }
                if (result.Any(existing => IsInside(full, existing))) continue;
                result.RemoveAll(existing => IsInside(existing, full));
                result.Add(full);
            }
            return result;
        }

        // ---------- Atualizar biblioteca ----------
        static LibraryDiff CheckLibrary(Item[] snapshot, HashSet<string> knownSnap, List<string> roots, IProgress<ScanProgress> progress)
        {
            var diff = new LibraryDiff();
            foreach (Item it in snapshot)
            {
                try
                {
                    if (!File.Exists(it.FilePath))
                    {
                        // só some da biblioteca se a unidade está presente (HD externo desconectado não apaga nada)
                        string root = System.IO.Path.GetPathRoot(it.FilePath);
                        if (!string.IsNullOrEmpty(root) && Directory.Exists(root)) diff.Missing.Add(it);
                        continue;
                    }
                    if (File.GetLastWriteTimeUtc(it.FilePath) != it.FileDate)
                        diff.Changed.Add(new KeyValuePair<Item, Item>(it, new Item(it.FilePath, it.IsVideo)));
                }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not check " + it.FilePath + ": " + ex.Message); }
            }
            if (roots.Count > 0)
            {
                List<string> files = DiscoverFiles(roots, progress);
                var fresh = files.Where(f => !knownSnap.Contains(f) && !YtTempRx.IsMatch(f)).ToList();
                if (fresh.Count > 0) diff.Added = ReadItems(fresh, progress);
            }
            return diff;
        }

        async void RefreshLibrary_Click(object s, RoutedEventArgs e)
        {
            try { await RefreshLibraryAsync(false); }
            catch (Exception ex) { App.LogError("LibraryRefresh", ex); Toast("Não foi possível atualizar a biblioteca: " + ex.Message); }
        }

        async Task RefreshLibraryAsync(bool silent, bool autoQueue = false)
        {
            await scanGate.WaitAsync();
            bool statusShown = false;
            try
            {
                Item[] snapshot = lib.ToArray();
                var knownSnap = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
                List<string> roots = ScanRoots();
                IProgress<ScanProgress> progress = null;
                if (!silent)
                {
                    statusShown = true;
                    ScanStatus.Visibility = Visibility.Visible;
                    ScanBar.IsIndeterminate = true;
                    ScanText.Text = "Procurando mudanças...";
                    progress = new Progress<ScanProgress>(UpdateScanProgress);
                }
                LibraryDiff diff = await Task.Run(() => CheckLibrary(snapshot, knownSnap, roots, progress));

                int removed = 0, added = 0, changed = 0;
                var addedItems = new List<Item>();
                if (diff.Missing.Count > 0)
                {
                    var gone = new HashSet<Item>(diff.Missing);
                    removed = lib.RemoveAll(i => gone.Contains(i));
                    recent.RemoveAll(i => gone.Contains(i));
                    for (int q = queue.Count - 1; q >= 0; q--) if (gone.Contains(queue[q])) queue.RemoveAt(q);
                    foreach (Item it in diff.Missing) { known.Remove(it.FilePath); itemsByPath.Remove(it.FilePath); }
                }
                foreach (var pair in diff.Changed)
                {
                    if (!itemsByPath.ContainsKey(pair.Key.FilePath)) continue;
                    pair.Key.CopyFrom(pair.Value);
                    changed++;
                }
                foreach (Item it in diff.Added)
                {
                    if (!known.Add(it.FilePath)) continue;
                    itemsByPath[it.FilePath] = it;
                    lib.Add(it);
                    recent.Insert(0, it);
                    if (recent.Count > 30) recent.RemoveAt(recent.Count - 1);
                    added++;
                    addedItems.Add(it);
                }

                if (removed + added + changed > 0)
                {
                    shufList = null; curIdx = -1;   // a ordem do aleatório será refeita
                    Save();
                    SyncYtFolderPlaylist();
                    RefreshQueue();
                    Refresh();
                    var parts = new List<string>();
                    if (added > 0) parts.Add(added == 1 ? "1 nova" : added + " novas");
                    if (removed > 0) parts.Add(removed == 1 ? "1 removida" : removed + " removidas");
                    if (changed > 0) parts.Add(changed == 1 ? "1 atualizada" : changed + " atualizadas");
                    Toast("Biblioteca atualizada: " + string.Join(", ", parts));
                    if (autoQueue && addedItems.Count > 0) AutoEnqueue(addedItems);   // só quando o vigia de pastas achou arquivo novo com o app aberto
                }
                else if (!silent) Toast("A biblioteca já está em dia");
            }
            finally
            {
                if (statusShown) ScanStatus.Visibility = Visibility.Collapsed;
                scanGate.Release();
            }
        }

        async Task StartupRefreshAsync()
        {
            try { await Task.Delay(2500); await RefreshLibraryAsync(true); }
            catch (Exception ex) { App.LogError("LibraryRefresh", ex); }
            StartFolderWatching();
        }

        // ---------- Vigia de pastas ----------
        void StartFolderWatching() { watchersOn = true; RebuildWatchers(); }

        void StopFolderWatching()
        {
            watchersOn = false;
            watchTimer?.Stop();
            foreach (var w in watchers)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not stop folder watcher: " + ex.Message); }
            }
            watchers.Clear();
        }

        void RebuildWatchers()
        {
            foreach (var w in watchers)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not stop folder watcher: " + ex.Message); }
            }
            watchers.Clear();
            if (!watchersOn) return;
            foreach (string root in ScanRoots())
            {
                try
                {
                    var w = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                        InternalBufferSize = 32768
                    };
                    FileSystemEventHandler handler = (a, e) => OnFolderChanged(e.FullPath);
                    w.Created += handler; w.Deleted += handler; w.Changed += handler;
                    w.Renamed += (a, e) => OnFolderChanged(e.FullPath);
                    w.Error += (a, e) => OnFolderChanged(null);
                    w.EnableRaisingEvents = true;
                    watchers.Add(w);
                }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not watch " + root + ": " + ex.Message); }
            }
        }

        // Muitos eventos seguidos (copiando uma pasta inteira) viram uma só atualização, 3 s depois do último
        void OnFolderChanged(string path)
        {
            if (path != null)
            {
                string ext = System.IO.Path.GetExtension(path);
                if (ext.Length > 0 && !IsSupportedMedia(path)) return;   // ignora o que não é mídia (.part, .jpg...)
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!watchersOn) return;
                if (watchTimer == null)
                {
                    watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    watchTimer.Tick += async (a, b) =>
                    {
                        watchTimer.Stop();
                        if (ytBusy || ytDlLock.CurrentCount == 0) { watchTimer.Start(); return; }   // espera a sincronização / o download do YouTube terminar (os arquivos temporários do yt-dlp não entram na biblioteca)
                        try { await RefreshLibraryAsync(true, true); }
                        catch (Exception ex) { App.LogError("LibraryWatcher", ex); }
                    };
                }
                watchTimer.Stop(); watchTimer.Start();
            }));
        }

        // ---------- Playlist automática da pasta "Azul Groove YouTube" ----------
        void SyncYtFolderPlaylist()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ytFolder) || !Directory.Exists(ytFolder)) return;
                if (watchersOn && watchers.Count != ScanRoots().Count) RebuildWatchers();   // a pasta pode ter acabado de ser criada
                string prefix = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(ytFolder)) + System.IO.Path.DirectorySeparatorChar;
                List<string> files = lib
                    .Where(i => i.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(i => i.FilePath)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)   // comparação simples: a por cultura era lenta em bibliotecas grandes
                    .ToList();
                SavedPlaylist pl = playlists.FirstOrDefault(p => p.Auto && p.Smart == null) ??
                    playlists.FirstOrDefault(p => string.Equals(p.Name, YtFolderPlaylistName, StringComparison.OrdinalIgnoreCase));
                if (pl == null)
                {
                    if (files.Count == 0) return;
                    AddPlaylistSorted(new SavedPlaylist { Name = YtFolderPlaylistName, Auto = true, Files = files });
                }
                else
                {
                    bool same = pl.Auto && pl.Files != null && pl.Files.Count == files.Count &&
                                pl.Files.SequenceEqual(files, StringComparer.OrdinalIgnoreCase);
                    if (same) return;
                    pl.Auto = true; pl.Files = files;
                }
                SavePlaylists();
                if (page == 5) RefreshPlaylistTracks();
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not sync the YouTube folder playlist: " + ex.Message); }
        }

        // ---------- Playlists: renomear e adicionar pelo botão direito ----------
        SavedPlaylist PlaylistOf(object s)
        {
            if (s is MenuItem mi && ItemsControl.ItemsControlFromItemContainer(mi) is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe)
                return fe.DataContext as SavedPlaylist;
            return PlaylistList.SelectedItem as SavedPlaylist;
        }

        void RenamePlaylist(SavedPlaylist playlist)
        {
            if (playlist == null) { Toast("Selecione uma playlist para renomear"); return; }
            if (playlist.Smart != null) { Toast("Playlist automática: não dá para renomear"); return; }
            string name = PromptPlaylistName("Renomear playlist", playlist.Name)?.Trim();
            if (string.IsNullOrEmpty(name) || name == playlist.Name) return;
            if (playlists.Any(p => p != playlist && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                Toast("Já existe uma playlist com esse nome");
                return;
            }
            int index = playlists.IndexOf(playlist);
            if (index < 0) return;
            playlists[index] = new SavedPlaylist { Name = name, Files = playlist.Files, Auto = playlist.Auto };
            SavePlaylists();
            PlaylistList.SelectedIndex = index;
            Toast("Playlist renomeada");
        }

        void RenamePlaylist_Click(object s, RoutedEventArgs e) => RenamePlaylist(PlaylistList.SelectedItem as SavedPlaylist);
        void RenamePlaylistMenu_Click(object s, RoutedEventArgs e) => RenamePlaylist(PlaylistOf(s));
        void PlayPlaylistMenu_Click(object s, RoutedEventArgs e)
        {
            var playlist = PlaylistOf(s);
            if (playlist == null) return;
            PlaylistList.SelectedItem = playlist;
            PlayPlaylist_Click(null, null);
        }
        void QueuePlaylistMenu_Click(object s, RoutedEventArgs e)
        {
            var playlist = PlaylistOf(s);
            if (playlist == null) return;
            PlaylistList.SelectedItem = playlist;
            AddPlaylistToQueue_Click(null, null);
        }
        void DeletePlaylistMenu_Click(object s, RoutedEventArgs e)
        {
            var playlist = PlaylistOf(s);
            if (playlist == null) return;
            PlaylistList.SelectedItem = playlist;
            DeletePlaylist_Click(null, null);
        }

        // Submenu "Adicionar à playlist": montado só quando abre (as playlists mudam)
        void AddToPlaylistMenu_Opened(object s, RoutedEventArgs e)
        {
            if (!(s is MenuItem parent) || !ReferenceEquals(e.OriginalSource, parent)) return;
            Item item = MenuItemOf(parent);
            parent.Items.Clear();
            if (item == null) return;
            var editable = playlists.Where(p => !p.Auto).ToList();   // a playlist automática do YouTube é refeita sozinha
            foreach (SavedPlaylist playlist in editable)
            {
                SavedPlaylist target = playlist;
                var entry = new MenuItem { Header = target.Name };
                entry.Click += (a, b) => AddItemToPlaylist(item, target);
                parent.Items.Add(entry);
            }
            if (editable.Count > 0) parent.Items.Add(new Separator());
            var create = new MenuItem { Header = "Nova playlist…" };
            create.Click += (a, b) => NewPlaylistWith(item);
            parent.Items.Add(create);
        }

        void AddItemToPlaylist(Item item, SavedPlaylist playlist)
        {
            if (item == null || playlist == null) return;
            if (playlist.Files == null) playlist.Files = new List<string>();
            if (playlist.Files.Contains(item.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                Toast("Essa faixa já está em “" + playlist.Name + "”");
                return;
            }
            playlist.Files.Add(item.FilePath);
            SavePlaylists();
            if (ReferenceEquals(PlaylistList.SelectedItem, playlist)) RefreshPlaylistTracks();
            Toast("Adicionada a “" + playlist.Name + "”");
        }

        void NewPlaylistWith(Item item)
        {
            string name = PromptPlaylistName()?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            SavedPlaylist existing = playlists.FirstOrDefault(p => !p.Auto && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { AddItemToPlaylist(item, existing); return; }
            AddPlaylistSorted(new SavedPlaylist { Name = name, Files = new List<string> { item.FilePath } });
            SavePlaylists();
            Toast("Playlist “" + name + "” criada");
        }

        // ---------- Artistas e álbuns ----------
        static bool InGroup(Item item, int kind, string name)
        {
            string value = kind == 1 ? item.Artist : item.Album;
            if (string.IsNullOrWhiteSpace(value)) return kind == 1 && name == UnknownArtist;
            return string.Equals(value.Trim(), name, StringComparison.CurrentCultureIgnoreCase);
        }

        List<GroupEntry> BuildGroups(Item[] items, bool artists)
        {
            var map = new Dictionary<string, List<Item>>(StringComparer.CurrentCultureIgnoreCase);
            foreach (Item item in items)
            {
                if (item.IsVideo) continue;
                string key = artists ? item.Artist : item.Album;
                if (string.IsNullOrWhiteSpace(key))
                {
                    if (!artists) continue;
                    key = UnknownArtist;
                }
                key = key.Trim();
                if (!map.TryGetValue(key, out List<Item> list)) map[key] = list = new List<Item>();
                list.Add(item);
            }

            var groups = new List<GroupEntry>(map.Count);
            foreach (var pair in map.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                List<Item> groupItems = pair.Value.OrderBy(i => i.Album, StringComparer.CurrentCultureIgnoreCase)
                                             .ThenBy(i => i.FilePath, StringComparer.CurrentCultureIgnoreCase).ToList();
                string subtitle = groupItems.Count == 1 ? "1 música" : groupItems.Count + " músicas";
                if (artists)
                {
                    int albums = groupItems.Where(i => !string.IsNullOrWhiteSpace(i.Album)).Select(i => i.Album.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase).Count();
                    if (albums > 0) subtitle += albums == 1 ? " · 1 álbum" : " · " + albums + " álbuns";
                }
                else
                {
                    string artist = groupItems.Where(i => !string.IsNullOrWhiteSpace(i.Artist)).GroupBy(i => i.Artist.Trim(), StringComparer.CurrentCultureIgnoreCase)
                                         .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
                    if (artist != null) subtitle += " · " + artist;
                }
                groups.Add(new GroupEntry { Name = pair.Key, Subtitle = subtitle, Items = groupItems, ArtRadius = new CornerRadius(artists ? 24 : 8) });
            }

            return groups;
        }

        int groupsGen;

        // O agrupamento e a ordenação (comparação por cultura, lenta com bibliotecas grandes) rodam em segundo plano;
        // a tela só recebe o resultado pronto.
        async void RefreshGroups()
        {
            bool artists = page == 7;
            int gen = ++groupsGen;
            Item[] snapshot = lib.ToArray();
            List<GroupEntry> groups;
            try { groups = await Task.Run(() => BuildGroups(snapshot, artists)); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not build groups: " + ex.Message); return; }
            if (gen != groupsGen || (page != 7 && page != 8)) return;

            GroupTitle.Text = artists ? "Artistas" : "Álbuns";
            GroupSub.Text = groups.Count == 1 ? (artists ? "1 artista" : "1 álbum") : groups.Count + (artists ? " artistas" : " álbuns");
            GroupList.ItemsSource = groups;
            GroupEmpty.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            GroupEmpty.Text = artists ? "Nenhum artista encontrado. Músicas com etiquetas (tags) de artista aparecem aqui." : "Nenhum álbum encontrado. Músicas com etiqueta de álbum aparecem aqui.";
        }

        void Group_Click(object s, RoutedEventArgs e)
        {
            if (!((s as FrameworkElement)?.DataContext is GroupEntry group)) return;
            groupKind = page == 7 ? 1 : 2;
            groupName = group.Name;
            Search.Text = "";
            Navigate(1, true);
        }

        void GroupPlay_Click(object s, RoutedEventArgs e)
        {
            e.Handled = true;   // não deixa o clique "subir" e abrir o grupo
            if (!((s as FrameworkElement)?.DataContext is GroupEntry group) || group.Items.Count == 0) return;
            queue.Clear();
            foreach (Item item in group.Items) queue.Add(item);
            RefreshQueue();
            Play(queue[0]);
        }

        void GroupBack_Click(object s, RoutedEventArgs e) => Navigate(groupKind == 2 ? 8 : 7);

        // O RadioButton só avisa quando muda; o Click também avisa quando já estava marcado (voltar da lista de um artista)
        void NavGroup_Click(object s, RoutedEventArgs e)
        {
            int target = int.Parse(((RadioButton)s).Uid);
            if (groupName != null || page != target) Navigate(target);
        }
    }
}
