using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using VLC = LibVLCSharp.Shared;

namespace AzulGroove
{
    public class Item : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public string FilePath { get; }
        public string Name { get; private set; }
        public string Artist { get; private set; }
        public string Album { get; private set; }
        public TimeSpan Duration { get; private set; }
        public DateTime FileDate { get; private set; }
        public string Subtitle
        {
            get
            {
                var parts = new List<string>();
                string text = string.Join(" · ", new[] { Artist, Album }.Where(value => !string.IsNullOrWhiteSpace(value)));
                if (text.Length > 0) parts.Add(text);
                if (Duration > TimeSpan.Zero) parts.Add(Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"m\:ss"));
                if (plays > 0) parts.Add("▶ " + plays);
                return string.Join(" · ", parts);
            }
        }
        int plays;
        public int Plays => plays;
        public void SetPlays(int count)
        {
            if (plays == count) return;
            plays = count;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
        }
        // Faixa tocada direto da internet (YouTube): o FilePath é o endereço do vídeo
        public bool IsStream => FilePath != null && FilePath.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        public bool IsVideo { get; }
        public string Kind => IsVideo ? "Vídeo" : "Música";
        public string Glyph => IsVideo ? "\uE714" : "\uE8D6";
        public string Initial => Name.Length > 0 ? Name.Substring(0, 1).ToUpper() : "♪";
        // Relê as informações depois que o arquivo mudou (atualizar biblioteca)
        public void CopyFrom(Item fresh)
        {
            Name = fresh.Name; Artist = fresh.Artist; Album = fresh.Album; Duration = fresh.Duration; FileDate = fresh.FileDate;
            lock (artCache) artCache.Remove(FilePath);
            foreach (string property in new[] { nameof(Name), nameof(Artist), nameof(Album), nameof(Subtitle), nameof(Initial), nameof(Art) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
        bool _p;
        public bool IsPlaying { get => _p; set { _p = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying))); } }

        // Capa: carregada só quando o cartão aparece na tela e guardada num cache pequeno (antes todas ficavam na RAM)
        static readonly Dictionary<string, Brush> artCache = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
        static readonly Queue<string> artOrder = new Queue<string>();
        const int ArtCacheMax = 160;
        public bool TryGetCachedArt(out Brush brush)
        {
            lock (artCache) return artCache.TryGetValue(FilePath, out brush);
        }

        public Brush Art
        {
            get
            {
                lock (artCache)
                    if (artCache.TryGetValue(FilePath, out Brush cached)) return cached;
                Brush made = LoadArtwork() ?? MakeGradient();
                lock (artCache)
                {
                    if (!artCache.ContainsKey(FilePath))
                    {
                        artCache[FilePath] = made; artOrder.Enqueue(FilePath);
                        while (artOrder.Count > ArtCacheMax) artCache.Remove(artOrder.Dequeue());
                    }
                }
                return made;
            }
        }

        // Construtor rápido: usa os dados já salvos, sem abrir o arquivo
        public Item(string path, bool video, string name, string artist, string album, TimeSpan duration, DateTime fileDate)
        {
            FilePath = path; IsVideo = video;
            Name = string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileNameWithoutExtension(path) : name;
            Artist = artist ?? string.Empty; Album = album ?? string.Empty;
            Duration = duration; FileDate = fileDate;
        }

        // Construtor completo: lê as tags (sem decodificar a capa)
        public Item(string path, bool video)
        {
            FilePath = path; IsVideo = video;
            Name = System.IO.Path.GetFileNameWithoutExtension(path);
            try { FileDate = File.GetLastWriteTimeUtc(path); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not read media date for " + path + ": " + ex.Message); }
            Artist = Album = string.Empty;
            Duration = TimeSpan.Zero;
            try
            {
                using (var media = TagLib.File.Create(path))
                {
                    if (!string.IsNullOrWhiteSpace(media.Tag.Title)) Name = media.Tag.Title;
                    Artist = media.Tag.Performers?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
                    Album = media.Tag.Album ?? string.Empty;
                    Duration = media.Properties?.Duration ?? TimeSpan.Zero;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Could not read media metadata for " + path + ": " + ex.Message);
            }
        }

        Brush LoadArtwork()
        {
            if (IsVideo || IsStream) return null;
            try
            {
                using (var media = TagLib.File.Create(FilePath))
                {
                    var picture = media.Tag.Pictures?.FirstOrDefault();
                    if (picture == null || picture.Data?.Data == null || picture.Data.Data.Length == 0) return null;
                    using (var stream = new MemoryStream(picture.Data.Data))
                    {
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.DecodePixelWidth = 256;
                        image.StreamSource = stream;
                        image.EndInit();
                        image.Freeze();
                        var imageBrush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                        imageBrush.Freeze();
                        return imageBrush;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Could not read artwork for " + FilePath + ": " + ex.Message);
                return null;
            }
        }

        Brush MakeGradient()
        {
            int h = 0; foreach (char c in Name) h = h * 31 + c; h &= 0x7fffffff;
            double hue = 195 + (h % 50);
            var b = new LinearGradientBrush(Hsv(hue, .7, .95), Hsv(hue + 25, .9, .42), 45);
            b.Freeze(); return b;
        }

        static Color Hsv(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c, r, g, b;
            if (h < 60) { r = c; g = x; b = 0; } else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; } else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; } else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }
    }

    sealed class PlayerSettings
    {
        public double Volume { get; set; } = 80;
        public bool Muted { get; set; }
        public float Speed { get; set; } = 1;
        public bool Shuffle { get; set; }
        public int Repeat { get; set; }
        public string CurrentFile { get; set; }
        public long PositionMs { get; set; }
        public List<string> Queue { get; set; } = new List<string>();
    }

    sealed class LibraryEntry
    {
        public string Path { get; set; }
        public bool Video { get; set; }
        public string Name { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public long DurationMs { get; set; }
        public long DateTicks { get; set; }
    }

    sealed class SavedPlaylist
    {
        public string Name { get; set; }
        public bool Auto { get; set; }   // playlist mantida sozinha pelo app (pasta Azul Groove YouTube)
        public string Smart { get; set; }   // "top" ou "recent": playlists automáticas calculadas na hora (não são salvas)
        public List<string> Files { get; set; } = new List<string>();
    }

    sealed class AudioDeviceChoice
    {
        public string Name { get; set; }
        public string DeviceId { get; set; }
        public override string ToString() => Name;
    }

    sealed class ScanProgress
    {
        public int Completed { get; set; }
        public int Total { get; set; }
        public bool Discovering { get; set; }
    }

    public partial class MainWindow : Window
    {
        static readonly string[] AudioExt = { ".mp3", ".flac", ".wav", ".ogg", ".oga", ".opus", ".m4a", ".aac", ".wma", ".aiff", ".ape" };
        static readonly string[] VideoExt = { ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v", ".wmv", ".flv", ".ts", ".mpg", ".mpeg", ".3gp" };
        static readonly string LibFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove", "library.txt");
        static readonly string LibCacheFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove", "library.json");
        static readonly string SettingsFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove", "settings.json");
        static readonly string PlaylistsFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove", "playlists.json");
        const int NB = 28;

        VLC.LibVLC _lib; VLC.MediaPlayer _mp;
        readonly List<Item> lib = new List<Item>();
        readonly List<Item> recent = new List<Item>();
        readonly ObservableCollection<Item> queue = new ObservableCollection<Item>();
        readonly ObservableCollection<SavedPlaylist> playlists = new ObservableCollection<SavedPlaylist>();
        List<Item> selectedPlaylistItems = new List<Item>();
        Item cur; int page; bool shuffle, full, dragging, navSet, ringOn, discOn;
        System.Windows.Forms.Panel vpanel;
        int repeat, aspIdx; float speed = 1f; long pendingSeek = -1;
        DateTime lastMove = DateTime.Now; System.Drawing.Point lastPos; DispatcherTimer toastTimer;
        readonly float[] speeds = { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f };
        static readonly string[] AspVal = { "", "16:9", "4:3", "21:9", "1:1" };
        static readonly string[] AspName = { "Auto", "16:9", "4:3", "21:9", "1:1" };
        static readonly Brush ActiveBg = Frozen(new SolidColorBrush(Color.FromArgb(0x45, 0x2F, 0x8C, 0xFF)));
        static Brush Frozen(Brush b) { b.Freeze(); return b; }
        readonly Random rnd = new Random();
        readonly Rectangle[] bars = new Rectangle[NB];
        readonly double[] lvl = new double[NB];
        RadioButton[] radios;
        Storyboard ringSb, discSb;
        DispatcherTimer timer;
        WindowState restoreState;
        readonly ObservableCollection<Item> shown = new ObservableCollection<Item>();
        readonly ObservableCollection<List<Item>> cardRows = new ObservableCollection<List<Item>>();
        List<Item> allShown = new List<Item>();
        readonly HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Item> itemsByPath = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        readonly SemaphoreSlim scanGate = new SemaphoreSlim(1, 1);
        readonly string[] launchPaths;
        readonly PlayerSettings savedSettings;
        double vol = 80; bool muted, volBusy;
        Item pendingItem; double lastAvg; long lastUi; bool bgOn;
        bool settingsReady;
        int queueDragIndex = -1;
        int sortMode;
        int cardColumns, cardItemCount;
        Point queueDragOrigin;
        long librarySaveVersion;
        readonly object librarySaveLock = new object();
        DispatcherTimer settingsTimer;
        MMDeviceEnumerator audioEnumerator;
        MMDevice audioDevice;
        AudioSessionControl audioSession;
        long audioMeterRefreshAt;
        bool audioOptionsReady;
        bool eqUpdating;
        float[] eqBandValues = Array.Empty<float>();
        int eqCustomPresetIndex = -1;
        System.Windows.Forms.NotifyIcon trayIcon;
        HwndSource windowSource;
        Storyboard homeSb; bool homeOn, homeStarted;
        // GPU fraca (renderização por software): desliga os efeitos mais pesados
        readonly bool lowGpu = App.SafeMode || (RenderCapability.Tier >> 16) == 0;
        int animIdx, fillGen, playSeq, refreshGeneration; readonly object playLock = new object();
        DispatcherTimer searchTimer; bool warming, vizHooked, isPlaying; long vizLast, tgtAt, lastVpMove;
        readonly double[] tgt = new double[NB];
        // Tempo/duração vêm dos eventos do VLC (nada de consultar o player pela thread da tela)
        long curTime, curLen;
        string lastSettingsKey; long lastSavedPos = -1;
        // Aleatório sem repetição: cada faixa uma vez por rodada
        List<Item> shufList; int shufIdx = -1, shufLibCount, curIdx = -1;
        WindowsFormsHost videoHost;

        public MainWindow() : this(Array.Empty<string>()) { }

        public MainWindow(string[] paths)
        {
            launchPaths = paths ?? Array.Empty<string>();
            savedSettings = LoadSettings();
            vol = Math.Max(0, Math.Min(100, savedSettings.Volume));
            muted = savedSettings.Muted;
            speed = speeds.Contains(savedSettings.Speed) ? savedSettings.Speed : 1f;
            shuffle = savedSettings.Shuffle;
            repeat = Math.Max(0, Math.Min(2, savedSettings.Repeat));
            pendingSeek = -1;
            InitializeComponent();
        }

        async void Window_Loaded(object s, RoutedEventArgs e)
        {
            try { await StartupAsync(); }
            catch (Exception ex)
            {
                App.LogError("Startup", ex);
                try { Toast("Erro ao iniciar: " + ex.Message); } catch { }
            }
            if (prefs.AutoUpdate) _ = UpdateAutoCheckAsync();   // procura versão nova nos Releases do GitHub
        }

        async Task StartupAsync()
        {
            // 1) Interface: tudo leve, a janela aparece na hora
            if (!App.SafeMode) BuildFx();
            Intro();
            Window_StateChanged(null, EventArgs.Empty);
            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            toastTimer.Tick += (a, b) => { toastTimer.Stop(); ToastPop.IsOpen = false; };
            windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            windowSource?.AddHook(WindowHook);
            ApplyMediaKeys();
            searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
            searchTimer.Tick += (a, b) => { searchTimer.Stop(); Refresh(); };
            settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            settingsTimer.Tick += (a, b) => SaveSettings();
            foreach (var sp in speeds)
            {
                var sb2 = new Button { Style = (Style)FindResource("MenuBtn"), Content = sp.ToString("0.##") + "x", HorizontalContentAlignment = HorizontalAlignment.Center, Tag = sp };
                sb2.Click += (a, b) => SetSpeed((float)((Button)a).Tag);
                SpdGrid.Children.Add(sb2);
            }
            MarkSpeed();
            // handledEventsToo = true: o Slider marca o clique como "tratado", e sem isso o pulo nunca era aplicado
            Seek.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((a, b) => { dragging = true; SyncTimer(); }), true);
            Seek.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((a, b) => FinishSeek()), true);
            Seek.ValueChanged += (a, b) => { if (dragging) CurT.Text = Fmt((long)Seek.Value); };

            ringSb = MakeSpin(RingRot, 2.6); discSb = MakeSpin(DiscRot, 9);
            for (int i = 0; i < NB; i++)
            {
                bars[i] = new Rectangle { Width = 9, Height = 6, RadiusX = 4.5, RadiusY = 4.5, Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Bottom, Fill = (Brush)FindResource("GradV") };
                Bars.Children.Add(bars[i]);
            }
            radios = new[] { NavHome, NavMusic, NavVideo, NavQueue, NavNow, NavPlaylists, NavYouTube, NavArtists, NavAlbums, NavSettings, NavAbout };
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += Tick; // só liga enquanto toca (SyncTimer)
            Cards.ItemsSource = cardRows;
            QueueItems.ItemsSource = queue;
            PlaylistList.ItemsSource = playlists;
            PlaylistList.DisplayMemberPath = nameof(SavedPlaylist.Name);
            PlaylistTracks.ItemsSource = selectedPlaylistItems;
            volBusy = true; VolSlider.Value = vol; volBusy = false;
            ApplyPlaybackOptions();
            UpdateVolIcon();
            Deactivated += (a, b) => ClosePops();
            InitYouTube();
            InitYtSearch();
            LoadTitleIcon();
            Navigate(0);
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(WarmPopups));

            // O motor de mídia (VLC) começa a carregar já, em paralelo com a biblioteca (antes esperava a biblioteca inteira)
            var vlcTask = Task.Run(() =>
            {
                string vlcDir = ResolveLibVlcDir();   // pasta libvlc ao lado do EXE, ou o VLC embutido no EXE único
                if (vlcDir != null) VLC.Core.Initialize(vlcDir); else VLC.Core.Initialize();
                var vlcOptions = new List<string> { "--no-video-title-show", "--no-osd", "--no-snapshot-preview", "--no-stats", App.SafeMode ? "--avcodec-hw=none" : "--avcodec-hw=any", "--file-caching=400" };
                if (prefs.LevelVolume) vlcOptions.AddRange(new[] { "--audio-filter=normvol", "--norm-buff-size=20", "--norm-max-level=2.0" });   // nivela o volume entre músicas
                var engine = new VLC.LibVLC(vlcOptions.ToArray());
                var player = new VLC.MediaPlayer(engine) { EnableMouseInput = false, EnableKeyInput = false };
                return (engine, player);
            });

            await LoadLibraryAsync();
            recent.Clear(); recent.AddRange(Enumerable.Reverse(lib).Take(30));
            RestoreQueue();
            LoadPlaylists();
            LoadRoots();
            SyncYtFolderPlaylist();
            LoadPlays(); ApplyPlayCounts(); RefreshSmartPlaylists(); UpdateSmartButton();
            RefreshPlaylistTracks();

            // 2) Motor de mídia (VLC): já estava carregando em segundo plano
            VLC.LibVLC l2; VLC.MediaPlayer m2;
            try { (l2, m2) = await vlcTask; }
            catch (Exception ex)
            {
                App.LogError("VLC", ex);
                MessageBox.Show("Não foi possível iniciar o motor de mídia (VLC):\n" + ex.Message + "\n\nVerifique se a pasta \"libvlc\" está junto do AzulGrooveMusic.exe.",
                    "Azul Groove Music", MessageBoxButton.OK, MessageBoxImage.Error);
                Close(); return;
            }
            _lib = l2; _mp = m2;
            try { _mp.Mute = false; }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not initialize VLC mute state: " + ex.Message); }
            ApplyVolume();
            _mp.TimeChanged += (a, b) => { Volatile.Write(ref curTime, b.Time); OnPlayTime(b.Time); };
            _mp.LengthChanged += (a, b) => Volatile.Write(ref curLen, b.Length);
            _mp.Playing += (a, b) => UI(() => { if (pendingSeek >= 0) { _mp.Time = pendingSeek; pendingSeek = -1; } ApplySpeed(); ApplyVolume(); SetPlayState(true); });
            _mp.Paused += (a, b) => UI(() => { SetPlayState(false); SaveSettings(true); });
            _mp.Stopped += (a, b) => UI(() => SetPlayState(false));
            _mp.EndReached += (a, b) => UI(() => Step(1, true));
            _mp.EncounteredError += (a, b) => UI(() => NowTitle.Text = "Não foi possível reproduzir este arquivo");
            if (vpanel != null && vpanel.IsHandleCreated) _mp.Hwnd = vpanel.Handle;
            InitializeAudioOptions();
            if (pendingItem != null) { var it0 = pendingItem; pendingItem = null; Play(it0); }
            else if (prefs.ResumePlayback && launchPaths.Length == 0 && !string.IsNullOrWhiteSpace(savedSettings.CurrentFile) && File.Exists(savedSettings.CurrentFile))
            {
                itemsByPath.TryGetValue(savedSettings.CurrentFile, out Item restored);
                if (restored == null && AddOne(savedSettings.CurrentFile)) restored = lib[lib.Count - 1];
                if (restored != null) { pendingSeek = Math.Max(0, savedSettings.PositionMs); Play(restored); }
            }
            settingsReady = true;
            if (launchPaths.Length > 0) pendingSeek = -1;
            if (launchPaths.Length > 0) await AddPathsAsync(launchPaths, true);
            settingsTimer.Start();
            _ = StartupRefreshAsync();   // confere o que mudou enquanto o app estava fechado e liga o vigia de pastas
        }

        IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0312 && HandleMediaHotkey((int)wParam)) { handled = true; return IntPtr.Zero; }   // WM_HOTKEY: teclas de mídia globais
            if (message != 0x0319) return IntPtr.Zero;
            int command = (int)(((long)lParam >> 16) & 0x7ff);
            switch (command)
            {
                case 11: Step(1); break;
                case 12: Prev_Click(null, null); break;
                case 13: _mp?.Stop(); break;
                case 14: Play_Click(null, null); break;
                case 46: if (cur == null || !_mp.IsPlaying) Play_Click(null, null); break;
                case 47: if (_mp != null && _mp.IsPlaying) _mp.Pause(); break;
                default: return IntPtr.Zero;
            }
            handled = true;
            return new IntPtr(1);
        }

        PlayerSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                    return JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(SettingsFile)) ?? new PlayerSettings();
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not load player settings: " + ex.Message); }
            return new PlayerSettings();
        }

        void ApplyPlaybackOptions()
        {
            ShufBtn.Foreground = shuffle ? (Brush)FindResource("Cyan") : (Brush)FindResource("Foam");
            ShufBtn.Background = shuffle ? ActiveBg : Brushes.Transparent;
            RepBtn.Content = repeat == 2 ? "\uE8ED" : "\uE8EE";
            RepBtn.Foreground = repeat > 0 ? (Brush)FindResource("Cyan") : (Brush)FindResource("Foam");
            RepBtn.Background = repeat > 0 ? ActiveBg : Brushes.Transparent;
            SpdBtn.Content = speed.ToString("0.##") + "x";
            MarkSpeed();
        }

        void InitializeAudioOptions()
        {
            audioOptionsReady = false;
            AudioOutputBox.Items.Clear();
            AudioOutputBox.Items.Add(new AudioDeviceChoice { Name = "Padrão do sistema", DeviceId = null });
            try
            {
                var devices = _mp.AudioOutputDeviceEnum;
                if (devices != null)
                    foreach (var device in devices)
                        AudioOutputBox.Items.Add(new AudioDeviceChoice
                        {
                            Name = string.IsNullOrWhiteSpace(device.Description) ? device.DeviceIdentifier : device.Description,
                            DeviceId = device.DeviceIdentifier
                        });

                EqPresetBox.Items.Clear();
                EqPresetBox.Items.Add(new ComboBoxItem { Content = "Desativado", Tag = -1 });
                using (var equalizerInfo = new VLC.Equalizer())
                {
                    uint presetCount = equalizerInfo.PresetCount;
                    for (uint i = 0; i < presetCount; i++)
                        EqPresetBox.Items.Add(new ComboBoxItem { Content = equalizerInfo.PresetName(i), Tag = (int)i });
                    eqCustomPresetIndex = EqPresetBox.Items.Count;
                    EqPresetBox.Items.Add(new ComboBoxItem { Content = "Personalizado", Tag = -2 });

                    int bandCount = (int)equalizerInfo.BandCount;
                    eqBandValues = new float[bandCount];
                    EqBandsPanel.Children.Clear();
                    for (int i = 0; i < bandCount; i++)
                    {
                        string label = equalizerInfo.BandFrequency((uint)i).ToString("0") + " Hz";
                        var column = new StackPanel { Width = 42, Margin = new Thickness(2, 0, 2, 0), HorizontalAlignment = HorizontalAlignment.Center };
                        var slider = new Slider
                        {
                            Minimum = -20,
                            Maximum = 20,
                            Value = 0,
                            Height = 88,
                            Orientation = Orientation.Vertical,
                            Tag = i,
                            ToolTip = label
                        };
                        slider.ValueChanged += EqBand_Changed;
                        column.Children.Add(slider);
                        column.Children.Add(new TextBlock { Text = label, FontSize = 9, TextAlignment = TextAlignment.Center, Foreground = (Brush)FindResource("Mute") });
                        EqBandsPanel.Children.Add(column);
                    }
                }
                AudioOutputBox.SelectedIndex = 0;
                EqPresetBox.SelectedIndex = 0;
                audioOptionsReady = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Could not initialize VLC audio controls: " + ex.Message);
                Toast("Controles de áudio avançados indisponíveis");
            }
        }

        void AudioOutput_Changed(object s, SelectionChangedEventArgs e)
        {
            if (!audioOptionsReady || _mp == null || !(AudioOutputBox.SelectedItem is AudioDeviceChoice choice)) return;
            try
            {
                _mp.SetOutputDevice(choice.DeviceId, null);
                audioMeterRefreshAt = 0;
                Toast("Saída de áudio: " + choice.Name);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Could not select audio output: " + ex.Message);
                Toast("Não foi possível selecionar essa saída de áudio");
            }
        }

        void EqPreset_Changed(object s, SelectionChangedEventArgs e)
        {
            if (!audioOptionsReady || eqUpdating || _mp == null || !(EqPresetBox.SelectedItem is ComboBoxItem choice)) return;
            int preset = (int)choice.Tag;
            if (preset == -2) { ApplyCustomEqualizer(); return; }
            try
            {
                eqUpdating = true;
                if (preset < 0)
                {
                    _mp.UnsetEqualizer();
                    Array.Clear(eqBandValues, 0, eqBandValues.Length);
                }
                else
                {
                    using (var equalizer = new VLC.Equalizer((uint)preset))
                    {
                        for (int i = 0; i < eqBandValues.Length; i++)
                            eqBandValues[i] = equalizer.Amp((uint)i);
                        _mp.SetEqualizer(equalizer);
                    }
                }
                UpdateEqBandSliders();
                eqUpdating = false;
                Toast(preset < 0 ? "Equalizador desativado" : "Preset: " + choice.Content);
            }
            catch (Exception ex)
            {
                eqUpdating = false;
                System.Diagnostics.Trace.WriteLine("Could not apply equalizer preset: " + ex.Message);
                Toast("Não foi possível aplicar o equalizador");
            }
        }

        void EqBand_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!audioOptionsReady || eqUpdating || !(s is Slider slider) || !(slider.Tag is int band)) return;
            eqBandValues[band] = (float)e.NewValue;
            if (EqPresetBox.SelectedIndex != eqCustomPresetIndex)
            {
                eqUpdating = true;
                EqPresetBox.SelectedIndex = eqCustomPresetIndex;
                eqUpdating = false;
            }
            ApplyCustomEqualizer();
        }

        void UpdateEqBandSliders()
        {
            for (int i = 0; i < EqBandsPanel.Children.Count && i < eqBandValues.Length; i++)
                if (((StackPanel)EqBandsPanel.Children[i]).Children[0] is Slider slider)
                    slider.Value = eqBandValues[i];
        }

        void ApplyCustomEqualizer()
        {
            try
            {
                using (var equalizer = new VLC.Equalizer())
                {
                    for (int i = 0; i < eqBandValues.Length; i++)
                        equalizer.SetAmp(eqBandValues[i], (uint)i);
                    _mp.SetEqualizer(equalizer);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Could not apply custom equalizer: " + ex.Message);
                Toast("Não foi possível aplicar o equalizador");
            }
        }

        void ExternalSubtitle_Click(object s, RoutedEventArgs e)
        {
            MorePop.IsOpen = false;
            if (_mp == null || cur == null) { Toast("Abra um vídeo antes de adicionar uma legenda"); return; }
            var dialog = new OpenFileDialog { Filter = "Legendas SubRip|*.srt|Todos os arquivos|*.*", CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            try
            {
                if (_mp.AddSlave(VLC.MediaSlaveType.Subtitle, dialog.FileName, true))
                    Toast("Legenda adicionada");
                else
                    Toast("O VLC não conseguiu adicionar essa legenda");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Could not add external subtitle: " + ex.Message);
                Toast("Não foi possível adicionar a legenda");
            }
        }

        void RefreshAudioMeter()
        {
            try
            {
                audioSession?.Dispose();
                audioSession = null;
                audioDevice?.Dispose();
                audioDevice = null;
                audioEnumerator?.Dispose();
                audioEnumerator = new MMDeviceEnumerator();
                var devices = audioEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                int processId = Environment.ProcessId;
                foreach (var candidateDevice in devices)
                {
                    bool found = false;
                    try
                    {
                        var sessions = candidateDevice.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            if (session.GetProcessID == processId)
                            {
                                audioSession = session;
                                audioDevice = candidateDevice;
                                found = true;
                                break;
                            }
                            session.Dispose();
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not inspect audio device " + candidateDevice.ID + ": " + ex.Message); }
                    if (found) break;
                    candidateDevice.Dispose();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Could not locate the app audio session: " + ex.Message);
                audioSession?.Dispose();
                audioSession = null;
            }
        }

        float ReadAudioPeak(long now)
        {
            // só procura a sessão de áudio de novo se ela sumiu (ou se a saída mudou); antes varria todos os dispositivos a cada 2 s
            bool forced = audioMeterRefreshAt == 0;
            bool retry = audioSession == null && now - audioMeterRefreshAt > 2000;
            if (forced || retry)
            {
                audioMeterRefreshAt = Math.Max(1, now);
                RefreshAudioMeter();
            }
            try { return audioSession?.AudioMeterInformation.MasterPeakValue ?? 0; }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Could not read the app audio level: " + ex.Message);
                try { audioSession?.Dispose(); } catch { }
                audioSession = null;
                audioMeterRefreshAt = Math.Max(1, now);
                return 0;
            }
        }

        void RestoreQueue()
        {
            foreach (string path in savedSettings.Queue ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(path)) continue;   // sem File.Exists: em HD de rede/externo isso atrasava a abertura (o arquivo é conferido ao tocar)
                if (itemsByPath.TryGetValue(path, out Item item)) queue.Add(item);
            }
            RefreshQueue();
        }

        // Só grava quando algo mudou. A posição da faixa não conta como mudança (senão gravaria a cada tick):
        // ela é salva ao pausar, ao fechar e a cada 30 s tocando.
        void SaveSettings(bool force = false)
        {
            if (!settingsReady) return;
            try
            {
                long pos = cur != null && _mp != null ? Math.Max(0, Volatile.Read(ref curTime)) : Math.Max(0, pendingSeek);
                var current = new PlayerSettings
                {
                    Volume = vol,
                    Muted = muted,
                    Speed = speed,
                    Shuffle = shuffle,
                    Repeat = repeat,
                    CurrentFile = cur?.FilePath,
                    PositionMs = 0,
                    Queue = queue.Select(item => item.FilePath).ToList()
                };
                string key = JsonSerializer.Serialize(current);
                bool changed = key != lastSettingsKey;
                bool posDue = lastSavedPos < 0 || (isPlaying && Math.Abs(pos - lastSavedPos) > 30000) || (force && pos != lastSavedPos);
                if (!changed && !posDue) return;
                current.PositionMs = pos;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsFile));
                string temp = SettingsFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(current));
                File.Move(temp, SettingsFile, true);
                lastSettingsKey = key; lastSavedPos = pos;
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not save player settings: " + ex.Message); }
        }

        void AddMore(int n)
        {
            int c = shown.Count, end = Math.Min(allShown.Count, c + n);
            for (int i = c; i < end; i++) shown.Add(allShown[i]);
            BuildCardRows();
        }
        void BuildCardRows()
        {
            if (Cards == null) return;
            int columns = Math.Max(1, (int)Math.Floor(Math.Max(1, Cards.ActualWidth - 24) / 200));
            if (columns == cardColumns && shown.Count >= cardItemCount)
            {
                if (cardItemCount == shown.Count) return;
                int start = cardRows.Count * columns;
                if (cardRows.Count > 0 && cardItemCount % columns != 0)
                {
                    int rowStart = (cardRows.Count - 1) * columns;
                    cardRows[cardRows.Count - 1] = shown.Skip(rowStart).Take(columns).ToList();
                    start = rowStart + columns;
                }
                for (; start < shown.Count; start += columns)
                    cardRows.Add(shown.Skip(start).Take(columns).ToList());
                cardItemCount = shown.Count;
                return;
            }

            cardColumns = columns;
            cardItemCount = shown.Count;
            cardRows.Clear();
            for (int start = 0; start < shown.Count; start += columns)
                cardRows.Add(shown.Skip(start).Take(columns).ToList());
        }

        void Cards_Loaded(object s, RoutedEventArgs e)
        {
            var scrollViewer = FindVisualChild<ScrollViewer>(Cards);
            if (scrollViewer != null) scrollViewer.ScrollChanged += Lib_Scroll;
        }

        static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result) return result;
                var nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        void Cards_SizeChanged(object s, SizeChangedEventArgs e) => BuildCardRows();

        void ContinueFill(int gen)
        {
            if (gen != fillGen || shown.Count >= allShown.Count || shown.Count >= 60) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (gen != fillGen) return; AddMore(12); ContinueFill(gen); }));
        }
        void Lib_Scroll(object s, ScrollChangedEventArgs e)
        {
            if (shown.Count < allShown.Count && e.VerticalOffset + e.ViewportHeight > e.ExtentHeight - 500)
            {
                animIdx = 0;
                AddMore(30);
            }
        }

        // ---------- Janela ----------
        void Full_Click(object s, RoutedEventArgs e) => ToggleFull();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);
        const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;
        int rx, ry, rw, rh;

        void ToggleFull()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!full)
            {
                // guarda a posição normal (em pixels), mesmo que a janela estivesse maximizada
                var m = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice;
                var rb = RestoreBounds;
                rx = (int)Math.Round(rb.X * m.M11); ry = (int)Math.Round(rb.Y * m.M22);
                rw = (int)Math.Round(rb.Width * m.M11); rh = (int)Math.Round(rb.Height * m.M22);
                restoreState = WindowState;
                full = true; lastMove = DateTime.Now;
                if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
                TitleBar.Visibility = Visibility.Collapsed; Side.Visibility = Visibility.Collapsed;
                TitleRow.Height = new GridLength(0); SideCol.Width = new GridLength(0);
                ContentHost.Margin = new Thickness(0); PlayerView.Margin = new Thickness(0);
                Root.Margin = new Thickness(0);
                if (cur != null) Navigate(4);
                Toast("Tela cheia  ·  Esc para sair");
                var b = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
                SetWindowPos(hwnd, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SWP_NOACTIVATE);
            }
            else
            {
                full = false;
                ShowBar();
                TitleBar.Visibility = Visibility.Visible; Side.Visibility = Visibility.Visible;
                TitleRow.Height = new GridLength(44); SideCol.Width = new GridLength(290);
                ContentHost.Margin = new Thickness(40, 18, 24, 0); PlayerView.Margin = new Thickness(-16, -18, -8, 0);
                SetWindowPos(hwnd, HWND_NOTOPMOST, rx, ry, rw, rh, SWP_NOACTIVATE);
                if (restoreState == WindowState.Maximized) WindowState = WindowState.Maximized;
                Window_StateChanged(null, EventArgs.Empty);
            }
        }
        void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void Close_Click(object s, RoutedEventArgs e) => Close();
        void Window_StateChanged(object s, EventArgs e)
        {
            if (Root == null || MaxBtn == null) return;
            if (WindowState == WindowState.Minimized && !prefs.MinimizeToTray)
            {
                Drift(false); Fx(false); // minimiza normalmente (barra de tarefas): só pausa as animações
                return;
            }
            if (WindowState == WindowState.Minimized)
            {
                EnsureTray();
                trayIcon.Visible = true;
                Hide();
                Drift(false); Fx(false); VizOn(false); SyncTimer(); // escondido na bandeja: nada de animação nem timer
                return;
            }
            Drift(WindowState != WindowState.Minimized && page != 4);
            Fx(WindowState != WindowState.Minimized && page == 0);
            bool mx = WindowState == WindowState.Maximized && !full;
            Root.Margin = mx ? new Thickness(7) : new Thickness(0);
            MaxBtn.Content = mx ? "\uE923" : "\uE922";
        }

        void Window_KeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { Search.Focus(); Search.SelectAll(); e.Handled = true; return; }   // Ctrl+F: ir para a busca
            if (Keyboard.FocusedElement is TextBox) { if (e.Key == Key.Escape) { Keyboard.ClearFocus(); } return; }
            switch (e.Key)
            {
                case Key.Space: Play_Click(null, null); e.Handled = true; break;
                case Key.Right: Skip(5000); e.Handled = true; break;
                case Key.Left: Skip(-5000); e.Handled = true; break;
                case Key.J: Skip(-10000); break;
                case Key.L: Skip(30000); break;
                case Key.OemPlus: case Key.Add: StepSpeed(1); break;
                case Key.OemMinus: case Key.Subtract: StepSpeed(-1); break;
                case Key.Up: VolSlider.Value = Math.Min(100, VolSlider.Value + 5); Toast("Volume " + (int)VolSlider.Value + "%"); e.Handled = true; break;
                case Key.Down: VolSlider.Value = Math.Max(0, VolSlider.Value - 5); Toast("Volume " + (int)VolSlider.Value + "%"); e.Handled = true; break;
                case Key.N: Step(1); break;
                case Key.P: Prev_Click(null, null); break;
                case Key.M: Mute_Click(null, null); break;
                case Key.S: Shuf_Click(null, null); break;
                case Key.R: Rep_Click(null, null); break;
                case Key.F: ToggleFull(); break;
                case Key.Escape: if (full) ToggleFull(); break;
                case Key.MediaPlayPause: Play_Click(null, null); e.Handled = true; break;
                case Key.MediaNextTrack: Step(1); e.Handled = true; break;
                case Key.MediaPreviousTrack: Prev_Click(null, null); e.Handled = true; break;
                case Key.MediaStop: _mp?.Stop(); e.Handled = true; break;
            }
        }

        void Window_Closing(object s, CancelEventArgs e)
        {
            AppLog.Write("SESSAO", "Janela principal fechando. Quem pediu o fechamento:" + Environment.NewLine + Environment.StackTrace);
            settingsTimer?.Stop();
            StopFolderWatching();
            try { ytDlCts.Cancel(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not stop YouTube downloads: " + ex.Message); }
            try { ytCts?.Cancel(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not stop YouTube sync: " + ex.Message); }
            SaveYtSettings();
            SaveSettings(true);
            if (trayIcon != null) { trayIcon.Visible = false; trayIcon.ContextMenuStrip?.Dispose(); trayIcon.Dispose(); }
            UnregisterMediaKeys();
            windowSource?.RemoveHook(WindowHook);
            audioSession?.Dispose();
            audioDevice?.Dispose();
            audioEnumerator?.Dispose();
            try
            {
                timer?.Stop();
                VizOn(false);
                System.Threading.Interlocked.Increment(ref playSeq);
                lock (playLock) { _mp?.Stop(); _mp?.Dispose(); _lib?.Dispose(); }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not shut down media playback cleanly: " + ex.Message); }
        }
    }
}
