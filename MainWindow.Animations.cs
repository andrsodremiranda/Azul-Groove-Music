using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AzulGroove
{
    public partial class MainWindow
    {
        // Luzes do fundo: movidas quadro a quadro por código (curvas seno), pausam no vídeo e quando minimizado
        double driftPhase; long driftLast;
        void Drift(bool on)
        {
            on &= AnimationsAllowed;
            if (App.SafeMode || on == bgOn) return; bgOn = on;
            if (on) { driftLast = Environment.TickCount64; CompositionTarget.Rendering += DriftFrame; }
            else CompositionTarget.Rendering -= DriftFrame;
        }
        void DriftFrame(object s, EventArgs e)
        {
            long now = Environment.TickCount64; long ms = now - driftLast;
            if (ms < 33) return;   // fundo animado a 30 quadros/s (basta e gasta metade)
            driftLast = now; driftPhase += Math.Min(0.1, ms / 1000.0);
            double t = driftPhase;
            ShA.X = 170 + 360 * Math.Sin(t * 0.50); ShA.Y = 110 + 170 * Math.Sin(t * 0.66 + 1);
            ShB.X = -280 + 300 * Math.Sin(t * 0.42 + 2); ShB.Y = -130 + 140 * Math.Sin(t * 0.58);
            ShC.X = 320 * Math.Sin(t * 0.54 + 4); ShC.Y = 150 * Math.Sin(t * 0.44 + 2);
            ShD.X = 280 * Math.Sin(t * 0.36 + 1); ShD.Y = 170 * Math.Sin(t * 0.50 + 5);
        }

        // ---------- Animação do Início: ondas subindo e partículas flutuando (30 fps, pausa fora do Início) ----------
        void BuildFx()
        {
            homeSb = new Storyboard();
            double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
            AddWave(sw, 520, 26, 18, false, "#5C4FD1FF");
            AddWave(sw, 760, 18, 26, true, "#4C2F8CFF");
            AddWave(sw, 940, 34, 36, false, "#30FFFFFF");

            var cv = new Canvas { IsHitTestVisible = false };
            FxLayer.Children.Add(cv);
            var r = new Random(7);
            for (int i = 0; i < (lowGpu ? 6 : 12); i++)
            {
                double sz = 3 + r.NextDouble() * 7, dur = 14 + r.NextDouble() * 18;
                var tr = new TranslateTransform();
                var el = new Ellipse { Width = sz, Height = sz, RenderTransform = tr, Opacity = 0, Fill = new SolidColorBrush(Color.FromArgb((byte)(80 + r.Next(100)), 190, 225, 255)) };
                Canvas.SetLeft(el, r.NextDouble() * sw); Canvas.SetTop(el, sh);
                cv.Children.Add(el);
                var bt = TimeSpan.FromSeconds(-r.NextDouble() * dur);
                var up = new DoubleAnimation(0, -(sh + 40), TimeSpan.FromSeconds(dur)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = bt };
                Storyboard.SetTarget(up, tr); Storyboard.SetTargetProperty(up, new PropertyPath(TranslateTransform.YProperty));
                var sway = new DoubleAnimation(-14, 14, TimeSpan.FromSeconds(4 + r.NextDouble() * 4)) { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                Storyboard.SetTarget(sway, tr); Storyboard.SetTargetProperty(sway, new PropertyPath(TranslateTransform.XProperty));
                var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(dur), RepeatBehavior = RepeatBehavior.Forever, BeginTime = bt };
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.15)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.75)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                Storyboard.SetTarget(fade, el); Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));
                homeSb.Children.Add(up); homeSb.Children.Add(sway); homeSb.Children.Add(fade);
            }
            Timeline.SetDesiredFrameRate(homeSb, lowGpu ? 20 : 30);
        }
        void AddWave(double screenW, double period, double amp, double sec, bool reverse, string color)
        {
            int n = (int)Math.Ceiling(screenW / period) + 2; double h = 230, y0 = 100, w = n * period;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(0, y0), true, true);
                for (int i = 0; i < n; i++)
                {
                    double x = i * period;
                    c.QuadraticBezierTo(new Point(x + period * .25, y0 - 2 * amp), new Point(x + period * .5, y0), true, false);
                    c.QuadraticBezierTo(new Point(x + period * .75, y0 + 2 * amp), new Point(x + period, y0), true, false);
                }
                c.LineTo(new Point(w, h), false, false); c.LineTo(new Point(0, h), false, false);
            }
            g.Freeze();
            var col = (Color)ColorConverter.ConvertFromString(color);
            var fill = new LinearGradientBrush(col, Color.FromArgb(0, col.R, col.G, col.B), 90); fill.Freeze();
            var tr = new TranslateTransform();
            var wave = new System.Windows.Shapes.Path { Data = g, Fill = fill, Width = w, Height = h, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, RenderTransform = tr, CacheMode = new BitmapCache(0.5) };
            FxLayer.Children.Add(wave);
            // desloca exatamente 1 período: o laço fica contínuo, sem emenda
            var an = new DoubleAnimation(reverse ? -period : 0, reverse ? 0 : -period, TimeSpan.FromSeconds(sec)) { RepeatBehavior = RepeatBehavior.Forever };
            Storyboard.SetTarget(an, tr); Storyboard.SetTargetProperty(an, new PropertyPath(TranslateTransform.XProperty));
            homeSb.Children.Add(an);
        }
        void Fx(bool on)
        {
            if (homeSb == null) return;
            on &= AnimationsAllowed;
            if (!AnimationsAllowed) FxLayer.Visibility = Visibility.Collapsed;
            if (on == homeOn) return; homeOn = on;
            FxLayer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) { if (!homeStarted) { homeSb.Begin(this, true); homeStarted = true; } else homeSb.Resume(this); }
            else if (homeStarted) homeSb.Pause(this);
        }

        // Menu do botão direito
        Item MenuItemOf(object s)
        {
            if (s is MenuItem mi && ItemsControl.ItemsControlFromItemContainer(mi) is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe) return fe.DataContext as Item;
            return (s as FrameworkElement)?.DataContext as Item;
        }
        void PlayMenu_Click(object s, RoutedEventArgs e) { var it = MenuItemOf(s); if (it != null) Play(it); }
        void Reveal_Click(object s, RoutedEventArgs e)
        {
            var it = MenuItemOf(s);
            if (it != null)
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + it.FilePath + "\""); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not reveal media file: " + ex.Message); }
            }
        }

        // ---------- Volume e mudo ----------
        // O mudo é controlado aqui (volume real = 0), assim o ícone e o som nunca ficam trocados
        void ApplyVolume()
        {
            if (_mp == null) return;
            try { _mp.Volume = muted ? 0 : (int)Math.Round(vol); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not set playback volume: " + ex.Message); }
        }
        void Vol_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (volBusy) return;
            vol = e.NewValue; if (vol > 0) muted = false;     // mexer no volume tira do mudo
            ApplyVolume(); UpdateVolIcon();
        }
        void Vol_Wheel(object s, MouseWheelEventArgs e)
        {
            VolSlider.Value = Math.Max(0, Math.Min(100, VolSlider.Value + (e.Delta > 0 ? 5 : -5))); e.Handled = true;
        }
        void Mute_Click(object s, RoutedEventArgs e)
        {
            muted = !muted;
            if (!muted && vol <= 0) vol = 40;
            AnimateVol(muted ? 0 : vol);
            ApplyVolume(); UpdateVolIcon(true);
            Toast(muted ? "Mudo" : "Volume " + (int)vol + "%");
        }
        void AnimateVol(double target)
        {
            volBusy = true;
            var an = new DoubleAnimation(target, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            an.Completed += (a, b) => { VolSlider.BeginAnimation(Slider.ValueProperty, null); VolSlider.Value = target; volBusy = false; };
            VolSlider.BeginAnimation(Slider.ValueProperty, an);
        }
        void UpdateVolIcon(bool pop = false)
        {
            if (MuteBtn == null) return;
            int v = muted ? 0 : (int)Math.Round(vol);
            MuteBtn.Content = muted ? "\uE74F" : v <= 0 ? "\uE992" : v < 34 ? "\uE993" : v < 67 ? "\uE994" : "\uE995";
            MuteBtn.Foreground = (Brush)FindResource(muted ? "Mute" : "Foam");
            VolSlider.BeginAnimation(OpacityProperty, new DoubleAnimation(muted ? 0.45 : 1, TimeSpan.FromMilliseconds(200)));
            if (pop)
            {
                var an = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.9 } };
                MuteScale.BeginAnimation(ScaleTransform.ScaleXProperty, an); MuteScale.BeginAnimation(ScaleTransform.ScaleYProperty, an);
            }
        }

        // ---------- Menus ----------
        void ClosePops() { SpdPop.IsOpen = false; MorePop.IsOpen = false; }
        void Pop_Opened(object s, EventArgs e)
        {
            if (warming) return;
            if (((Popup)s).Child is FrameworkElement c)
            {
                Anim(c, OpacityProperty, 0, 1, 140);
                if (c.RenderTransform is ScaleTransform st)
                {
                    var an = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, an); st.BeginAnimation(ScaleTransform.ScaleYProperty, an);
                }
            }
        }

        // A primeira abertura de um popup transparente é lenta (o Windows cria a janela na hora).
        // Abre invisível uma vez, logo após o início, para a primeira vez do usuário já ser rápida.
        void WarmPopups()
        {
            try
            {
                warming = true;
                foreach (var pp in new[] { SpdPop, MorePop, ToastPop }) { if (pp.Child != null) pp.Child.Opacity = 0; pp.IsOpen = true; }
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    SpdPop.IsOpen = false; MorePop.IsOpen = false; ToastPop.IsOpen = false; warming = false;
                    if (ToastPop.Child != null) ToastPop.Child.Opacity = 1;
                }));
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not warm popups: " + ex.Message); warming = false; }
        }

        // Animação simples de um número. Sem atraso: termina e volta ao valor normal.
        // Com atraso: já começa no valor inicial (sem "piscar") e fixa o final ao terminar.
        static void Anim<T>(T o, DependencyProperty p, double from, double to, int ms, int delay = 0) where T : DependencyObject, IAnimatable
        {
            var an = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            if (delay <= 0) { an.FillBehavior = FillBehavior.Stop; o.SetValue(p, to); }
            else { an.BeginTime = TimeSpan.FromMilliseconds(delay); o.SetValue(p, from); an.Completed += (a, b) => { o.BeginAnimation(p, null); o.SetValue(p, to); }; }
            o.BeginAnimation(p, an);
        }

        // Entrada do app: menu lateral desliza, itens aparecem em sequência, barra de reprodução sobe
        void Intro()
        {
            var st = new TranslateTransform(); Side.RenderTransform = st;
            // Entrada curta (antes passava de 0,8 s com a tela quase vazia e parecia que o app demorava para abrir)
            Anim(Side, OpacityProperty, 0, 1, 220);
            Anim(st, TranslateTransform.XProperty, -24, 0, 280);
            Anim(TitleBar, OpacityProperty, 0, 1, 220);
            Anim(Bar, OpacityProperty, 0, 1, 220, 40);
            Anim(BarShift, TranslateTransform.YProperty, 40, 0, 280, 40);
            var rs = new[] { NavHome, NavMusic, NavVideo, NavQueue, NavNow };
            for (int i = 0; i < rs.Length; i++)
            {
                var t = new TranslateTransform(); rs[i].RenderTransform = t;
                Anim(rs[i], OpacityProperty, 0, 1, 200, 50 + i * 25);
                Anim(t, TranslateTransform.XProperty, -14, 0, 240, 50 + i * 25);
            }
        }

        // Os primeiros cartões de cada lote entram em sequência; o resto aparece direto (barato)
        void Card_Loaded(object s, RoutedEventArgs e)
        {
            var b = (Button)s;
            if (b.Tag != null) return; b.Tag = "x";
            int i = animIdx++; if (i >= 14) return;
            Anim(b, OpacityProperty, 0, 1, 340, i * 30);
            if (b.RenderTransform is TranslateTransform tt) Anim(tt, TranslateTransform.YProperty, 24, 0, 460, i * 30);
        }

        // Medidor de nível da sessão de áudio do app, sem gerar barras quando não há sinal real
        void UpdateViz()
        {
            bool fast = page == 4 && cur != null && !cur.IsVideo;
            VizOn(fast && (isPlaying || lastAvg > 0.01));
        }
        void VizOn(bool on)
        {
            if (on == vizHooked) return; vizHooked = on;
            if (on) { vizLast = Environment.TickCount64; CompositionTarget.Rendering += VizFrame; }
            else CompositionTarget.Rendering -= VizFrame;
        }
        void VizFrame(object s, EventArgs e)
        {
            long now = Environment.TickCount64;
            if (now - vizLast < (prefs.LightMode ? 66 : 33)) return;   // limita o visualizador a ~30 quadros/s (15 no modo leve) em vez de todo quadro da tela
            double dt = Math.Min(0.05, (now - vizLast) / 1000.0); vizLast = now;
            bool pl = isPlaying;
            if (now - tgtAt > 110)
            {
                tgtAt = now;
                double level = pl ? Math.Min(1, Math.Sqrt(ReadAudioPeak(now)) * 1.25) : 0;
                for (int i = 0; i < NB; i++) tgt[i] = level;
            }
            double k = 1 - Math.Exp(-dt * (pl ? 16 : 7)), sum = 0;
            for (int i = 0; i < NB; i++) { lvl[i] += (tgt[i] - lvl[i]) * k; bars[i].Height = 6 + lvl[i] * 88; sum += lvl[i]; }
            lastAvg = sum / NB;
            double sc = 1 + lastAvg * 0.14; DiscScale.ScaleX = sc; DiscScale.ScaleY = sc;
            if (!pl && lastAvg <= 0.01) UpdateViz();
        }

        void UI(Action a) => Dispatcher.BeginInvoke(a);

        static Storyboard MakeSpin(DependencyObject target, double sec)
        {
            var a = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(sec)) { RepeatBehavior = RepeatBehavior.Forever };
            Storyboard.SetTarget(a, target);
            Storyboard.SetTargetProperty(a, new PropertyPath(RotateTransform.AngleProperty));
            var sb = new Storyboard(); sb.Children.Add(a); return sb;
        }

        void Spin(Storyboard sb, ref bool started, bool on)
        {
            if (on) { if (!started) { sb.Begin(this, true); started = true; } else sb.Resume(this); }
            else if (started) sb.Pause(this);
        }

    }
}
