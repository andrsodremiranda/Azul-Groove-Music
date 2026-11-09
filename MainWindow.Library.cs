using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AzulGroove
{
    public partial class MainWindow
    {
        // ---------- Biblioteca ----------
        bool AddOne(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            bool v = VideoExt.Contains(ext);
            if (!v && !AudioExt.Contains(ext)) return false;
            if (!known.Add(path)) return false;
            var it = new Item(path, v); itemsByPath.Add(path, it); lib.Add(it); recent.Insert(0, it);
            if (recent.Count > 30) recent.RemoveAt(recent.Count - 1);
            return true;
        }

        static List<string> DiscoverFiles(IEnumerable<string> paths, IProgress<ScanProgress> progress)
        {
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int discovered = 0;
            progress?.Report(new ScanProgress { Discovering = true });
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (Directory.Exists(p))
                {
                    var pendingDirectories = new Stack<string>();
                    pendingDirectories.Push(p);
                    while (pendingDirectories.Count > 0)
                    {
                        string directory = pendingDirectories.Pop();
                        string[] directoryFiles;
                        try { directoryFiles = Directory.GetFiles(directory); }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not scan directory " + directory + ": " + ex.Message); continue; }
                        foreach (string file in directoryFiles.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                        {
                            if (!IsSupportedMedia(file) || !seen.Add(file)) continue;
                            files.Add(file);
                            discovered++;
                            if (discovered % 100 == 0)
                                progress?.Report(new ScanProgress { Discovering = true, Completed = discovered });
                        }

                        string[] subdirectories;
                        try { subdirectories = Directory.GetDirectories(directory); }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not enumerate folders in " + directory + ": " + ex.Message); continue; }
                        foreach (string child in subdirectories.OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase))
                        {
                            try
                            {
                                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pendingDirectories.Push(child);
                            }
                            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not inspect directory " + child + ": " + ex.Message); }
                        }
                    }
                }
                else if (File.Exists(p) && IsSupportedMedia(p) && seen.Add(p)) files.Add(p);
            }
            return files;
        }

        static List<Item> ScanPaths(IEnumerable<string> paths, IProgress<ScanProgress> progress)
            => ReadItems(DiscoverFiles(paths, progress), progress);

        static List<Item> ReadItems(List<string> files, IProgress<ScanProgress> progress)
        {
            progress?.Report(new ScanProgress { Total = files.Count, Discovering = false });
            var results = new Item[files.Count];
            int done = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Math.Min(4, Environment.ProcessorCount / 2)) };
            Parallel.For(0, files.Count, options, i =>
            {
                string path = files[i];
                bool video = VideoExt.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
                try { results[i] = new Item(path, video); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not read media file " + path + ": " + ex.Message); }
                int completed = Interlocked.Increment(ref done);
                if (completed % 10 == 0 || completed == files.Count)
                    progress?.Report(new ScanProgress { Completed = completed, Total = files.Count });
            });
            var items = new List<Item>(files.Count);
            foreach (var item in results) if (item != null) items.Add(item);
            return items;
        }

        // Abre a biblioteca a partir do cache (library.json): não toca em nenhum arquivo de mídia, então abre na hora
        static List<Item> ReadLibraryCache()
        {
            var list = new List<Item>();
            var entries = JsonSerializer.Deserialize<List<LibraryEntry>>(File.ReadAllText(LibCacheFile));
            if (entries == null) return list;
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Path)) continue;
                DateTime date = default;
                if (entry.DateTicks > 0 && entry.DateTicks <= DateTime.MaxValue.Ticks) date = new DateTime(entry.DateTicks, DateTimeKind.Utc);
                list.Add(new Item(entry.Path, entry.Video, entry.Name, entry.Artist, entry.Album,
                    TimeSpan.FromMilliseconds(Math.Max(0, entry.DurationMs)), date));
            }
            return list;
        }

        async Task LoadLibraryAsync()
        {
            bool loaded = false;
            if (File.Exists(LibCacheFile))
            {
                try
                {
                    List<Item> cached = await Task.Run(ReadLibraryCache);
                    foreach (var item in cached)
                    {
                        if (!known.Add(item.FilePath)) continue;
                        itemsByPath.Add(item.FilePath, item);
                        lib.Add(item);
                    }
                    Refresh();
                    loaded = true;
                }
                catch (Exception ex) { App.LogError("LibraryCache", ex); }
            }
            if (loaded || !File.Exists(LibFile)) return;
            try
            {
                // biblioteca antiga (só caminhos): lê uma vez e já gera o cache para as próximas aberturas
                string[] savedPaths = await Task.Run(() => File.ReadAllLines(LibFile));
                await AddPathsAsync(savedPaths, false);
            }
            catch (Exception ex) { App.LogError("LibraryLegacy", ex); Toast("Não foi possível carregar a biblioteca: " + ex.Message); }
        }

        static bool IsSupportedMedia(string path)
        {
            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return AudioExt.Contains(extension) || VideoExt.Contains(extension);
        }

        async Task AddPathsAsync(IEnumerable<string> paths, bool autoplay)
        {
            string[] requestedPaths = paths?.ToArray() ?? Array.Empty<string>();
            if (requestedPaths.Length == 0) return;
            RegisterRoots(requestedPaths);
            await scanGate.WaitAsync();
            try
            {
                ScanStatus.Visibility = Visibility.Visible;
                ScanBar.IsIndeterminate = true;
                ScanText.Text = "Localizando arquivos...";
                var progress = new Progress<ScanProgress>(UpdateScanProgress);
                List<Item> scanned = await Task.Run(() => ScanPaths(requestedPaths, progress));
                Item first = null;
                for (int i = 0; i < scanned.Count; i++)
                {
                    Item candidate = scanned[i];
                    if (itemsByPath.TryGetValue(candidate.FilePath, out Item existing))
                    {
                        if (first == null) first = existing;
                        continue;
                    }
                    if (!known.Add(candidate.FilePath)) continue;
                    itemsByPath.Add(candidate.FilePath, candidate);
                    lib.Add(candidate);
                    recent.Insert(0, candidate);
                    if (recent.Count > 30) recent.RemoveAt(recent.Count - 1);
                    if (first == null) first = candidate;
                    if ((i + 1) % 100 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
                }
                Save();
                Refresh();
                if (autoplay && first != null)
                {
                    pendingSeek = -1;
                    Play(first);
                }
            }
            finally
            {
                ScanStatus.Visibility = Visibility.Collapsed;
                scanGate.Release();
            }
        }

        void UpdateScanProgress(ScanProgress progress)
        {
            if (progress.Discovering)
            {
                ScanBar.IsIndeterminate = true;
                ScanText.Text = progress.Completed > 0
                    ? "Localizando arquivos... " + progress.Completed
                    : "Localizando arquivos...";
            }
            else
            {
                ScanBar.IsIndeterminate = false;
                ScanBar.Maximum = Math.Max(1, progress.Total);
                ScanBar.Value = progress.Completed;
                ScanText.Text = progress.Total == 0
                    ? "Nenhum arquivo de mídia encontrado"
                    : "Lendo arquivos " + progress.Completed + " de " + progress.Total;
            }
        }

        void Save()
        {
            var entries = lib.Select(item => new LibraryEntry
            {
                Path = item.FilePath, Video = item.IsVideo, Name = item.Name, Artist = item.Artist, Album = item.Album,
                DurationMs = (long)item.Duration.TotalMilliseconds, DateTicks = item.FileDate.Ticks
            }).ToArray();
            long version = Interlocked.Increment(ref librarySaveVersion);
            _ = Task.Run(() =>
            {
                lock (librarySaveLock)
                {
                    if (version != Volatile.Read(ref librarySaveVersion)) return;
                    try
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LibCacheFile));
                        string temp = LibCacheFile + ".tmp";
                        File.WriteAllText(temp, JsonSerializer.Serialize(entries));
                        File.Move(temp, LibCacheFile, true);
                    }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not save library: " + ex.Message); }
                }
            });
        }

        async void Refresh()
        {
            if (page == 3) { RefreshQueue(); return; }
            if (page == 5) { RefreshPlaylistTracks(); return; }
            if (page == 7 || page == 8) { RefreshGroups(); return; }
            if (page == 6 || page == 9 || page == 10) return;
            int generation = Interlocked.Increment(ref refreshGeneration);
            int currentPage = page;
            string query = Search.Text.Trim();
            int currentSort = sortMode;
            int gKind = groupKind; string gName = groupName;
            Item[] source;
            string title, subtitle = "";
            if (query.Length > 0)
            {
                source = lib.ToArray();
                title = "Resultados";
                subtitle = "Busca por “" + query + "”";
            }
            else if (gName != null)
            {
                source = lib.ToArray();
                title = gName;
                subtitle = gKind == 1 ? "Artista" : "Álbum";
            }
            else switch (currentPage)
                {
                    case 1: source = lib.ToArray(); title = "Músicas"; break;
                    case 2: source = lib.ToArray(); title = "Vídeos"; break;
                    default: source = recent.ToArray(); title = "Início"; subtitle = "Mídia recente"; break;
                }

            List<Item> filtered;
            try
            {
                filtered = await Task.Run(() =>
                {
                    IEnumerable<Item> items = source;
                    if (query.Length > 0)
                        items = items.Where(item => item.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            item.Artist.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            item.Album.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                    else if (gName != null) items = items.Where(item => !item.IsVideo && InGroup(item, gKind, gName));
                    else if (currentPage == 1) items = items.Where(item => !item.IsVideo);
                    else if (currentPage == 2) items = items.Where(item => item.IsVideo);

                    if (currentPage != 0)
                    {
                        if (currentSort == 1) items = items.OrderByDescending(item => item.FileDate);
                        else if (currentSort == 2) items = items.OrderBy(item => item.Duration);
                        else items = items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase);
                    }
                    return items.ToList();
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Could not refresh the media list: " + ex.Message);
                return;
            }
            if (generation != refreshGeneration || currentPage != page || query != Search.Text.Trim()) return;
            allShown = filtered;
            shown.Clear();
            animIdx = 0;
            AddMore(24);
            ContinueFill(++fillGen);
            PageTitle.Text = title;
            PageSub.Text = subtitle;
            PageSub.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Empty.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            GroupBack.Visibility = gName != null && query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Navigate(int p, bool keepGroup = false)
        {
            if (!keepGroup) { groupKind = 0; groupName = null; }
            searchTimer?.Stop(); Interlocked.Increment(ref refreshGeneration); fillGen++; page = p; bool pv = p == 4;
            Drift(!pv && WindowState != WindowState.Minimized);
            Fx(p == 0 && WindowState != WindowState.Minimized);
            bool groups = p == 7 || p == 8;
            PlayerView.Visibility = pv ? Visibility.Visible : Visibility.Collapsed;
            QueueView.Visibility = p == 3 && !pv ? Visibility.Visible : Visibility.Collapsed;
            PlaylistView.Visibility = p == 5 && !pv ? Visibility.Visible : Visibility.Collapsed;
            YtView.Visibility = p == 6 && !pv ? Visibility.Visible : Visibility.Collapsed;
            SettingsView.Visibility = p == 9 && !pv ? Visibility.Visible : Visibility.Collapsed;
            AboutView.Visibility = p == 10 && !pv ? Visibility.Visible : Visibility.Collapsed;
            GroupView.Visibility = groups && !pv ? Visibility.Visible : Visibility.Collapsed;
            LibView.Visibility = pv || p == 3 || p == 5 || p == 6 || p == 9 || p == 10 || groups ? Visibility.Collapsed : Visibility.Visible;
            int navIndex = keepGroup && groupName != null ? (groupKind == 1 ? 7 : 8) : p;   // dentro de um artista/álbum, o menu continua nele
            navSet = true; radios[navIndex].IsChecked = true; navSet = false;
            if (p == 3) RefreshQueue();
            else if (p == 5)
            {
                RefreshSmartPlaylists();
                // já abre com a primeira playlist selecionada (antes a lista da direita ficava vazia até o usuário clicar)
                if (PlaylistList.SelectedItem == null && playlists.Count > 0) PlaylistList.SelectedIndex = 0;
                else RefreshPlaylistTracks();
            }
            else if (p == 6) { }
            else if (p == 9) RefreshSettingsView();
            else if (p == 10) RefreshAboutView();
            else if (groups) RefreshGroups();
            else if (!pv) Refresh();
            UpdateViz();
            if (pv) return; // a página do vídeo não é animada: evita falhas gráficas com a janela nativa
            if (p == 9) Anim(SettingsView, OpacityProperty, 0, 1, 260);
            if (p == 10) Anim(AboutView, OpacityProperty, 0, 1, 260);
            Anim(Head, OpacityProperty, 0, 1, 260);
            Anim(LibShift, TranslateTransform.YProperty, 18, 0, 320);
        }

        void Nav_Checked(object s, RoutedEventArgs e) { if (IsLoaded && !navSet) Navigate(int.Parse(((RadioButton)s).Uid)); }
        void Back_Click(object s, RoutedEventArgs e) { Search.Text = ""; Navigate(0); }
        void Search_Changed(object s, TextChangedEventArgs e)
        {
            Ph.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!IsLoaded) return;
            if (page >= 3 && page <= 10) { if (Search.Text.Trim().Length > 0) Navigate(1); }
            else { searchTimer.Stop(); searchTimer.Start(); }
            YtSearchSchedule();
        }

        void Card_Click(object s, RoutedEventArgs e) { if (((FrameworkElement)s).DataContext is Item it) Play(it); }

        void RefreshQueue()
        {
            QueueSummary.Text = queue.Count == 1 ? "1 faixa na fila" : queue.Count + " faixas na fila";
            QueueEmpty.Visibility = queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void AddToQueue(Item item, bool next)
        {
            if (item == null) return;
            if (next) queue.Insert(0, item);
            else queue.Add(item);
            RefreshQueue();
            Toast(next ? "Adicionada para tocar a seguir" : "Adicionada à fila");
        }

        void PlayNextMenu_Click(object s, RoutedEventArgs e) => AddToQueue(MenuItemOf(s), true);
        void QueueMenu_Click(object s, RoutedEventArgs e) => AddToQueue(MenuItemOf(s), false);

        Item QueueItemOf(object source) => (source as FrameworkElement)?.DataContext as Item;

        void QueuePlay_Click(object s, RoutedEventArgs e)
        {
            var item = QueueItemOf(s);
            if (item != null) Play(item);
        }

        void QueueRemove_Click(object s, RoutedEventArgs e)
        {
            var item = QueueItemOf(s);
            if (item != null) queue.Remove(item);
            RefreshQueue();
        }

        void ClearQueue_Click(object s, RoutedEventArgs e)
        {
            queue.Clear();
            RefreshQueue();
        }

        void Sort_Changed(object s, SelectionChangedEventArgs e)
        {
            if (SortBox == null) return;
            sortMode = Math.Max(0, SortBox.SelectedIndex);
            if (IsLoaded && page != 0 && page != 3 && page != 4 && page != 5 && page != 6 && page != 7 && page != 8 && page != 9 && page != 10) Refresh();
        }

        void LoadPlaylists()
        {
            try
            {
                if (!File.Exists(PlaylistsFile)) return;
                var stored = JsonSerializer.Deserialize<List<SavedPlaylist>>(File.ReadAllText(PlaylistsFile));
                if (stored == null) return;
                playlists.Clear();
                var loaded = stored.Where(item => item != null && !string.IsNullOrWhiteSpace(item.Name))
                    .Select(entry => new SavedPlaylist { Name = entry.Name, Auto = entry.Auto, Files = entry.Files ?? new List<string>() }).ToList();
                // As do usuário primeiro; as automáticas (pasta do YouTube) por último
                foreach (var playlist in loaded.Where(p => !p.Auto)) playlists.Add(playlist);
                foreach (var playlist in loaded.Where(p => p.Auto)) playlists.Add(playlist);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not load playlists: " + ex.Message); }
        }

        // Ordem da lista: playlists do usuário, depois a pasta "Azul Groove YouTube", e por fim as automáticas ★
        static readonly string[] SmartOrder = { "top", "played", "recent", "forgotten", "never", "auto" };

        int PlaylistInsertIndex(SavedPlaylist playlist)
        {
            if (playlist.Smart != null)
            {
                int rank = Array.IndexOf(SmartOrder, playlist.Smart);
                for (int i = 0; i < playlists.Count; i++)
                    if (playlists[i].Smart != null && Array.IndexOf(SmartOrder, playlists[i].Smart) > rank) return i;
                return playlists.Count;
            }
            if (playlist.Auto)
            {
                for (int i = 0; i < playlists.Count; i++)
                    if (playlists[i].Smart != null) return i;
                return playlists.Count;
            }
            for (int i = 0; i < playlists.Count; i++)
                if (playlists[i].Auto) return i;
            return playlists.Count;
        }

        void AddPlaylistSorted(SavedPlaylist playlist) => playlists.Insert(PlaylistInsertIndex(playlist), playlist);

        void SavePlaylists()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PlaylistsFile));
                string temp = PlaylistsFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(playlists.Where(p => p.Smart == null).ToList()));
                File.Move(temp, PlaylistsFile, true);
            }
            catch (Exception ex) { Toast("Não foi possível salvar as playlists: " + ex.Message); }
        }

        // A lista nova é montada de uma vez e entregue à tela inteira (antes entrava faixa por faixa, o que travava playlists grandes)
        void RefreshPlaylistTracks()
        {
            var items = new List<Item>();
            if (PlaylistList?.SelectedItem is SavedPlaylist playlist)
            {
                foreach (string path in playlist.Files ?? new List<string>())
                    if (itemsByPath.TryGetValue(path, out Item item)) items.Add(item);
            }
            selectedPlaylistItems = items;
            if (PlaylistTracks != null) PlaylistTracks.ItemsSource = items;
        }

        void Playlist_SelectionChanged(object s, SelectionChangedEventArgs e) => RefreshPlaylistTracks();

        void SaveQueue_Click(object s, RoutedEventArgs e)
        {
            if (queue.Count == 0) { Toast("Adicione faixas à fila antes de salvá-la"); return; }
            string name = PromptPlaylistName();
            if (string.IsNullOrWhiteSpace(name)) return;
            var created = new SavedPlaylist { Name = name.Trim(), Files = queue.Select(item => item.FilePath).ToList() };
            AddPlaylistSorted(created);
            SavePlaylists();
            PlaylistList.SelectedItem = created;
        }

        string PromptPlaylistName(string title = "Nova playlist", string initial = "")
        {
            var dialog = new Window
            {
                Title = title,
                Owner = this,
                Width = 360,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = (Brush)FindResource("Abyss"),
                Foreground = (Brush)FindResource("Foam")
            };
            var layout = new StackPanel { Margin = new Thickness(18) };
            layout.Children.Add(new TextBlock { Text = "Nome da playlist", Margin = new Thickness(0, 0, 0, 8) });
            var input = new TextBox { MinWidth = 280, Padding = new Thickness(7), Text = initial ?? string.Empty };
            layout.Children.Add(input);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            string result = null;
            var save = new Button { Content = "Salvar", IsDefault = true, MinWidth = 76, Margin = new Thickness(0, 0, 8, 0) };
            save.Click += (sender, args) => { result = input.Text; dialog.DialogResult = true; };
            var cancel = new Button { Content = "Cancelar", IsCancel = true, MinWidth = 76 };
            actions.Children.Add(save);
            actions.Children.Add(cancel);
            layout.Children.Add(actions);
            dialog.Content = layout;
            dialog.Loaded += (sender, args) => { input.Focus(); input.SelectAll(); };
            dialog.ShowDialog();
            return result;
        }

        void PlayPlaylist_Click(object s, RoutedEventArgs e)
        {
            if (selectedPlaylistItems.Count == 0) { Toast("Selecione uma playlist com faixas"); return; }
            queue.Clear();
            foreach (var item in selectedPlaylistItems) queue.Add(item);
            RefreshQueue();
            Play(queue[0]);
        }

        void AddPlaylistToQueue_Click(object s, RoutedEventArgs e)
        {
            if (selectedPlaylistItems.Count == 0) { Toast("Selecione uma playlist com faixas"); return; }
            foreach (var item in selectedPlaylistItems) queue.Add(item);
            RefreshQueue();
        }

        void DeletePlaylist_Click(object s, RoutedEventArgs e)
        {
            if (PlaylistList.SelectedItem is not SavedPlaylist playlist) return;
            if (playlist.Smart != null) { Toast("Playlist automática: não dá para excluir"); return; }
            playlists.Remove(playlist);
            SavePlaylists();
            RefreshPlaylistTracks();
        }

        void PlaylistTrack_DoubleClick(object s, MouseButtonEventArgs e)
        {
            if (PlaylistTracks.SelectedItem is Item item) Play(item);
        }

        void PlaylistTrackPlay_Click(object s, RoutedEventArgs e)
        {
            if ((s as FrameworkElement)?.DataContext is Item item) Play(item);
        }

        void Queue_DoubleClick(object s, MouseButtonEventArgs e)
        {
            if (QueueItems.SelectedItem is Item item) Play(item);
        }

        void Queue_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (FindVisualChild<Button>(e.OriginalSource as DependencyObject) != null)
            {
                queueDragIndex = -1;
                return;
            }
            var container = FindListBoxItem(e.OriginalSource as DependencyObject);
            queueDragIndex = container == null ? -1 : QueueItems.ItemContainerGenerator.IndexFromContainer(container);
            queueDragOrigin = e.GetPosition(QueueItems);
        }

        void Queue_MouseMove(object s, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || queueDragIndex < 0 || queueDragIndex >= queue.Count) return;
            Point position = e.GetPosition(QueueItems);
            if (Math.Abs(position.X - queueDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(position.Y - queueDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var index = queueDragIndex;
            queueDragIndex = -1;
            DragDrop.DoDragDrop(QueueItems, index, DragDropEffects.Move);
        }

        void Queue_DragOver(object s, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(int)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        void Queue_Drop(object s, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(int))) return;
            int from = (int)e.Data.GetData(typeof(int));
            if (from < 0 || from >= queue.Count) return;
            var target = FindListBoxItem(e.OriginalSource as DependencyObject);
            int to = target == null ? queue.Count : QueueItems.ItemContainerGenerator.IndexFromContainer(target);
            if (to < 0) to = queue.Count;
            if (from < to) to--;
            var item = queue[from];
            queue.RemoveAt(from);
            queue.Insert(Math.Max(0, Math.Min(queue.Count, to)), item);
            RefreshQueue();
            e.Handled = true;
        }

        static ListBoxItem FindListBoxItem(DependencyObject source)
        {
            while (source != null && !(source is ListBoxItem))
                source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            return source as ListBoxItem;
        }

        public void HandleActivation(string[] paths)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
            if (paths != null && paths.Length > 0) OpenActivationPaths(paths);
        }

        async void OpenActivationPaths(string[] paths)
        {
            try
            {
                pendingSeek = -1;
                await AddPathsAsync(paths, true);
            }
            catch (Exception ex) { Toast("Não foi possível abrir a mídia: " + ex.Message); }
        }

        void Remove_Click(object s, RoutedEventArgs e)
        {
            if (MenuItemOf(s) is Item it)
            {
                if (it == cur) { _mp?.Stop(); cur = null; UpdateNow(); }
                while (queue.Remove(it)) { }
                lib.Remove(it); recent.Remove(it); known.Remove(it.FilePath); itemsByPath.Remove(it.FilePath); Save(); Refresh();
                RefreshQueue();
            }
        }

        void Files_Click(object s, RoutedEventArgs e)
        {
            var d = new OpenFileDialog { Multiselect = true, Filter = "Mídia|*.mp3;*.flac;*.wav;*.ogg;*.opus;*.m4a;*.aac;*.wma;*.mp4;*.mkv;*.webm;*.mov;*.avi;*.m4v;*.wmv;*.ts|Todos os arquivos|*.*" };
            if (d.ShowDialog() == true) OpenActivationPaths(d.FileNames);
        }
        void Folder_Click(object s, RoutedEventArgs e)
        {
            var d = new OpenFolderDialog();
            if (d.ShowDialog() == true) OpenActivationPaths(new[] { d.FolderName });
        }
        void Window_Drop(object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) OpenActivationPaths((string[])e.Data.GetData(DataFormats.FileDrop));
        }

    }
}
