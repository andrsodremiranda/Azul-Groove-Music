using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VLC = LibVLCSharp.Shared;

namespace AzulGroove
{
    public partial class MainWindow
    {
        // ---------- Reprodução ----------
        void Play(Item it)
        {
            if (_mp == null) { pendingItem = it; NowTitle.Text = "Carregando…"; return; }
            if (!File.Exists(it.FilePath))
            {
                lib.Remove(it); recent.Remove(it); while (queue.Remove(it)) { }
                known.Remove(it.FilePath); itemsByPath.Remove(it.FilePath); Save(); Refresh(); RefreshQueue(); return;
            }
            queue.Remove(it);
            RefreshQueue();
            if (cur != null) cur.IsPlaying = false;
            cur = it; it.IsPlaying = true;
            recent.Remove(it); recent.Insert(0, it);
            if (recent.Count > 30) recent.RemoveAt(recent.Count - 1);
            ChipRes.Visibility = Visibility.Collapsed;
            UpdateNow();
            Volatile.Write(ref curTime, Math.Max(0, pendingSeek)); Volatile.Write(ref curLen, (long)it.Duration.TotalMilliseconds);
            if (it.IsVideo)
            {
                // A janela de vídeo só nasce no primeiro vídeo; precisa estar pronta antes do VLC começar
                EnsureVideoPanel(); Navigate(4); Toast(it.Name); UpdateLayout();
                if (vpanel.IsHandleCreated) _mp.Hwnd = vpanel.Handle;
            }
            countItem = it.IsVideo ? null : it;   // conta como reprodução quando passar de 30 s
            StartMedia(it.FilePath);
            if (!it.IsVideo) PromoteRecent(it);
            UI(SmartQueueFill);   // completa a fila pelo seu gosto a cada troca de música
        }

        // Músicas novas (baixadas do YouTube ou copiadas para uma pasta da biblioteca) entram sozinhas na fila.
        // Se o player está sem faixa ou parado no fim, a primeira já começa a tocar; se está tocando ou pausado, só entram na fila.
        void AutoEnqueue(IEnumerable<Item> items, string message = null)
        {
            if (!prefs.AutoQueueNew || _mp == null || items == null) return;
            var fresh = items.Where(i => i != null && !i.IsVideo && !i.IsStream && !ReferenceEquals(i, cur) && !queue.Contains(i)).Take(50).ToList();
            if (fresh.Count == 0) return;
            var state = _mp.State;
            bool idle = cur == null || state == VLC.VLCState.Ended || state == VLC.VLCState.Stopped || state == VLC.VLCState.Error;
            foreach (Item item in fresh) queue.Add(item);
            RefreshQueue();
            if (idle) Play(queue[0]);
            Toast(message ?? (fresh.Count == 1 ? "Nova música no player: " + fresh[0].Name : fresh.Count + " músicas novas na fila"));
        }

