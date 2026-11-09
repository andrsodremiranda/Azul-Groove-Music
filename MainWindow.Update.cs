using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AzulGroove
{
    // Atualização: ao abrir o app (se estiver ligado nas Configurações) consulta os Releases do GitHub,
    // procura o instalador "AzulGrooveMusic-Setup*.exe" e, se houver versão mais nova, oferece baixar e instalar.
    public partial class MainWindow
    {
        const string UpdateRepo = "andrsodremiranda/Azul-Groove-Music";
        const string UpdateApi = "https://api.github.com/repos/" + UpdateRepo + "/releases/latest";
        const string UpdateReleasesUrl = "https://github.com/" + UpdateRepo + "/releases";
        const string UpdateDownloadPrefix = "https://github.com/" + UpdateRepo + "/releases/download/";
        const string UpdateAssetPrefix = "AzulGrooveMusic-Setup";

        static readonly HttpClient updHttp = CreateUpdHttp(TimeSpan.FromSeconds(20));
        static readonly HttpClient updDownload = CreateUpdHttp(System.Threading.Timeout.InfiniteTimeSpan);
        bool updBusy;

        sealed class UpdateInfo
        {
            public Version Version;
            public string Tag, AssetName, AssetUrl, Sha256;
        }

        static HttpClient CreateUpdHttp(TimeSpan timeout)
        {
            var c = new HttpClient { Timeout = timeout };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("AzulGrooveMusic/" + UpdCurrent().ToString(3));
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        static Version UpdNormalize(Version v) => new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
        static Version UpdCurrent() => UpdNormalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0));

        static Version UpdParse(string tag)
        {
            Match m = Regex.Match(tag ?? string.Empty, @"\d+(\.\d+){1,3}");
            return m.Success && Version.TryParse(m.Value, out Version v) ? UpdNormalize(v) : null;
        }

        void SetUpdStatus(string text) { if (UpdStatusTxt != null) UpdStatusTxt.Text = text; }

        async Task<UpdateInfo> UpdFetchLatestAsync()
        {
            using (HttpResponseMessage r = await updHttp.GetAsync(UpdateApi))
            {
                if (r.StatusCode == HttpStatusCode.NotFound) return null;   // ainda não há nenhum release
                r.EnsureSuccessStatusCode();
                using (JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync()))
                {
                    JsonElement root = doc.RootElement;
                    string tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() : null;
                    Version v = UpdParse(tag);
                    if (v == null) return null;
                    var info = new UpdateInfo { Version = v, Tag = tag };
                    if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement a in assets.EnumerateArray())
                        {
                            string name = a.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                            if (name == null || !name.StartsWith(UpdateAssetPrefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                            info.AssetName = name;
                            info.AssetUrl = a.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                            string digest = a.TryGetProperty("digest", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                            if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) info.Sha256 = digest.Substring(7);
                            break;
                        }
                    }
                    return info;
                }
            }
        }

        // Chamado ao abrir o app: espera a janela terminar de carregar e consulta em segundo plano
        async Task UpdateAutoCheckAsync()
        {
            try { await Task.Delay(6000); await UpdateCheckAsync(false); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Auto update check failed: " + ex.Message); }
        }

        async Task UpdateCheckAsync(bool manual)
        {
            if (updBusy) return;
            updBusy = true;
            try
            {
                if (manual) SetUpdStatus("Procurando atualizações…");
                UpdateInfo info = await UpdFetchLatestAsync();
                Version current = UpdCurrent();
                if (info == null || info.Version <= current)
                {
                    SetUpdStatus("Você está na versão mais recente (" + current.ToString(3) + ").");
                    if (manual) Toast("Você já está na versão mais recente");
                    return;
                }
                if (string.IsNullOrEmpty(info.AssetUrl) || !info.AssetUrl.StartsWith(UpdateDownloadPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    SetUpdStatus("Versão " + info.Version.ToString(3) + " encontrada, mas sem o instalador " + UpdateAssetPrefix + " nos Releases.");
                    if (manual) Toast("Versão nova encontrada, mas sem instalador nos Releases");
                    return;
                }
                SetUpdStatus("Nova versão disponível: " + info.Version.ToString(3) + " (você tem " + current.ToString(3) + ").");
                if (!manual && string.Equals(prefs.UpdateSkipped, info.Tag, StringComparison.OrdinalIgnoreCase)) return;   // você já disse "agora não" para esta

                MessageBoxResult answer = MessageBox.Show(this,
                    "Nova versão do Azul Groove Music disponível: " + info.Version.ToString(3) + "\n(você está na " + current.ToString(3) + ")\n\n" +
                    "Baixar e instalar agora?\n\nSim: baixa e abre o instalador (o app fecha).\nNão: não perguntar de novo para esta versão (dá para atualizar depois em Configurações).",
                    "Atualização disponível", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (answer != MessageBoxResult.Yes)
                {
                    prefs.UpdateSkipped = info.Tag; prefs.Save();
                    return;
                }
                await UpdInstallAsync(info);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Update check failed: " + ex.Message);
                if (manual) { SetUpdStatus("Não foi possível buscar atualizações (sem internet?)."); Toast("Não foi possível buscar atualizações"); }
            }
            finally { updBusy = false; }
        }

        async Task UpdInstallAsync(UpdateInfo info)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AzulGrooveUpdate");
            Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(info.AssetName));
            try
            {
                using (HttpResponseMessage resp = await updDownload.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? -1;
                    using (Stream src = await resp.Content.ReadAsStreamAsync())
                    using (FileStream dst = File.Create(file))
                    {
                        byte[] buf = new byte[81920];
                        long done = 0; int n, last = -1;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                        {
                            await dst.WriteAsync(buf, 0, n);
                            done += n;
                            if (total > 0)
                            {
                                int pct = (int)(done * 100 / total);
                                if (pct != last && pct % 5 == 0) { last = pct; SetUpdStatus("Baixando atualização… " + pct + "%"); Toast("Baixando atualização… " + pct + "%"); }
                            }
                        }
                    }
                    if (total > 0 && new FileInfo(file).Length != total) throw new IOException("Download incompleto.");
                }
                if (!string.IsNullOrEmpty(info.Sha256))   // confere a integridade quando o GitHub informa o hash
                {
                    string hash;
                    using (var sha = SHA256.Create())
                    using (FileStream fs = File.OpenRead(file))
                        hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", string.Empty);
                    if (!string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("O arquivo baixado não confere com o hash do GitHub.");
                }
                SetUpdStatus("Abrindo o instalador…");
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                Close();   // fecha o app para o instalador poder atualizar os arquivos
            }
            catch (Exception ex)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch (Exception ex2) { System.Diagnostics.Trace.WriteLine("Could not delete update file: " + ex2.Message); }
                App.LogError("Atualização", ex);
                SetUpdStatus("Falha ao baixar a atualização: " + ex.Message);
                MessageBox.Show(this, "Não foi possível baixar a atualização:\n" + ex.Message + "\n\nVocê pode baixar manualmente em:\n" + UpdateReleasesUrl,
                    "Atualização", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---------- Página "Sobre" ----------
        void RefreshAboutView()
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            AboutVersionTxt.Text = "Versão " + (v != null ? v.ToString(3) : "?");
            PrefUpdate.IsChecked = prefs.AutoUpdate;
        }

        void PrefUpdate_Click(object s, RoutedEventArgs e)
        {
            prefs.AutoUpdate = ((CheckBox)s).IsChecked == true;
            if (prefs.AutoUpdate) prefs.UpdateSkipped = string.Empty;
            prefs.Save();
        }

        async void PrefUpdateNow_Click(object s, RoutedEventArgs e) => await UpdateCheckAsync(true);

        void PrefReleases_Click(object s, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(UpdateReleasesUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Toast("Não foi possível abrir o navegador: " + ex.Message); }
        }

        void PrefLicense_Click(object s, RoutedEventArgs e)
        {
            string text = "Licença não encontrada.";
            try
            {
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("LICENSE.txt"))
                    if (st != null) using (var rd = new StreamReader(st, Encoding.UTF8)) text = rd.ReadToEnd();
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not read license: " + ex.Message); }

            var abyss = (Brush)FindResource("Abyss");
            var foam = (Brush)FindResource("Foam");
            var mute = (Brush)FindResource("Mute");
            var cyan = (Brush)FindResource("Cyan");
            var cardBg = (Brush)new BrushConverter().ConvertFromString("#1BFFFFFF");
            var cardBorder = (Brush)new BrushConverter().ConvertFromString("#2678AAFF");

            var win = new Window
            {
                Title = "Licença de uso · Azul Groove Music",
                Width = 780, Height = 680, MinWidth = 520, MinHeight = 420,
                Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = abyss, Foreground = foam,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                UseLayoutRounding = true, SnapsToDevicePixels = true, Icon = Icon
            };
            System.Windows.Shell.WindowChrome.SetWindowChrome(win, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 44, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
            });

            // Fundo com o mesmo brilho azul do app
            var root = new Grid();
            root.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 560, Height = 560, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -220, -140, 0), IsHitTestVisible = false,
                Fill = new RadialGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x60, 0x4F, 0xD1, 0xFF), 0), new GradientStop(Color.FromArgb(0x00, 0x4F, 0xD1, 0xFF), 1)
                })
            });
            root.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 520, Height = 520, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(-180, 0, 0, -260), IsHitTestVisible = false,
                Fill = new RadialGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x70, 0x2F, 0x8C, 0xFF), 0), new GradientStop(Color.FromArgb(0x00, 0x2F, 0x8C, 0xFF), 1)
                })
            });

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(grid);

            // Barra de título própria (igual à do app)
            var bar = new Grid();
            var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
            if (TitleIcon != null && TitleIcon.Source != null)
                left.Children.Add(new System.Windows.Controls.Image { Source = TitleIcon.Source, Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0) });
            left.Children.Add(new TextBlock { Text = "Azul Groove Music", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(left);
            var closeX = new Button
            {
                Style = (Style)FindResource("CapBtn"), Content = "\uE8BB", HorizontalAlignment = HorizontalAlignment.Right,
                Tag = FindResource("CloseHover")
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(closeX, true);
            closeX.Click += (a, b) => win.Close();
            bar.Children.Add(closeX);
            grid.Children.Add(bar);

            // Conteúdo: título + cartão com a licença em seções
            var lines = text.Replace("\r", "").TrimStart('\uFEFF').Split('\n');
            var content = new StackPanel { Margin = new Thickness(32, 8, 32, 8) };
            content.Children.Add(new TextBlock
            {
                Text = "Licença de uso", FontSize = 28, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Grad"), Margin = new Thickness(0, 0, 0, 4)
            });

            int idx = 0;
            // 1ª linha = título do documento (já representado acima); linhas seguintes até o 1º espaço = versão e copyright
            if (lines.Length > 3) idx = 1;
            var sub = new List<string>();
            while (idx < lines.Length && !string.IsNullOrWhiteSpace(lines[idx])) sub.Add(lines[idx++].Trim());
            if (sub.Count > 0)
                content.Children.Add(new TextBlock { Text = string.Join("\n", sub), FontSize = 13, Foreground = mute, Margin = new Thickness(0, 0, 0, 16), LineHeight = 20 });

            var card = new Border { Background = cardBg, BorderBrush = cardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(24, 20, 24, 22) };
            var body = new StackPanel();
            card.Child = body;
            content.Children.Add(card);

            var heading = new Regex(@"^\d+\.\s+\S", RegexOptions.Compiled);
            var subItem = new Regex(@"^(\d+\.\d+\.|[a-z]\))\s", RegexOptions.Compiled);
            bool first = true;
            for (; idx < lines.Length; idx++)
            {
                string ln = lines[idx].Trim();
                if (ln.Length == 0) continue;
                if (heading.IsMatch(ln) && !subItem.IsMatch(ln) && ln == ln.ToUpperInvariant() && ln.Length < 80)
                {
                    body.Children.Add(new TextBlock
                    {
                        Text = ln, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = cyan,
                        Margin = new Thickness(0, first ? 0 : 20, 0, 6)
                    });
                    first = false;
                    continue;
                }
                bool isSub = subItem.IsMatch(ln);
                body.Children.Add(new TextBlock
                {
                    Text = ln, FontSize = 13.5, LineHeight = 21, TextWrapping = TextWrapping.Wrap,
                    Foreground = isSub ? foam : mute,
                    Margin = new Thickness(isSub ? 14 : 0, 0, 0, 6)
                });
                first = false;
            }

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content, Focusable = false };
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            var close = new Button
            {
                Style = (Style)FindResource("PillBtn"), Content = "Fechar", Padding = new Thickness(34, 10, 34, 10),
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(32, 8, 32, 22), IsCancel = true, IsDefault = true
            };
            close.Click += (a, b) => win.Close();
            Grid.SetRow(close, 2);
            grid.Children.Add(close);

            win.Content = new Border { BorderBrush = cardBorder, BorderThickness = new Thickness(1), Child = root };

            // Entrada suave
            win.Opacity = 0;
            win.Loaded += (a, b) => win.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            win.ShowDialog();
        }
    }
}
