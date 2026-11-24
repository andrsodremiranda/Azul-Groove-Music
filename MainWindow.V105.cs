using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VLC = LibVLCSharp.Shared;

namespace AzulGroove
{
    sealed class LrcLine
    {
        public long Ms;
        public string Text;
    }

    // Novidades da 1.0.5: letras (.lrc), favoritos, transição suave, mini player, cor de destaque, duplicadas,
    // importar .m3u, resumo de uso, bloqueio na fila inteligente, limite/limpeza das músicas automáticas e mais atalhos.
    public partial class MainWindow
    {
        static readonly string V105Dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove");

        // Grava em arquivo temporário e troca no fim: nunca deixa um JSON pela metade se o app fechar no meio
        static void WriteAtomic(string file, string text)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                string temp = file + ".tmp";
                File.WriteAllText(temp, text);
                File.Move(temp, file, true);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not save " + file + ": " + ex.Message); }
        }

        // ---------- Favoritos (♥) ----------
        static readonly string FavFile = System.IO.Path.Combine(V105Dir, "favorites.json");
        readonly HashSet<string> favs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void LoadFavs()
        {
            try
            {
                if (!File.Exists(FavFile)) return;
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FavFile));
                if (list == null) return;
                foreach (string p in list) if (!string.IsNullOrWhiteSpace(p)) favs.Add(p);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not load favorites: " + ex.Message); }
        }

        void SaveFavs()
        {
            string json = JsonSerializer.Serialize(favs.ToList());
            _ = Task.Run(() => WriteAtomic(FavFile, json));
        }

        void ToggleFav(Item it)
        {
            if (it == null) return;
            if (it.IsStream || it.IsVideo) { Toast("Só dá para favoritar músicas do computador"); return; }
            bool on = favs.Add(it.FilePath);
            if (!on) favs.Remove(it.FilePath);
            SaveFavs(); UpdateFavBtn(); RefreshSmartPlaylists();
            Toast(on ? "♥ Adicionada às favoritas" : "Removida das favoritas");
        }

        void UpdateFavBtn()
        {
            if (FavBtn == null) return;
            bool on = cur != null && !cur.IsStream && favs.Contains(cur.FilePath);
            FavBtn.Content = on ? "♥" : "♡";
            SetAccentOn(FavBtn, on);
        }

        void Fav_Click(object s, RoutedEventArgs e) => ToggleFav(cur);
        void FavMenu_Click(object s, RoutedEventArgs e) => ToggleFav(MenuItemOf(s));

        List<string> FavoritePaths() =>
            lib.Where(i => !i.IsVideo && !i.IsStream && favs.Contains(i.FilePath)).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).Select(i => i.FilePath).ToList();

        // ---------- Letras sincronizadas (.lrc ao lado do arquivo, ou letra gravada na tag) ----------
        static readonly Regex LrcStamp = new Regex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);
        List<LrcLine> lyr;
        int lyrIdx = -2;

        static List<LrcLine> ReadLrc(string mediaPath)
        {
            string text = null;
            try
            {
                string lrc = System.IO.Path.ChangeExtension(mediaPath, ".lrc");
                if (File.Exists(lrc)) text = File.ReadAllText(lrc);
                else using (var f = TagLib.File.Create(mediaPath)) text = f.Tag.Lyrics;
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not read lyrics: " + ex.Message); return null; }
            return ParseLrc(text);
        }

        static List<LrcLine> ParseLrc(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var list = new List<LrcLine>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                MatchCollection stamps = LrcStamp.Matches(line);
                if (stamps.Count == 0) continue;
                string words = LrcStamp.Replace(line, string.Empty).Trim();
                foreach (Match m in stamps)
                {
                    if (!int.TryParse(m.Groups[1].Value, out int min)) continue;
                    if (!double.TryParse(m.Groups[2].Value.Replace(':', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double sec)) continue;
                    list.Add(new LrcLine { Ms = (long)((min * 60 + sec) * 1000), Text = words });
                }
            }
            if (list.Count < 2) return null;   // letra sem marcação de tempo não dá para sincronizar
            list.Sort((a, b) => a.Ms.CompareTo(b.Ms));
            return list;
        }

        // Lê o arquivo fora da thread da tela; se a faixa já mudou quando terminar, descarta
        void LoadLyrics(Item it)
        {
            lyr = null; lyrIdx = -2; ShowLyrics(false);
            if (!prefs.Lyrics || it == null || it.IsVideo || it.IsStream) return;
            string path = it.FilePath;
            Task.Run(() => ReadLrc(path)).ContinueWith(t =>
            {
                List<LrcLine> found = t.IsFaulted || t.IsCanceled ? null : t.Result;
                if (found == null) return;
                UI(() =>
                {
                    if (!ReferenceEquals(cur, it)) return;
                    lyr = found; lyrIdx = -2; ShowLyrics(true); UpdateLyricUi(Volatile.Read(ref curTime));
                });
            });
        }

        void ShowLyrics(bool on)
        {
            if (LyrPanel == null) return;
            LyrPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            Bars.Visibility = on ? Visibility.Collapsed : Visibility.Visible;   // a letra ocupa o lugar do visualizador
            if (!on) LyrPrev.Text = LyrCur.Text = LyrNext.Text = string.Empty;
        }

        // Busca binária pela linha atual: só mexe na tela quando a linha muda
        void UpdateLyricUi(long t)
        {
            List<LrcLine> l = lyr;
            if (l == null || l.Count == 0) return;
            int lo = 0, hi = l.Count - 1, idx = -1;
            long at = t + 250;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (l[mid].Ms <= at) { idx = mid; lo = mid + 1; } else hi = mid - 1;
            }
            if (idx == lyrIdx) return;
            lyrIdx = idx;
            LyrPrev.Text = idx > 0 ? l[idx - 1].Text : string.Empty;
            LyrCur.Text = idx >= 0 && l[idx].Text.Length > 0 ? l[idx].Text : "♪";
            LyrNext.Text = idx + 1 < l.Count ? l[idx + 1].Text : string.Empty;
        }

        // ---------- Transição suave: a música some no fim e a próxima entra aos poucos ----------
        // (o player usa um único motor de áudio, então é fade-out + fade-in pelo volume, e não uma sobreposição)
        int lastFadeVol = -1;

        double FadeFactor()
        {
            int ms = prefs.FadeMs;
            if (ms <= 0 || cur == null || cur.IsVideo) return 1;
            long len = Volatile.Read(ref curLen), t = Volatile.Read(ref curTime);
            double f = 1;
            if (t >= 0 && t < ms) f = Math.Min(f, t / (double)ms);
            if (len > 0 && len - t < ms) f = Math.Min(f, Math.Max(0, (len - t) / (double)ms));
            return f < 0 ? 0 : f > 1 ? 1 : f;
        }

        // Chamado a cada 200 ms enquanto toca; só fala com o VLC quando o volume realmente mudou
        void FadeTick()
        {
            if (prefs.FadeMs <= 0 || muted || _mp == null) return;
            int v = (int)Math.Round(vol * FadeFactor());
            if (v == lastFadeVol) return;
            lastFadeVol = v;
            try { _mp.Volume = v; }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not apply fade volume: " + ex.Message); }
        }

        // ---------- Atalhos extras ----------
        bool HandleExtraKeys(KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            Key k = e.Key;
            if (ctrl)
            {
                switch (k)
                {
                    case Key.Q: Navigate(3); break;                      // Ctrl+Q: fila
                    case Key.L: if (cur != null) Navigate(4); break;     // Ctrl+L: tocando agora (letra)
                    case Key.M: ToggleMini(); break;                     // Ctrl+M: mini player
                    case Key.D: ToggleFav(cur); break;                   // Ctrl+D: favoritar
                    default: return false;
                }
                e.Handled = true; return true;
            }
            if (shift && (k == Key.Left || k == Key.Right)) { Skip(k == Key.Right ? 30000 : -30000); e.Handled = true; return true; }   // Shift+←/→: 30 s
            int digit = k >= Key.D0 && k <= Key.D9 ? k - Key.D0 : k >= Key.NumPad0 && k <= Key.NumPad9 ? k - Key.NumPad0 : -1;
            if (digit >= 0 && !shift) { SeekPercent(digit * 10); e.Handled = true; return true; }   // 0–9: pula para 0%–90%
            if (k == Key.H) { ToggleFav(cur); e.Handled = true; return true; }                      // H: favoritar
            return false;
        }

        void SeekPercent(int pct)
        {
            if (cur == null || _mp == null) return;
            long len = Volatile.Read(ref curLen);
            if (len <= 0) return;
            long t = len * pct / 100;
            var st = _mp.State;
            if (st == VLC.VLCState.Ended || st == VLC.VLCState.Stopped || st == VLC.VLCState.Error) { pendingSeek = t; Play(cur); }
            else { _mp.Time = t; Volatile.Write(ref curTime, t); if (!isPlaying) UpdateTimeUi(); }
            Toast(pct + "%");
        }

        // ---------- Mini player (janela pequena, sempre no topo) ----------
        Window mini;
        TextBlock miniTitle, miniSub;
        Border miniArt;
        Button miniPlay;
        bool appClosing;

        void Mini_Open_Click(object s, RoutedEventArgs e) { MorePop.IsOpen = false; ToggleMini(); }

        void ToggleMini()
        {
            if (mini != null) { mini.Close(); return; }
            var w = new Window
            {
                Width = 400, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                Topmost = true, ShowInTaskbar = false, Title = "Azul Groove Music", Icon = Icon,
                Background = (Brush)FindResource("Abyss"), Foreground = (Brush)FindResource("Foam"),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), UseLayoutRounding = true,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            Rect wa = SystemParameters.WorkArea;
            w.Left = wa.Right - 400 - 16; w.Top = wa.Bottom - 130;

            Button B(string glyph, string tip, RoutedEventHandler h)
            {
                var b = new Button { Style = (Style)FindResource("IconBtn"), Content = glyph, ToolTip = tip };
                b.Click += h;
                return b;
            }

            miniArt = new Border { Width = 76, Height = 76, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            miniTitle = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            miniSub = new TextBlock { FontSize = 11.5, Foreground = (Brush)FindResource("Mute"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) };
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
            texts.Children.Add(miniTitle); texts.Children.Add(miniSub);

            miniPlay = B("", "Reproduzir / Pausar", (a, b) => Play_Click(null, null));
            var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            top.Children.Add(B("", "Voltar à janela grande", (a, b) => w.Close()));
            top.Children.Add(B("", "Fechar o mini player", (a, b) => w.Close()));
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            bottom.Children.Add(B("", "Anterior", (a, b) => Prev_Click(null, null)));
            bottom.Children.Add(miniPlay);
            bottom.Children.Add(B("", "Próxima", (a, b) => Step(1)));
            var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 8, 6) };
            buttons.Children.Add(top); buttons.Children.Add(bottom);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(miniArt);
            Grid.SetColumn(texts, 1); grid.Children.Add(texts);
            Grid.SetColumn(buttons, 2); grid.Children.Add(buttons);
            w.Content = new Border { BorderBrush = (Brush)new BrushConverter().ConvertFromString("#554F8CFF"), BorderThickness = new Thickness(1), Child = grid };

            w.MouseLeftButtonDown += (a, b) => { if (b.ButtonState == MouseButtonState.Pressed) { try { w.DragMove(); } catch (InvalidOperationException) { } } };
            w.Closed += (a, b) =>
            {
                mini = null;
                if (!appClosing) RestoreMain();
            };
            mini = w;
            UpdateMini();
            w.Show();
            WindowState = WindowState.Minimized;   // a janela grande sai da frente (ou vai para a bandeja, se for o seu ajuste)
        }

        void RestoreMain()
        {
            if (trayIcon != null) { RestoreFromTray(); return; }
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        void UpdateMini()
        {
            if (mini == null) return;
            miniTitle.Text = NowTitle.Text;
            miniSub.Text = NowDetails.Text;
            miniArt.Background = MiniArt.Background;
            miniPlay.Content = isPlaying ? "" : "";
        }

        // ---------- Cor de destaque ----------
        // Troca as cores das pinceladas compartilhadas (botões, barras, degradês). Os brilhos de fundo fixos continuam azuis.
        static readonly string[][] Accents =
        {
            new[] { "Azul", "#2F8CFF", "#4FD1FF" },
            new[] { "Roxo", "#7C5CFF", "#C084FC" },
            new[] { "Verde", "#10B981", "#6EE7B7" },
            new[] { "Rosa", "#EC4899", "#F9A8D4" },
            new[] { "Laranja", "#F97316", "#FDBA74" }
        };

        // Por que isto mudou: antes o app MUDAVA a cor dos pincéis compartilhados. Só que o WPF "congela" (deixa somente leitura) os pincéis
        // usados em estilos e modelos, então parte da interface nunca mudava de cor e, em outros casos, a troca dava erro.
        // Agora o app SUBSTITUI os recursos por pincéis novos, e todo o XAML usa DynamicResource: tudo muda junto, na hora.
        static readonly string[] AccentAlphas = { "1A", "1E", "22", "26", "2E", "33", "3A", "3D", "44", "55", "66" };
        int appliedAccent;   // 0 = o azul que já vem no App.xaml (não precisa trocar nada na abertura)

        static T Frz<T>(T f) where T : Freezable { if (f.CanFreeze) f.Freeze(); return f; }

        static LinearGradientBrush AccentGrad(Color c1, Color c2, Point start, Point end)
        {
            var g = new LinearGradientBrush { StartPoint = start, EndPoint = end };
            g.GradientStops.Add(new GradientStop(c1, 0));
            g.GradientStops.Add(new GradientStop(c2, 1));
            return Frz(g);
        }

        // Liga/desliga o destaque (letra na cor de destaque) de um botão e continua acompanhando a cor quando ela muda
        static void SetAccentOn(Control c, bool on) => c.SetResourceReference(Control.ForegroundProperty, on ? "Cyan" : "Foam");
        static void SetAccentBg(Control c, bool on)
        {
            if (on) c.SetResourceReference(Control.BackgroundProperty, "Az44");
            else c.Background = Brushes.Transparent;
        }

        void ApplyAccent()
        {
            int index = Math.Max(0, Math.Min(prefs.Accent, Accents.Length - 1));
            if (index == appliedAccent) return;
            ResourceDictionary res = Application.Current.Resources;
            try
            {
                string[] a = Accents[index];
                Color c1 = (Color)ColorConverter.ConvertFromString(a[1]);
                Color c2 = (Color)ColorConverter.ConvertFromString(a[2]);
                res["AzureColor"] = c1; res["CyanColor"] = c2;
                res["Azure"] = Frz(new SolidColorBrush(c1));
                res["Cyan"] = Frz(new SolidColorBrush(c2));
                res["Grad"] = AccentGrad(c1, c2, new Point(0, 0), new Point(1, 0));
                res["GradV"] = AccentGrad(c2, c1, new Point(0, 0), new Point(0, 1));
                foreach (string h in AccentAlphas)
                {
                    byte al = Convert.ToByte(h, 16);
                    res["Az" + h] = Frz(new SolidColorBrush(Color.FromArgb(al, c1.R, c1.G, c1.B)));
                    res["Cy" + h] = Frz(new SolidColorBrush(Color.FromArgb(al, c2.R, c2.G, c2.B)));
                }
                appliedAccent = index;
            }
            catch (Exception ex) { App.LogError("Cor de destaque", ex); }
        }

        // ---------- Configurações novas ----------
        static readonly int[] FadeSteps = { 0, 2000, 4000, 6000 };
        static readonly int[] AutoMaxSteps = { 20, 60, 100, 200, 500 };
        static string FadeText(int ms) => ms <= 0 ? T.Get("Desligada") : (ms / 1000) + " s";

        void RefreshV105Settings()
        {
            if (PrefLyrics == null) return;
            if (PrefLangBtn != null) PrefLangBtn.Content = T.NameOf(T.Current);
            PrefLyrics.IsChecked = prefs.Lyrics;
            PrefFadeBtn.Content = FadeText(prefs.FadeMs);
            PrefAccentBtn.Content = T.Get(Accents[Math.Max(0, Math.Min(prefs.Accent, Accents.Length - 1))][0]);
            PrefAutoMaxBtn.Content = T.Get("{0} arquivos", prefs.SmartMaxFiles);
        }

        void V105AfterReset()
        {
            ApplyAccent(); lastFadeVol = -1; ApplyVolume(); LoadLyrics(cur); RefreshV105Settings();
        }

        void PrefLyrics_Click(object s, RoutedEventArgs e)
        {
            prefs.Lyrics = ((CheckBox)s).IsChecked == true;
            prefs.Save();
            LoadLyrics(cur);
        }

        void PrefFade_Click(object s, RoutedEventArgs e)
        {
            int i = Array.IndexOf(FadeSteps, prefs.FadeMs);
            prefs.FadeMs = FadeSteps[(i + 1) % FadeSteps.Length];
            prefs.Save();
            PrefFadeBtn.Content = FadeText(prefs.FadeMs);
            lastFadeVol = -1; ApplyVolume();
            Toast("Transição suave: " + FadeText(prefs.FadeMs));
        }

        void PrefAccent_Click(object s, RoutedEventArgs e)
        {
            prefs.Accent = (prefs.Accent + 1) % Accents.Length;
            prefs.Save();
            ApplyAccent();
            RefreshV105Settings();
            Toast(T.Get("Cor de destaque: {0}", T.Get(Accents[prefs.Accent][0])));
        }

        void PrefAutoMax_Click(object s, RoutedEventArgs e)
        {
            int i = Array.IndexOf(AutoMaxSteps, prefs.SmartMaxFiles);
            prefs.SmartMaxFiles = AutoMaxSteps[(i + 1) % AutoMaxSteps.Length];
            prefs.Save();
            RefreshV105Settings();
        }

        // ---------- Remoção em lote da biblioteca (duplicadas e músicas automáticas) ----------
        void RemoveItemsFromLibrary(ICollection<Item> items)
        {
            if (items == null || items.Count == 0) return;
            var set = new HashSet<Item>(items);
            if (cur != null && set.Contains(cur)) { _mp?.Stop(); cur = null; UpdateNow(); }
            for (int i = queue.Count - 1; i >= 0; i--) if (set.Contains(queue[i])) queue.RemoveAt(i);
            lib.RemoveAll(set.Contains);
            recent.RemoveAll(set.Contains);
            foreach (Item it in set) { known.Remove(it.FilePath); itemsByPath.Remove(it.FilePath); favs.Remove(it.FilePath); }
            SaveFavs(); Save(); Refresh(); RefreshQueue(); RefreshSmartPlaylists();
        }

        // Manda o arquivo para a Lixeira (dá para recuperar)
        static bool RecycleFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return true;
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return true;
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not recycle " + path + ": " + ex.Message); return false; }
        }

        // ---------- Músicas duplicadas ----------
        sealed class DupGroup
        {
            public Item Keep;
            public List<Item> Extra = new List<Item>();
        }

        static long DupSize(Item i)
        {
            try { return new FileInfo(i.FilePath).Length; } catch (Exception) { return 0; }
        }

        // Mesmo artista + título (sem acentos/pontuação) e duração com até 3 s de diferença
        List<DupGroup> FindDuplicates()
        {
            var result = new List<DupGroup>();
            var groups = lib.Where(i => !i.IsVideo && !i.IsStream && !string.IsNullOrWhiteSpace(i.Name))
                            .GroupBy(i => SmartNorm(i.Artist) + "|" + SmartNorm(i.Name));
            foreach (var g in groups)
            {
                if (g.Key.Length < 3 || g.Count() < 2) continue;
                var cluster = new List<Item>();
                foreach (Item it in g.OrderBy(i => i.Duration))
                {
                    if (cluster.Count > 0 && Math.Abs((it.Duration - cluster[0].Duration).TotalSeconds) > 3) { AddDup(result, cluster); cluster = new List<Item>(); }
                    cluster.Add(it);
                }
                AddDup(result, cluster);
            }
            return result.OrderBy(d => d.Keep.Artist, StringComparer.CurrentCultureIgnoreCase).ThenBy(d => d.Keep.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        void AddDup(List<DupGroup> result, List<Item> cluster)
        {
            if (cluster.Count < 2) return;
            Item keep = cluster.FirstOrDefault(i => ReferenceEquals(i, cur)) ?? cluster.OrderByDescending(DupSize).ThenBy(i => i.FilePath.Length).First();
            var d = new DupGroup { Keep = keep };
            foreach (Item i in cluster) if (!ReferenceEquals(i, keep)) d.Extra.Add(i);
            result.Add(d);
        }

        void FindDuplicates_Click(object s, RoutedEventArgs e)
        {
            List<DupGroup> groups = FindDuplicates();
            if (groups.Count == 0) { Toast("Nenhuma música duplicada encontrada"); return; }
            var mute = (Brush)FindResource("Mute");
            var cyan = (Brush)FindResource("Cyan");
            int extra = groups.Sum(g => g.Extra.Count);

            var layout = new StackPanel { Margin = new Thickness(30, 8, 30, 26) };
            layout.Children.Add(new TextBlock { Text = "Músicas duplicadas", FontSize = 22, FontWeight = FontWeights.SemiBold });
            layout.Children.Add(new TextBlock
            {
                Text = groups.Count + " músicas repetidas (" + extra + " cópias a mais). Em cada grupo fica a cópia de maior tamanho (✔); as outras (✖) saem da biblioteca.",
                FontSize = 12.5, Foreground = mute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10)
            });
            var list = new StackPanel();
            foreach (DupGroup g in groups.Take(60))
            {
                list.Children.Add(new TextBlock
                {
                    Text = (g.Keep.Artist.Length > 0 ? g.Keep.Artist + " – " : string.Empty) + g.Keep.Name,
                    FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 2)
                });
                list.Children.Add(new TextBlock { Text = "✔ " + g.Keep.FilePath, FontSize = 11.5, Foreground = cyan, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = g.Keep.FilePath });
                foreach (Item d in g.Extra)
                    list.Children.Add(new TextBlock { Text = "✖ " + d.FilePath, FontSize = 11.5, Foreground = mute, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = d.FilePath });
            }
            if (groups.Count > 60) list.Children.Add(new TextBlock { Text = "… e mais " + (groups.Count - 60) + " grupos", FontSize = 12, Foreground = mute, Margin = new Thickness(0, 10, 0, 0) });
            layout.Children.Add(new ScrollViewer { MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });

            var trash = BrandedCheckBox("Enviar as cópias para a Lixeira");
            trash.Margin = new Thickness(0, 14, 0, 0);
            layout.Children.Add(trash);
            layout.Children.Add(new TextBlock
            {
                Text = "Sem isso, as cópias só saem da biblioteca e podem voltar quando a pasta for atualizada.",
                FontSize = 11.5, Foreground = mute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
            });

            Window dialog = null;
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
            var cancel = BrandedButton("Cancelar", false, false); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 10, 0);
            var ok = BrandedButton("Remover " + extra + " cópias", true, false); ok.IsDefault = true;
            ok.Click += (a, b) => dialog.DialogResult = true;
            actions.Children.Add(cancel); actions.Children.Add(ok);
            layout.Children.Add(actions);
            dialog = NewBrandedWindow(620, layout, out Button dialogX);
            dialog.Title = "Músicas duplicadas";
            if (dialog.ShowDialog() != true) return;

            var gone = new List<Item>();
            int failed = 0;
            foreach (Item it in groups.SelectMany(g => g.Extra))
            {
                if (trash.IsChecked == true && !RecycleFile(it.FilePath)) { failed++; continue; }
                gone.Add(it);
            }
            RemoveItemsFromLibrary(gone);
            Toast(failed > 0 ? gone.Count + " removidas; " + failed + " não puderam ir para a Lixeira" : gone.Count + " cópias removidas");
        }

        // ---------- Importar playlist .m3u / .m3u8 ----------
        async void ImportPlaylist_Click(object s, RoutedEventArgs e)
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = "Importar playlist", Filter = "Playlists (*.m3u;*.m3u8)|*.m3u;*.m3u8", Multiselect = true };
            if (d.ShowDialog(this) != true) return;
            try { foreach (string f in d.FileNames) await ImportM3uAsync(f); }
            catch (Exception ex)
            {
                App.LogError("Importar playlist", ex);
                Toast("Não foi possível importar a playlist");
            }
        }

        async Task ImportM3uAsync(string file)
        {
            string text = File.ReadAllText(file, Encoding.UTF8);
            if (text.IndexOf('�') >= 0) text = File.ReadAllText(file, Encoding.Latin1);   // .m3u antigo, em ANSI
            string dir = System.IO.Path.GetDirectoryName(file) ?? string.Empty;
            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int missing = 0;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim().Trim('﻿');
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { missing++; continue; }   // endereços da internet não entram na biblioteca
                if (line.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) line = Uri.UnescapeDataString(line.Substring(8)).Replace('/', '\\');
                string full;
                try { full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(line) ? line : System.IO.Path.Combine(dir, line)); }
                catch (Exception) { missing++; continue; }
                if (!File.Exists(full)) { missing++; continue; }
                if (seen.Add(full)) paths.Add(full);
            }
            if (paths.Count == 0) { Toast("Nenhum arquivo da playlist foi encontrado"); return; }

            List<string> fresh = paths.Where(p => !known.Contains(p)).ToList();
            if (fresh.Count > 0) await AddPathsAsync(fresh, false);
            paths = paths.Where(itemsByPath.ContainsKey).ToList();   // só o que o app reconheceu como mídia
            if (paths.Count == 0) { Toast("Nenhum arquivo da playlist é uma mídia compatível"); return; }

            string baseName = System.IO.Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "Playlist importada";
            string name = baseName;
            for (int n = 2; playlists.Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)); n++) name = baseName + " (" + n + ")";
            var created = new SavedPlaylist { Name = name, Files = paths };
            AddPlaylistSorted(created);
            SavePlaylists();
            PlaylistList.SelectedItem = created;
            Toast("Playlist importada: " + paths.Count + " faixas" + (missing > 0 ? " (" + missing + " não encontradas)" : string.Empty));
        }

        // ---------- Resumo de uso ----------
        // "Dj Kantik - Música" com artista "Dj Kantik" não vira "Dj Kantik – Dj Kantik - Música"
        static string StatTitle(Item it)
        {
            string name = (it.Name ?? string.Empty).Trim(), artist = (it.Artist ?? string.Empty).Trim();
            if (artist.Length > 0 && name.Length > artist.Length && name.StartsWith(artist, StringComparison.OrdinalIgnoreCase))
            {
                string rest = name.Substring(artist.Length).TrimStart();
                if (rest.Length > 1 && (rest[0] == '-' || rest[0] == '–' || rest[0] == '—' || rest[0] == ':' || rest[0] == '|'))
                {
                    rest = rest.Substring(1).TrimStart();
                    if (rest.Length > 0) name = rest;
                }
            }
            return artist.Length > 0 ? artist + " – " + name : name;
        }

        void Stats_Click(object s, RoutedEventArgs e)
        {
            var music = lib.Where(i => !i.IsVideo && !i.IsStream).ToList();
            long totalPlays = 0; double secs = 0; int played = 0;
            var artists = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var hours = new int[24];
            var tracks = new List<KeyValuePair<Item, int>>();
            foreach (Item it in music)
            {
                if (!plays.TryGetValue(it.FilePath, out PlayStat st) || st.Count <= 0) continue;
                played++; totalPlays += st.Count; secs += st.Count * it.Duration.TotalSeconds;
                tracks.Add(new KeyValuePair<Item, int>(it, st.Count));
                if (!string.IsNullOrWhiteSpace(it.Artist)) { string ar = it.Artist.Trim(); artists.TryGetValue(ar, out int n); artists[ar] = n + st.Count; }   // sem espaços sobrando: "THOM ARES " e "THOM ARES" são o mesmo artista
                if (st.Hours != null && st.Hours.Length == 24) for (int h = 0; h < 24; h++) hours[h] += st.Hours[h];
            }
            if (totalPlays == 0) { Toast("Ainda não há reproduções para mostrar"); return; }

            var mute = (Brush)FindResource("Mute");
            var cyan = (Brush)FindResource("Cyan");
            var grad = (Brush)FindResource("Grad");
            var tileBg = (Brush)new BrushConverter().ConvertFromString("#1A78AAFF");
            var tileBorder = (Brush)new BrushConverter().ConvertFromString("#2278AAFF");
            TextBlock Heading(string t) => new TextBlock { Text = t, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = cyan, Margin = new Thickness(0, 16, 0, 7) };

            // Número grande + legenda
            UIElement Tile(string value, string label)
            {
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = value, FontSize = 18, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                sp.Children.Add(new TextBlock { Text = T.Get(label), FontSize = 11.5, Foreground = mute, TextTrimming = TextTrimming.CharacterEllipsis });
                return new Border { Background = tileBg, BorderBrush = tileBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(11, 7, 11, 8), Margin = new Thickness(0, 0, 8, 8), Child = sp };
            }

            // Linha do ranking: posição, nome (corta com "…" e mostra completo ao passar o mouse), contagem sempre visível e barra proporcional
            UIElement RankRow(int pos, string text, int count, int top)
            {
                var line = new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var num = new TextBlock { Text = pos.ToString(), FontSize = 13, Foreground = mute };
                var name = new TextBlock { Text = text, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text };
                var cnt = new TextBlock { Text = "▶ " + count, FontSize = 12.5, Foreground = cyan, Margin = new Thickness(12, 0, 0, 0) };
                Grid.SetColumn(name, 1); Grid.SetColumn(cnt, 2);
                line.Children.Add(num); line.Children.Add(name); line.Children.Add(cnt);
                double ratio = Math.Min(1, Math.Max(0.04, (double)count / Math.Max(1, top)));
                var bar = new Grid { Height = 3, Margin = new Thickness(24, 4, 0, 0) };
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = grad, Opacity = 0.85 });
                var box = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };
                box.Children.Add(line); box.Children.Add(bar);
                return box;
            }

            var layout = new StackPanel { Margin = new Thickness(30, 4, 30, 6) };
            layout.Children.Add(new TextBlock { Text = T.Get("Meu resumo de uso"), FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });

            int totalMin = (int)(secs / 60);
            string timeText = totalMin < 1 ? "< 1 min" : totalMin >= 60 ? (totalMin / 60) + " h " + (totalMin % 60).ToString("00") + " min" : totalMin + " min";
            var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -8, 0) };
            tiles.Children.Add(Tile(music.Count.ToString(), "Músicas"));
            tiles.Children.Add(Tile(played.ToString(), "Já ouvidas"));
            tiles.Children.Add(Tile(totalPlays.ToString(), "Reproduções"));
            tiles.Children.Add(Tile(timeText, "Tempo ouvindo (estimado)"));
            tiles.Children.Add(Tile(favs.Count.ToString(), "Favoritas"));
            tiles.Children.Add(Tile(artists.Count.ToString(), "Artistas ouvidos"));
            layout.Children.Add(tiles);

            layout.Children.Add(Heading(T.Get("Músicas mais ouvidas")));
            var topTracks = tracks.OrderByDescending(p => p.Value).Take(5).ToList();
            int rank = 1;
            foreach (var t in topTracks)
                layout.Children.Add(RankRow(rank++, StatTitle(t.Key), t.Value, topTracks[0].Value));

            layout.Children.Add(Heading(T.Get("Artistas mais ouvidos")));
            var topArtists = artists.OrderByDescending(p => p.Value).Take(5).ToList();
            rank = 1;
            foreach (var a in topArtists)
                layout.Children.Add(RankRow(rank++, a.Key.Trim(), a.Value, topArtists[0].Value));

            int maxH = Math.Max(1, hours.Max()), peak = Array.IndexOf(hours, hours.Max());
            layout.Children.Add(Heading(T.Get("Quando você mais ouve (pico por volta das {0} h)", peak)));
            var bars = new System.Windows.Controls.Primitives.UniformGrid { Columns = 24, Rows = 1, Height = 60 };
            var labels = new System.Windows.Controls.Primitives.UniformGrid { Columns = 24, Rows = 1, Margin = new Thickness(0, 3, 0, 0) };
            for (int h = 0; h < 24; h++)
            {
                var holder = new Grid { Background = Brushes.Transparent, ToolTip = T.Get("{0} h: {1} reproduções", h, hours[h]) };
                holder.Children.Add(new Border
                {
                    Height = Math.Max(3, 60.0 * hours[h] / maxH), CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(1.5, 0, 1.5, 0), Background = grad, Opacity = h == peak ? 1 : 0.55
                });
                bars.Children.Add(holder);
                labels.Children.Add(new TextBlock { Text = h % 3 == 0 ? h.ToString() : string.Empty, FontSize = 9.5, Foreground = mute, HorizontalAlignment = HorizontalAlignment.Center });
            }
            layout.Children.Add(bars);
            layout.Children.Add(labels);

            // Se a tela for baixa, a lista rola e o botão Fechar continua sempre visível
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = layout, MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 190) };
            var close = BrandedButton("Fechar", true, false); close.IsDefault = true; close.IsCancel = true;
            close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, 14, 30, 24);
            var outer = new StackPanel();
            outer.Children.Add(scroll); outer.Children.Add(close);
            Window dialog = NewBrandedWindow(500, outer, out Button dialogX);
            dialog.Title = "Meu resumo de uso";
            close.Click += (a, b) => dialog.Close();
            dialog.ShowDialog();
        }

        // ---------- Fila inteligente: não sugerir mais ----------
        void BlockTrack_Click(object s, RoutedEventArgs e)
        {
            Item it = MenuItemOf(s);
            if (it == null) return;
            LoadSmart();
            if (smartBlockTracks.Add(it.FilePath)) smart.Blocked.Add(it.FilePath);
            DropFromQueueIf(i => ReferenceEquals(i, it) && !ReferenceEquals(i, cur));
            SaveSmart();
            Toast("A fila inteligente não vai mais sugerir esta música");
        }

        void BlockArtist_Click(object s, RoutedEventArgs e)
        {
            Item it = MenuItemOf(s);
            if (it == null) return;
            if (string.IsNullOrWhiteSpace(it.Artist)) { Toast("Esta música não tem artista nas informações"); return; }
            LoadSmart();
            if (smartBlockArtists.Add(it.Artist)) smart.BlockedArtists.Add(it.Artist);
            DropFromQueueIf(i => string.Equals(i.Artist, it.Artist, StringComparison.OrdinalIgnoreCase));
            SaveSmart();
            Toast("A fila inteligente não vai mais sugerir " + it.Artist);
        }

        void ClearBlocks_Click(object s, RoutedEventArgs e)
        {
            LoadSmart();
            int n = smart.Blocked.Count + smart.BlockedArtists.Count;
            smart.Blocked.Clear(); smart.BlockedArtists.Clear(); smartBlockTracks.Clear(); smartBlockArtists.Clear();
            SaveSmart();
            Toast(n == 0 ? "Não há bloqueios" : n + " bloqueios removidos");
        }

        void DropFromQueueIf(Func<Item, bool> match)
        {
            bool changed = false;
            for (int i = queue.Count - 1; i >= 0; i--)
                if (match(queue[i])) { queue.RemoveAt(i); changed = true; }
            if (changed) RefreshQueue();
        }

        // ---------- Músicas automáticas: limpar ----------
        void CleanAuto_Click(object s, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ytFolder)) { Toast("Pasta de downloads não definida"); return; }
            string prefix = System.IO.Path.Combine(ytFolder, "Audio", "Automatico") + System.IO.Path.DirectorySeparatorChar;
            List<Item> autos = lib.Where(i => i.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !favs.Contains(i.FilePath) && !ReferenceEquals(i, cur)).ToList();
            if (autos.Count == 0) { Toast("Não há músicas automáticas para limpar"); return; }
            if (!BrandedAsk(T.Get("Mandar {0} músicas baixadas automaticamente para a Lixeira?", autos.Count),
                T.Get("As favoritas (♥) e a que está tocando ficam."),
                T.Get("Mandar para a Lixeira"), T.Get("Cancelar"), "\uE74D", true)) return;
            var gone = new List<Item>();
            int failed = 0;
            foreach (Item it in autos) { if (RecycleFile(it.FilePath)) gone.Add(it); else failed++; }
            RemoveItemsFromLibrary(gone);
            Toast(failed > 0 ? gone.Count + " removidas; " + failed + " não puderam ser apagadas" : gone.Count + " músicas automáticas removidas");
        }
    }
}