        // O VLC leva um instante para parar a faixa atual e abrir a nova; fora da thread da tela a interface não congela.
        // Se o usuário clicar em várias faixas seguidas, só a última é aberta.
        void StartMedia(string path, bool location = false)
        {
            int my = System.Threading.Interlocked.Increment(ref playSeq);
            var mp = _mp; var lb = _lib;
            System.Threading.Tasks.Task.Run(() =>
            {
                lock (playLock)
                {
                    if (my != System.Threading.Volatile.Read(ref playSeq)) return;
                    try
                    {
                        using (var m = new VLC.Media(lb, path, location ? VLC.FromType.FromLocation : VLC.FromType.FromPath))
                        {
                            if (location) { m.AddOption(":network-caching=800"); m.AddOption(":http-reconnect=true"); }   // internet: começa a tocar mais cedo e reconecta se a conexão cair
                            mp.Play(m);
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not play media " + path + ": " + ex.Message); }
                }
            });
        }

        // Início: a lista "recentes" já foi atualizada em Play(); a tela só é refeita quando o usuário voltar ao Início
        // (antes cada clique num cartão refazia todos os cartões na hora, atrasando a música e embaralhando a tela)
        void PromoteRecent(Item it) { }

        void UpdateNow()
        {
            bool has = cur != null, v = has && cur.IsVideo;
            NowTitle.Text = has ? cur.Name : "Nenhuma faixa";
            NowDetails.Text = has ? cur.Subtitle : string.Empty;
            PlayerTitle.Text = NowTitle.Text; DiscLabel.Text = has ? cur.Initial : "♪";
            SetMiniArt(cur);
            MiniGlyph.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            VideoHost.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
            AudioPanel.Visibility = v ? Visibility.Collapsed : Visibility.Visible;
            if (!has) { ChipRes.Visibility = Visibility.Collapsed; Seek.Value = 0; CurT.Text = DurT.Text = "0:00"; }
            else
            {
                var pop = new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 } };
                MiniScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop); MiniScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                NowTitle.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)));
            }
            UpdateTrayText();
        }

        // A capa vem do arquivo (TagLib) e antes era lida aqui, na thread da tela, a cada troca de faixa: a música só começava depois disso.
        // Agora a tela mostra a cor padrão na hora e a capa entra quando estiver pronta.
        static readonly Brush MiniArtDefault = Frozen(new SolidColorBrush(Color.FromRgb(14, 42, 92)));
        void SetMiniArt(Item item)
        {
            if (item == null) { MiniArt.Background = MiniArtDefault; return; }
            if (item.TryGetCachedArt(out Brush cached)) { MiniArt.Background = cached; return; }
            MiniArt.Background = MiniArtDefault;
            System.Threading.Tasks.Task.Run(() => item.Art).ContinueWith(task =>
            {
                if (task.IsFaulted || task.IsCanceled) return;
                Brush loaded = task.Result;
                UI(() => { if (ReferenceEquals(cur, item)) MiniArt.Background = loaded; });
            });
        }

        void SetPlayState(bool pl)
        {
            isPlaying = pl;
            PlayBtn.Content = pl ? "\uE769" : "\uE768";
            Spin(ringSb, ref ringOn, pl); Spin(discSb, ref discOn, pl); UpdateViz();
            if (!pl) UpdateTimeUi();
            SyncTimer();
        }

        // O timer de 200 ms só roda com a janela visível e a mídia tocando (ou arrastando a barra)
        void SyncTimer()
        {
            if (timer == null) return;
            bool need = _mp != null && IsVisible && (isPlaying || dragging);
            if (need != timer.IsEnabled) timer.IsEnabled = need;
        }

        void Step(int d, bool auto = false)
        {
            if (lib.Count == 0) return;
            if (!auto && d > 0) SmartNoteSkip();   // pulou antes de contar como ouvida: aprende que não gostou
            if (auto && repeat == 2 && cur != null) { Play(cur); return; }
            if (d > 0 && queue.Count > 0) { Play(queue[0]); return; }
            if (shuffle)
            {
                // Embaralha a biblioteca uma vez e percorre a lista: nenhuma faixa repete até acabar a rodada
                if (shufList == null || shufLibCount != lib.Count || shufIdx < 0 || shufIdx >= shufList.Count || shufList[shufIdx] != cur) BuildShuffle();
                int k = shufIdx + d;
                if (k >= shufList.Count)
                {
                    if (auto && repeat == 0) { _mp.Stop(); return; }
                    BuildShuffle(); k = shufIdx + 1;
                    if (k >= shufList.Count) k = 0;
                }
                if (k < 0) k = shufList.Count - 1;
                shufIdx = k; Play(shufList[k]);
                return;
            }
            int i = curIdx >= 0 && curIdx < lib.Count && lib[curIdx] == cur ? curIdx : lib.IndexOf(cur), n;
            n = i + d;
            if (n >= lib.Count) { if (repeat == 1 || !auto) n = 0; else { _mp.Stop(); return; } }
            if (n < 0) n = lib.Count - 1;
            curIdx = n; Play(lib[n]);
        }

        void BuildShuffle()
        {
            shufList = new List<Item>(lib); shufLibCount = lib.Count; shufIdx = -1;
            for (int i = shufList.Count - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                Item t = shufList[i]; shufList[i] = shufList[j]; shufList[j] = t;
            }
            if (cur == null) return;
            int at = shufList.IndexOf(cur);   // a faixa atual vai para o começo da rodada
            if (at < 0) return;
            Item first = shufList[0]; shufList[0] = shufList[at]; shufList[at] = first;
            shufIdx = 0;
        }

        void Play_Click(object s, RoutedEventArgs e)
        {
            if (cur == null) { if (queue.Count > 0) Play(queue[0]); else if (lib.Count > 0) Play(lib[0]); return; }
            if (_mp.IsPlaying) _mp.Pause(); else _mp.Play();
        }
        void Next_Click(object s, RoutedEventArgs e) => Step(1);
        void Prev_Click(object s, RoutedEventArgs e)
        {
            if (_mp != null && Volatile.Read(ref curTime) > 3000) { _mp.Time = 0; Volatile.Write(ref curTime, 0); if (!isPlaying) UpdateTimeUi(); }
            else Step(-1);
        }
        void Shuf_Click(object s, RoutedEventArgs e) { shuffle = !shuffle; ShufBtn.Foreground = shuffle ? (Brush)FindResource("Cyan") : (Brush)FindResource("Foam"); ShufBtn.Background = shuffle ? ActiveBg : Brushes.Transparent; }
        void Rep_Click(object s, RoutedEventArgs e)
        {
            repeat = (repeat + 1) % 3;
            RepBtn.Content = repeat == 2 ? "\uE8ED" : "\uE8EE";
            RepBtn.Foreground = repeat > 0 ? (Brush)FindResource("Cyan") : (Brush)FindResource("Foam");
            RepBtn.Background = repeat > 0 ? ActiveBg : Brushes.Transparent;
        }
        // ---------- Pulo, velocidade e extras ----------
        void FinishSeek()
        {
            if (!dragging) return; dragging = false; SyncTimer();
            if (cur == null) return;
            long t = (long)Seek.Value; var st = _mp.State;
            if (st == VLC.VLCState.Ended || st == VLC.VLCState.Stopped || st == VLC.VLCState.Error) { pendingSeek = t; Play(cur); }
            else { _mp.Time = t; Volatile.Write(ref curTime, t); if (!isPlaying) UpdateTimeUi(); }
        }
        void Skip(long ms)
        {
            if (cur == null) return;
            long len = Volatile.Read(ref curLen), t = Volatile.Read(ref curTime) + ms;
            if (t < 0) t = 0; if (len > 0 && t > len - 500) t = Math.Max(0, len - 500);
            _mp.Time = t; Volatile.Write(ref curTime, t); if (!isPlaying) UpdateTimeUi(); Toast((ms > 0 ? "+" : "−") + Math.Abs(ms / 1000) + " s");
        }
        void Back10_Click(object s, RoutedEventArgs e) => Skip(-10000);
        void Fwd_Click(object s, RoutedEventArgs e) => Skip(30000);

        void Spd_Click(object s, RoutedEventArgs e) { SpdPop.IsOpen = !SpdPop.IsOpen; }
        void SetSpeed(float r)
        {
            speed = r; SpdBtn.Content = r.ToString("0.##") + "x"; MarkSpeed(); SpdPop.IsOpen = false;
            ApplySpeed(); Toast("Velocidade " + r.ToString("0.##") + "x");
        }
        void StepSpeed(int d) { int i = Array.IndexOf(speeds, speed); SetSpeed(speeds[Math.Max(0, Math.Min(speeds.Length - 1, i + d))]); }
        void MarkSpeed()
        {
            foreach (Button b in SpdGrid.Children)
            {
                bool on = Math.Abs((float)b.Tag - speed) < 0.01f;
                b.Foreground = (Brush)FindResource(on ? "Cyan" : "Foam"); b.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
            }
        }
        // O VLC trava um instante ao trocar a velocidade; fora da thread da tela a interface não congela
        void ApplySpeed()
        {
            float r = speed; var mp = _mp;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { if (Math.Abs(mp.Rate - r) > 0.01f) mp.SetRate(r); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not set playback speed: " + ex.Message); }
            });
        }

        void More_Click(object s, RoutedEventArgs e) { MorePop.IsOpen = !MorePop.IsOpen; }
        void Aspect_Click(object s, RoutedEventArgs e)
        {
            aspIdx = (aspIdx + 1) % AspVal.Length;
            try { _mp.AspectRatio = AspVal[aspIdx]; }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not set video aspect ratio: " + ex.Message); }
            AspectTxt.Text = "Proporção da tela: " + AspName[aspIdx]; Toast("Proporção " + AspName[aspIdx]);
        }
        void Sub_Click(object s, RoutedEventArgs e) { MorePop.IsOpen = false; CycleTrack(true); }
        void Audio_Click(object s, RoutedEventArgs e) { MorePop.IsOpen = false; CycleTrack(false); }
        void CycleTrack(bool sub)
        {
            if (_mp == null) return;
            var d = sub ? _mp.SpuDescription : _mp.AudioTrackDescription;
            if (d == null || d.Length == 0) { Toast(sub ? "Este vídeo não tem legendas" : "Sem outras faixas de áudio"); return; }
            int curId = sub ? _mp.Spu : _mp.AudioTrack;
            int i = Array.FindIndex(d, x => x.Id == curId); var n = d[(i + 1) % d.Length];
            if (sub) _mp.SetSpu(n.Id); else _mp.SetAudioTrack(n.Id);
            Toast((sub ? "Legenda: " : "Áudio: ") + n.Name);
        }

        void Toast(string t) { ToastTxt.Text = t; ToastPop.IsOpen = true; toastTimer.Stop(); toastTimer.Start(); }

        // Controles somem sozinhos em tela cheia
        void Moved()
        {
            if (!full) return;
            var p = System.Windows.Forms.Control.MousePosition;
            if (Math.Abs(p.X - lastPos.X) + Math.Abs(p.Y - lastPos.Y) <= 3) return;
            lastPos = p; lastMove = DateTime.Now; ShowBar();
        }
        void Window_MouseMove(object s, MouseEventArgs e) => Moved();
        bool cursorHidden, barHiding;
        void ShowBar()
        {
            if (cursorHidden) { System.Windows.Forms.Cursor.Show(); cursorHidden = false; }
            if (Bar.Visibility == Visibility.Visible && !barHiding && Bar.Opacity >= 1) return;
            barHiding = false; Bar.Visibility = Visibility.Visible;
            Bar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            BarShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        void HideBar()
        {
            if (barHiding || Bar.Visibility != Visibility.Visible) return;
            barHiding = true;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(280));
            fade.Completed += (a, b) => { if (barHiding) { barHiding = false; Bar.Visibility = Visibility.Collapsed; } };
            Bar.BeginAnimation(OpacityProperty, fade);
            BarShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(40, TimeSpan.FromMilliseconds(280)));
            if (!cursorHidden) { System.Windows.Forms.Cursor.Hide(); cursorHidden = true; }
        }
        
        void Mini_Click(object s, RoutedEventArgs e) { if (cur != null) Navigate(4); }

        static string Fmt(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
        static string Res(uint w, uint h) => (w >= 7680 || h >= 4320) ? "8K" : (w >= 3840 || h >= 2160) ? "4K" : h >= 1440 ? "2K" : h >= 1080 ? "Full HD" : h >= 720 ? "HD" : h + "p";

        void Tick(object s, EventArgs e)
        {
            if (_mp == null || !IsVisible) return;
            if (dragging && Mouse.LeftButton != MouseButtonState.Pressed) FinishSeek();
            if (full && cur != null && cur.IsVideo && isPlaying && Bar.Visibility == Visibility.Visible && (DateTime.Now - lastMove).TotalSeconds > 3) HideBar();
            UpdateViz();
            UpdateTimeUi();
        }

        // Lê o tempo guardado pelos eventos do VLC (sem chamar o player)
        void UpdateTimeUi()
        {
            if (cur == null || _mp == null) return;
            long len = Volatile.Read(ref curLen), t = Volatile.Read(ref curTime);
            if (!dragging) { if (len > 0) { Seek.Maximum = len; Seek.Value = t; } CurT.Text = Fmt(t); }
            DurT.Text = Fmt(len);
            if (cur.IsVideo && ChipRes.Visibility != Visibility.Visible)
            {
                uint w = 0, h = 0;
                if (_mp.Size(0, ref w, ref h) && w > 0) { ChipTxt.Text = Res(w, h) + "  ·  " + w + "×" + h; ChipRes.Visibility = Visibility.Visible; }
            }
        }

    }
}
