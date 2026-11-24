using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AzulGroove
{
    // Janela de atualização no visual do app: mostra a versão nova, baixa com barra de progresso,
    // instala em modo silencioso (sem janelas) e o instalador abre o app de novo quando termina.
    public partial class MainWindow
    {
        const string XamlNs = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

        static ControlTemplate ParseTemplate(string targetType, string inner) =>
            (ControlTemplate)XamlReader.Parse("<ControlTemplate " + XamlNs + " TargetType='" + targetType + "'>" + inner + "</ControlTemplate>");
        static ControlTemplate ParseButtonTemplate(string inner) => ParseTemplate("Button", inner);

        // Janela sem a moldura do Windows: barra de título própria, brilho azul e contorno fino
        Window NewBrandedWindow(double width, UIElement body, out Button closeX)
        {
            var abyss = (Brush)FindResource("Abyss");
            var outline = (Brush)new BrushConverter().ConvertFromString("#2678AAFF");
            var win = new Window
            {
                Width = width, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                Background = abyss, Foreground = (Brush)FindResource("Foam"),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                UseLayoutRounding = true, SnapsToDevicePixels = true, Icon = Icon
            };
            System.Windows.Shell.WindowChrome.SetWindowChrome(win, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 44, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
            });

            var root = new Grid { ClipToBounds = true };
            root.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 480, Height = 480, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -200, -150, 0), IsHitTestVisible = false,
                Fill = new RadialGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x60, 0x4F, 0xD1, 0xFF), 0), new GradientStop(Color.FromArgb(0x00, 0x4F, 0xD1, 0xFF), 1)
                })
            });
            root.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 440, Height = 440, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(-190, 0, 0, -240), IsHitTestVisible = false,
                Fill = new RadialGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x70, 0x2F, 0x8C, 0xFF), 0), new GradientStop(Color.FromArgb(0x00, 0x2F, 0x8C, 0xFF), 1)
                })
            });

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(grid);

            var bar = new Grid();
            var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
            if (TitleIcon != null && TitleIcon.Source != null)
                left.Children.Add(new System.Windows.Controls.Image { Source = TitleIcon.Source, Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0) });
            left.Children.Add(new TextBlock { Text = "Azul Groove Music", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(left);
            var x = new Button { Style = (Style)FindResource("CapBtn"), Content = "\uE8BB", HorizontalAlignment = HorizontalAlignment.Right, Tag = FindResource("CloseHover") };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(x, true);
            x.Click += (a, b) => win.Close();
            bar.Children.Add(x);
            grid.Children.Add(bar);
            Grid.SetRow(body, 1);
            grid.Children.Add(body);
            closeX = x;

            win.Content = new Border { BorderBrush = outline, BorderThickness = new Thickness(1), Child = root };
            win.Opacity = 0;
            win.Loaded += (a, b) => win.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            return win;
        }

        // Tira a marcação de Markdown do texto do release (títulos, negrito, código) e limita o tamanho
        static string UpdCleanNotes(string md)
        {
            if (string.IsNullOrWhiteSpace(md)) return string.Empty;
            var lines = new List<string>();
            foreach (string raw in md.Replace("\r", string.Empty).Split('\n'))
            {
                string l = raw.Trim();
                l = Regex.Replace(l, @"^#{1,6}\s*", string.Empty);
                l = Regex.Replace(l, @"^[-*+]\s+", "• ");
                l = l.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);
                l = Regex.Replace(l, @"\[([^\]]+)\]\([^)]*\)", "$1");
                if (l.Length == 0 && (lines.Count == 0 || lines[lines.Count - 1].Length == 0)) continue;
                lines.Add(l);
            }
            string text = string.Join("\n", lines).Trim();
            return text.Length > 900 ? text.Substring(0, 900).TrimEnd() + "…" : text;
        }

        async Task UpdPromptAsync(UpdateInfo info, Version current)
        {
            var foam = (Brush)FindResource("Foam");
            var mute = (Brush)FindResource("Mute");
            var cyan = (Brush)FindResource("Cyan");
            var grad = (Brush)FindResource("Grad");

            Button MakeBtn(string text, bool primary, bool flat) => BrandedButton(text, primary, flat);

            // ---- conteúdo ----
            var body = new StackPanel { Margin = new Thickness(30, 10, 30, 24) };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.Children.Add(new Border
            {
                Width = 60, Height = 60, CornerRadius = new CornerRadius(18), Background = grad, Margin = new Thickness(0, 0, 18, 0),
                Child = new TextBlock { Text = "\uE896", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 26, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(new TextBlock { Text = "Nova versão disponível", FontSize = 22, FontWeight = FontWeights.SemiBold });
            var ver = new TextBlock { FontSize = 13.5, Margin = new Thickness(0, 4, 0, 0) };
            ver.Inlines.Add(new System.Windows.Documents.Run("Você está na " + current.ToString(3) + "   →   ") { Foreground = mute });
            ver.Inlines.Add(new System.Windows.Documents.Run(info.Version.ToString(3)) { Foreground = cyan, FontWeight = FontWeights.SemiBold });
            titles.Children.Add(ver);
            Grid.SetColumn(titles, 1);
            head.Children.Add(titles);
            body.Children.Add(head);

            body.Children.Add(new TextBlock
            {
                Text = "O Azul Groove baixa a atualização, instala em segundo plano e abre de novo sozinho quando terminar. Você não precisa fazer mais nada.",
                FontSize = 13.5, LineHeight = 21, Foreground = mute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 0)
            });

            // novidades: o texto do release no GitHub
            string releaseNotes = UpdCleanNotes(info.Notes);
            if (releaseNotes.Length > 0)
            {
                body.Children.Add(new TextBlock { Text = "Novidades", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = cyan, Margin = new Thickness(0, 18, 0, 6) });
                body.Children.Add(new ScrollViewer
                {
                    MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock { Text = releaseNotes, FontSize = 13, LineHeight = 20, Foreground = mute, TextWrapping = TextWrapping.Wrap }
                });
            }

            // progresso (aparece depois de clicar em "Atualizar agora")
            var progress = new StackPanel { Margin = new Thickness(0, 22, 0, 0), Visibility = Visibility.Collapsed };
            var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = (Brush)new BrushConverter().ConvertFromString("#26FFFFFF") };
            var fill = new Border { Height = 6, Width = 0, CornerRadius = new CornerRadius(3), Background = grad, HorizontalAlignment = HorizontalAlignment.Left };
            var barGrid = new Grid();
            barGrid.Children.Add(track); barGrid.Children.Add(fill);
            progress.Children.Add(barGrid);
            var status = new TextBlock { FontSize = 12.5, Foreground = mute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
            progress.Children.Add(status);
            var cancel = MakeBtn("Cancelar", false, false);
            cancel.HorizontalAlignment = HorizontalAlignment.Right; cancel.Margin = new Thickness(0, 16, 0, 0);
            progress.Children.Add(cancel);
            body.Children.Add(progress);

            // botões
            var actions = new Grid { Margin = new Thickness(0, 26, 0, 0) };
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var skip = MakeBtn("Pular esta versão", false, true);
            skip.HorizontalAlignment = HorizontalAlignment.Left; skip.Margin = new Thickness(-12, 0, 0, 0);
            var later = MakeBtn("Mais tarde", false, false); later.IsCancel = true; later.Margin = new Thickness(0, 0, 10, 0);
            var go = MakeBtn("Atualizar agora", true, false); go.IsDefault = true;
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(later); right.Children.Add(go);
            Grid.SetColumn(right, 1);
            actions.Children.Add(skip); actions.Children.Add(right);
            body.Children.Add(actions);

            Button closeX;
            Window win = NewBrandedWindow(520, body, out closeX);
            win.Title = T.Get("Atualização disponível · Azul Groove Music");

            bool busy = false, installed = false, failed = false;
            CancellationTokenSource cts = null;
            win.Closing += (a, b) => { if (busy) b.Cancel = true; };

            void SetBar(int pct)
            {
                double w = Math.Max(0, track.ActualWidth * Math.Min(100, pct) / 100.0);
                fill.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(140)));
            }

            skip.Click += (a, b) =>
            {
                if (failed) { try { Process.Start(new ProcessStartInfo(UpdateReleasesUrl) { UseShellExecute = true }); } catch (Exception ex) { App.LogError("Abrir Releases", ex); } win.Close(); return; }
                prefs.UpdateSkipped = info.Tag; prefs.Save();   // não pergunta de novo para esta versão (dá para atualizar em Configurações)
                win.Close();
            };
            later.Click += (a, b) => win.Close();
            cancel.Click += (a, b) => cts?.Cancel();

            async Task StartAsync()
            {
                busy = true; failed = false; closeX.IsEnabled = false;
                actions.Visibility = Visibility.Collapsed; progress.Visibility = Visibility.Visible; cancel.Visibility = Visibility.Visible;
                status.Foreground = mute; status.Text = T.Get("Baixando atualização… 0%");
                fill.BeginAnimation(FrameworkElement.WidthProperty, null); fill.Width = 0;
                cts = new CancellationTokenSource();
                try
                {
                    string file = await UpdDownloadAsync(info, pct =>
                    {
                        SetBar(pct);
                        status.Text = T.Get(pct >= 100 ? "Conferindo o arquivo…" : "Baixando atualização… " + pct + "%");
                        SetUpdStatus("Baixando atualização… " + pct + "%");
                    }, cts.Token);

                    cancel.Visibility = Visibility.Collapsed;
                    status.Text = T.Get("Instalando em segundo plano… o app fecha e abre de novo sozinho.");
                    SetUpdStatus("Instalando a atualização…");
                    SetBar(100);
                    await Task.Delay(700);
                    UpdLaunchInstaller(file);
                    installed = true; busy = false;
                    win.Close();
                }
                catch (OperationCanceledException)
                {
                    busy = false; SetUpdStatus("Atualização cancelada."); win.Close();
                }
                catch (Exception ex)
                {
                    busy = false; failed = true; closeX.IsEnabled = true;
                    App.LogError("Atualização", ex);
                    SetUpdStatus("Falha ao atualizar: " + ex.Message);
                    progress.Visibility = Visibility.Visible; cancel.Visibility = Visibility.Collapsed;
                    status.Foreground = (Brush)new BrushConverter().ConvertFromString("#FF8A8F");
                    status.Text = T.Get("Não foi possível atualizar: {0}", ex.Message);
                    skip.Content = T.Get("Abrir Releases no GitHub"); later.Content = T.Get("Fechar"); go.Content = T.Get("Tentar de novo");
                    actions.Visibility = Visibility.Visible;
                }
            }
            go.Click += async (a, b) => await StartAsync();

            win.ShowDialog();
            if (installed) Close();   // fecha o app; o instalador (silencioso) abre ele de novo no fim
        }

        async Task<string> UpdDownloadAsync(UpdateInfo info, Action<int> progress, CancellationToken ct)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AzulGrooveUpdate");
            Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(info.AssetName));
            try
            {
                using (HttpResponseMessage resp = await updDownload.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? -1;
                    using (Stream src = await resp.Content.ReadAsStreamAsync())
                    using (FileStream dst = File.Create(file))
                    {
                        byte[] buf = new byte[81920];
                        long done = 0; int n, last = -1;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                        {
                            await dst.WriteAsync(buf, 0, n, ct);
                            done += n;
                            if (total > 0)
                            {
                                int pct = (int)(done * 100 / total);
                                if (pct != last) { last = pct; progress(pct); }
                            }
                        }
                    }
                    if (total > 0 && new FileInfo(file).Length != total) throw new IOException("Download incompleto.");
                }
                if (!string.IsNullOrEmpty(info.Sha256))   // confere a integridade quando o GitHub informa o hash
                {
                    string expected = info.Sha256;
                    string hash = await Task.Run(() =>
                    {
                        using (var sha = SHA256.Create())
                        using (FileStream fs = File.OpenRead(file))
                            return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", string.Empty);
                    }, ct);
                    if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("O arquivo baixado não confere com o hash do GitHub.");
                }
                return file;
            }
            catch
            {
                try { if (File.Exists(file)) File.Delete(file); } catch (Exception ex2) { System.Diagnostics.Trace.WriteLine("Could not delete update file: " + ex2.Message); }
                throw;
            }
        }

        // Atualização totalmente automática: baixa, instala em modo silencioso e o instalador abre o app de novo.
        // Só roda com a música parada (não corta o que você está ouvindo); se estiver tocando, tenta de novo no próximo ciclo.
        async Task UpdSilentInstallAsync(UpdateInfo info)
        {
            bool playing = false;
            try { playing = _mp != null && _mp.IsPlaying; } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not read playback state: " + ex.Message); }
            if (playing) { SetUpdStatus("Versão " + info.Version.ToString(3) + " disponível: instala quando a música parar."); return; }
            try
            {
                SetUpdStatus("Baixando a versão " + info.Version.ToString(3) + " em segundo plano…");
                string file;
                using (var limit = new CancellationTokenSource(TimeSpan.FromMinutes(20)))   // conexão parada não trava as próximas buscas de atualização
                    file = await UpdDownloadAsync(info, pct => SetUpdStatus("Baixando atualização… " + pct + "%"), limit.Token);
                bool playingNow = false;
                try { playingNow = _mp != null && _mp.IsPlaying; } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not read playback state: " + ex.Message); }
                if (playingNow) { SetUpdStatus("Versão " + info.Version.ToString(3) + " baixada: instala quando a música parar."); return; }
                SetUpdStatus("Instalando a atualização…");
                Toast("Atualizando o Azul Groove… o app abre de novo sozinho");
                await Task.Delay(1500);
                UpdLaunchInstaller(file);
                Close();
            }
            catch (Exception ex)
            {
                App.LogError("Atualização automática", ex);
                SetUpdStatus("Não foi possível atualizar sozinho: " + ex.Message);
            }
        }

        // Roda o instalador sem janelas (/VERYSILENT). /RELAUNCH=1 faz o instalador abrir o app de novo no fim.
        static void UpdLaunchInstaller(string file)
        {
            string log = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file), "install.log");
            string args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1 /LOG=\"" + log + "\"";
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }

        // Apaga instaladores antigos baixados em atualizações anteriores
        static void UpdCleanup()
        {
            try
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AzulGrooveUpdate");
                if (!Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir, "AzulGrooveMusic-Setup*.exe"))
                    try { File.Delete(f); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not delete old installer: " + ex.Message); }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Update cleanup failed: " + ex.Message); }
        }
    }
}
