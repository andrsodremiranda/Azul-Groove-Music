using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AzulGroove
{
    // Assistente de gostos: na primeira abertura (e quando o app ganha perguntas novas) o usuário marca
    // estilos, artistas e quanto quer descobrir. Isso alimenta a fila inteligente e fica pronto para as
    // recomendações da tela inicial (veja TasteRecommend). Dá para refazer em Configurações → Fila inteligente.
    public partial class MainWindow
    {
        // Suba este número quando acrescentar perguntas novas: quem já usa o app vê o assistente de novo uma vez.
        const int TasteCurrentVersion = 1;

        // (rótulo mostrado, palavras que aparecem no nome/artista/álbum/pasta e nas buscas do YouTube)
        static readonly string[][] TasteGenreList =
        {
            new[] { "Sertanejo", "sertanejo" }, new[] { "Funk", "funk" }, new[] { "Pagode", "pagode", "samba" },
            new[] { "MPB", "mpb", "bossa" }, new[] { "Rock", "rock" }, new[] { "Pop", "pop" },
            new[] { "Hip-hop / Rap", "rap", "hip hop", "hip-hop", "trap" }, new[] { "Eletrônica", "electronic", "eletrônica", "edm", "house", "techno" },
            new[] { "Gospel", "gospel", "louvor" }, new[] { "Forró", "forró", "forro", "piseiro" },
            new[] { "Reggae", "reggae" }, new[] { "Jazz / Blues", "jazz", "blues" },
            new[] { "Clássica", "classical", "clássica", "orquestra" }, new[] { "Lo-fi / Relax", "lofi", "lo-fi", "chill", "relax" },
            new[] { "Metal", "metal" }, new[] { "Country", "country" }, new[] { "K-pop", "k-pop", "kpop" },
            new[] { "Trilhas / Games", "soundtrack", "ost", "trilha" }
        };

        static readonly string[] TasteLangList = { "Português", "Inglês", "Espanhol", "Outros" };

        bool tasteAsked;   // já tentou mostrar nesta abertura

        bool TasteHasData => prefs.TasteGenres.Count > 0 || prefs.TasteArtists.Count > 0;

        // Chamado depois das boas-vindas/novidades (na primeira vez ou quando TasteCurrentVersion sobe)
        async System.Threading.Tasks.Task ShowTasteIfNeededAsync()
        {
            try
            {
                if (tasteAsked || prefs.TasteVersion >= TasteCurrentVersion) return;
                tasteAsked = true;
                await System.Threading.Tasks.Task.Delay(400);
                if (appClosing || !IsVisible || WindowState == WindowState.Minimized) { tasteAsked = false; return; }   // tenta de novo na próxima abertura
                ShowTasteWizard();
            }
            catch (Exception ex) { App.LogError("Gostos", ex); }
        }

        void PrefTaste_Click(object s, RoutedEventArgs e)
        {
            try { ShowTasteWizard(); }
            catch (Exception ex) { App.LogError("Gostos", ex); }
        }

        // Botão em forma de "chip" (ou cartão) que liga/desliga. big = versão grande da tela de configuração inicial.
        ControlTemplate TasteToggleTemplate(int radius, string align)
        {
            return ParseTemplate("ToggleButton",
                "<Border x:Name='b' CornerRadius='" + radius + "' Background='#14FFFFFF' BorderBrush='#2678AAFF' BorderThickness='1' Padding='{TemplateBinding Padding}'>" +
                "<ContentPresenter HorizontalAlignment='" + align + "' VerticalAlignment='Center'/></Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='#24FFFFFF'/></Trigger>" +
                "<Trigger Property='IsChecked' Value='True'><Setter TargetName='b' Property='Background' Value='{DynamicResource Az33}'/>" +
                "<Setter TargetName='b' Property='BorderBrush' Value='{DynamicResource Cyan}'/></Trigger></ControlTemplate.Triggers>");
        }

        System.Windows.Controls.Primitives.ToggleButton TasteChip(string text, bool on, bool big = false)
        {
            var b = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = T.Get(text), IsChecked = on, Tag = text, Foreground = (Brush)FindResource("Foam"),
                Cursor = System.Windows.Input.Cursors.Hand, FocusVisualStyle = null,
                Margin = big ? new Thickness(4, 0, 4, 9) : new Thickness(0, 0, 8, 8),
                Padding = big ? new Thickness(16, 8, 16, 8) : new Thickness(14, 8, 14, 8),
                FontSize = big ? 14.5 : 13
            };
            b.Template = TasteToggleTemplate(big ? 22 : 16, "Center");
            return b;
        }

        // Cartão grande de escolha única (título + descrição)
        System.Windows.Controls.Primitives.ToggleButton TasteCard(string title, string desc, bool on)
        {
            var box = new StackPanel();
            box.Children.Add(new TextBlock { Text = T.Get(title), FontSize = 15.5, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Foam"), TextWrapping = TextWrapping.Wrap });
            box.Children.Add(new TextBlock { Text = T.Get(desc), FontSize = 12.5, LineHeight = 18, Margin = new Thickness(0, 5, 0, 0), Foreground = (Brush)FindResource("Mute"), TextWrapping = TextWrapping.Wrap });
            var b = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = box, IsChecked = on, Width = 214, Margin = new Thickness(5, 0, 5, 10), Padding = new Thickness(16, 14, 16, 14),
                Cursor = System.Windows.Input.Cursors.Hand, FocusVisualStyle = null, VerticalAlignment = VerticalAlignment.Stretch
            };
            b.Template = TasteToggleTemplate(18, "Left");
            return b;
        }

        static List<string> TasteSplit(string text)
        {
            return (text ?? string.Empty).Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).Take(15).ToList();
        }

        // Configuração inicial em janela, no estilo do Windows depois de instalar: uma pergunta por página, Voltar/Avançar e uma tela final.
        void ShowTasteWizard()
        {
            bool redo = prefs.TasteVersion > 0;   // refazendo pelas Configurações: pula a página de boas-vindas
            var foam = (Brush)FindResource("Foam");
            var mute = (Brush)FindResource("Mute");

            var layout = new Grid { Height = 560 };   // altura fixa: a janela não muda de tamanho entre as páginas
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var host = new ContentControl { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28, 10, 28, 0), MaxWidth = 720, IsTabStop = false, Focusable = false };
            layout.Children.Add(host);

            // ---- páginas ----
            TextBlock Txt(string text, double size, FontWeight weight, Brush fg, double top, double maxWidth)
            {
                return new TextBlock
                {
                    Text = T.Get(text), FontSize = size, FontWeight = weight, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = maxWidth, LineHeight = size * 1.4,
                    Margin = new Thickness(0, top, 0, 0)
                };
            }

            StackPanel Page(string glyph, string title, string sub)
            {
                var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                var badge = new Border
                {
                    Width = 60, Height = 60, CornerRadius = new CornerRadius(19), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 18),
                    Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 27, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                badge.SetResourceReference(Border.BackgroundProperty, "Grad");
                p.Children.Add(badge);
                p.Children.Add(Txt(title, 28, FontWeights.SemiBold, foam, 0, 680));
                p.Children.Add(Txt(sub, 14.5, FontWeights.Normal, mute, 10, 560));
                return p;
            }

            WrapPanel Wrap() => new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0), MaxWidth = 700 };

            // 0) boas-vindas
            StackPanel p0 = Page("\uE8D6", "Olá! Vamos deixar o Azul Groove com a sua cara",
                "São só quatro perguntinhas rápidas. Assim a fila inteligente e as recomendações já começam do seu jeito, antes de o app aprender com o que você ouve.");

            // 1) estilos
            StackPanel p1 = Page("\uE8D6", "Quais estilos você curte?", "Escolha quantos quiser. Dá para mudar depois.");
            var genreChips = new List<System.Windows.Controls.Primitives.ToggleButton>();
            WrapPanel genreWrap = Wrap();
            foreach (string[] g in TasteGenreList)
            {
                var chip = TasteChip(g[0], prefs.TasteGenres.Contains(g[0]), true);
                genreChips.Add(chip); genreWrap.Children.Add(chip);
            }
            p1.Children.Add(genreWrap);

            // 2) artistas
            StackPanel p2 = Page("\uE77B", "Quem você mais ouve?", "Digite artistas ou bandas, separados por vírgula. Se preferir, deixe em branco.");
            TextBox artists = BrandedTextBox(string.Join(", ", prefs.TasteArtists));
            artists.FontSize = 16; artists.Padding = new Thickness(14, 11, 14, 11); artists.Width = 520; artists.Margin = new Thickness(0, 24, 0, 0);
            p2.Children.Add(artists);
            p2.Children.Add(Txt("Ex.: Marília Mendonça, Queen, Djavan", 13, FontWeights.Normal, mute, 10, 520));

            // 3) idioma
            StackPanel p3 = Page("\uE774", "Em que idioma você ouve?", "Pode marcar mais de um.");
            var langChips = new List<System.Windows.Controls.Primitives.ToggleButton>();
            WrapPanel langWrap = Wrap();
            foreach (string l in TasteLangList)
            {
                var chip = TasteChip(l, prefs.TasteLangs.Contains(l), true);
                langChips.Add(chip); langWrap.Children.Add(chip);
            }
            p3.Children.Add(langWrap);

            // 4) descoberta
            StackPanel p4 = Page("\uE8FD", "Como você quer as sugestões?", "Você pode mudar isso depois.");
            string[][] discover =
            {
                new[] { "Só o que eu já conheço", "Músicas parecidas com as que você já ouve." },
                new[] { "Equilibrado", "Um pouco do que você conhece e um pouco de novidade." },
                new[] { "Quero descobrir coisas novas", "Mais músicas novas e diferentes dentro do seu estilo." }
            };
            var cards = new List<System.Windows.Controls.Primitives.ToggleButton>();
            WrapPanel cardWrap = Wrap();
            for (int i = 0; i < discover.Length; i++)
            {
                var card = TasteCard(discover[i][0], discover[i][1], i == prefs.TasteDiscovery);
                card.Click += (a, b) => { foreach (var c in cards) c.IsChecked = ReferenceEquals(c, a); };   // escolha única
                cards.Add(card); cardWrap.Children.Add(card);
            }
            p4.Children.Add(cardWrap);

            // 5) final: "Preparando tudo..." e depois "Tudo pronto!"
            StackPanel p5 = Page("\uE73E", "Preparando tudo para você…", "Ajustando a fila inteligente com o que você escolheu.");
            var finalTitle = (TextBlock)p5.Children[1];
            var finalSub = (TextBlock)p5.Children[2];
            var track = new Border { Width = 420, Height = 6, CornerRadius = new CornerRadius(3), Background = (Brush)new BrushConverter().ConvertFromString("#22FFFFFF"), Margin = new Thickness(0, 26, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            var fill = new Border { Width = 0, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3) };
            fill.SetResourceReference(Border.BackgroundProperty, "Grad");
            track.Child = fill;
            p5.Children.Add(track);
            TextBlock status = Txt(" ", 13.5, FontWeights.Normal, mute, 14, 600);
            p5.Children.Add(status);

            var pages = new List<UIElement> { p0, p1, p2, p3, p4, p5 };
            const int LastPage = 5;
            int start = redo ? 1 : 0;
            int idx = -1;
            bool saved = false;

            // ---- rodapé: Voltar / Agora não · pontinhos · Avançar ----
            Button back = BrandedButton(T.Get("Voltar"), false, true);
            Button skip = BrandedButton(T.Get("Agora não"), false, true);
            Button next = BrandedButton(T.Get("Começar"), true, false);
            next.IsDefault = true; next.MinWidth = 150;

            var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(back); left.Children.Add(skip);
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var dotList = new List<System.Windows.Shapes.Ellipse>();
            for (int i = 0; i < 4; i++)
            {
                var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Margin = new Thickness(5, 0, 5, 0) };
                dotList.Add(dot); dots.Children.Add(dot);
            }
            next.HorizontalAlignment = HorizontalAlignment.Right; next.VerticalAlignment = VerticalAlignment.Center;
            var footer = new Grid { Margin = new Thickness(28, 0, 28, 24) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(dots, 1); Grid.SetColumn(next, 2);
            footer.Children.Add(left); footer.Children.Add(dots); footer.Children.Add(next);
            Grid.SetRow(footer, 1);
            layout.Children.Add(footer);

            Button closeX;
            Window win = NewBrandedWindow(780, layout, out closeX);
            win.Title = T.Get("Seus gostos") + " · Azul Groove Music";

            Brush dotOff = (Brush)new BrushConverter().ConvertFromString("#33FFFFFF");
            void Go(int to, int dir)
            {
                idx = to;
                UIElement page = pages[to];
                host.Content = page;
                var shift = new TranslateTransform(dir * 46, 0);
                page.RenderTransform = shift; page.Opacity = 0;
                page.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(340)));
                shift.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(dir * 46, 0, TimeSpan.FromMilliseconds(420))
                { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });

                bool finalPage = to == LastPage;
                back.Visibility = to > start && !finalPage ? Visibility.Visible : Visibility.Collapsed;
                skip.Visibility = finalPage ? Visibility.Collapsed : Visibility.Visible;
                dots.Visibility = to >= 1 && !finalPage ? Visibility.Visible : Visibility.Hidden;
                for (int i = 0; i < dotList.Count; i++)
                {
                    if (i == to - 1) dotList[i].SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Cyan");
                    else dotList[i].Fill = dotOff;
                }
                next.Content = T.Get(to == 0 ? "Começar" : to == 4 ? "Concluir" : "Avançar");
                next.Visibility = finalPage ? Visibility.Collapsed : Visibility.Visible;
                if (to == 2) artists.Focus(); else next.Focus();
            }

            void Collect()
            {
                prefs.TasteGenres = genreChips.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
                prefs.TasteLangs = langChips.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
                prefs.TasteArtists = TasteSplit(artists.Text);
                int d = cards.FindIndex(c => c.IsChecked == true);
                prefs.TasteDiscovery = d < 0 ? 1 : d;
                prefs.TasteVersion = TasteCurrentVersion;
                prefs.Save();
                tasteTerms = null;   // recalcula as palavras-chave
                saved = true;
            }

            System.Windows.Threading.DispatcherTimer msgTimer = null;
            void RunFinish()
            {
                string[] msgs = { "Anotando seus estilos e artistas…", "Ajustando a fila inteligente…", "Preparando as recomendações…", "Quase lá…" };
                int m = 0;
                status.Text = T.Get(msgs[m++]);
                msgTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
                msgTimer.Tick += (a, b) => { if (m < msgs.Length) status.Text = T.Get(msgs[m++]); else msgTimer.Stop(); };
                msgTimer.Start();
                var grow = new System.Windows.Media.Animation.DoubleAnimation(0, 420, TimeSpan.FromMilliseconds(2700))
                { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut } };
                grow.Completed += (a, b) =>
                {
                    msgTimer.Stop();
                    finalTitle.Text = T.Get("Tudo pronto!");
                    finalSub.Text = T.Get("O Azul Groove já conhece o seu gosto. Você pode mudar isso quando quiser em Configurações → Fila inteligente.");
                    status.Text = string.Empty;
                    next.Content = T.Get("Começar a ouvir");
                    next.Visibility = Visibility.Visible; next.Focus();
                };
                fill.BeginAnimation(FrameworkElement.WidthProperty, grow);
            }

            next.Click += (a, b) =>
            {
                if (idx < 4) Go(idx + 1, 1);
                else if (idx == 4) { Collect(); Go(LastPage, 1); RunFinish(); }
                else win.Close();
            };
            back.Click += (a, b) => { if (idx > start) Go(idx - 1, -1); };
            skip.Click += (a, b) =>
            {
                if (!redo) { prefs.TasteVersion = TasteCurrentVersion; prefs.Save(); }   // pulou: não pergunta de novo sozinho
                win.Close();
            };
            win.Closed += (a, b) =>
            {
                if (!saved && !redo && prefs.TasteVersion < TasteCurrentVersion) { prefs.TasteVersion = TasteCurrentVersion; prefs.Save(); }   // fechou no X: igual a "Agora não"
            };
            win.Closed += (a, b) => { try { msgTimer?.Stop(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Taste timer: " + ex.Message); } };

            win.Loaded += (a, b) => Go(start, 1);
            win.ShowDialog();
            if (saved) Toast("Gostos salvos! A fila inteligente já usa isso.");
        }

        // ---------- uso nos cálculos ----------
        HashSet<string> tasteArtistSet;
        List<string> tasteTerms;   // palavras-chave dos estilos marcados

        void TasteEnsureCache()
        {
            if (tasteTerms != null) return;
            tasteArtistSet = new HashSet<string>(prefs.TasteArtists, StringComparer.OrdinalIgnoreCase);
            tasteTerms = new List<string>();
            foreach (string[] g in TasteGenreList)
                if (prefs.TasteGenres.Contains(g[0])) tasteTerms.AddRange(g.Skip(1));
        }

        // Bônus de nota (0 a ~1,6) para a fila inteligente. Vale mais enquanto o app ainda não aprendeu com o seu uso.
        double TasteBonus(Item it, bool hasHistory)
        {
            if (!TasteHasData) return 0;
            TasteEnsureCache();
            double b = 0;
            if (!string.IsNullOrEmpty(it.Artist))
            {
                if (tasteArtistSet.Contains(it.Artist)) b += 1.2;
                else if (tasteArtistSet.Any(a => it.Artist.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0 || a.IndexOf(it.Artist, StringComparison.OrdinalIgnoreCase) >= 0)) b += 0.8;
            }
            if (tasteTerms.Count > 0)
            {
                string hay = (it.Name + " " + it.Artist + " " + it.Album + " " + System.IO.Path.GetDirectoryName(it.FilePath ?? string.Empty)).ToLowerInvariant();
                if (tasteTerms.Any(t => hay.Contains(t))) b += 0.6;
            }
            return hasHistory ? b * 0.6 : b;   // com histórico de escuta, o que você realmente ouve manda mais
        }

        bool TasteMatches(Item it) => TasteBonus(it, false) >= 0.5;

        // Peso da "novidade" para a fila (0 = só conhecidas, 1 = equilibrado, 2 = descobrir)
        double TasteNoveltyWeight => prefs.TasteDiscovery == 0 ? 0.6 : prefs.TasteDiscovery == 2 ? 1.6 : 1.0;

        // Termos de busca para o YouTube quando o app ainda não tem histórico para usar de "semente"
        string TasteSeedQuery()
        {
            if (!TasteHasData) return null;
            var parts = new List<string>();
            if (prefs.TasteArtists.Count > 0) parts.Add(prefs.TasteArtists[smartRnd.Next(prefs.TasteArtists.Count)]);
            else if (prefs.TasteGenres.Count > 0)
            {
                string g = prefs.TasteGenres[smartRnd.Next(prefs.TasteGenres.Count)];
                string[] row = TasteGenreList.FirstOrDefault(x => x[0] == g);
                parts.Add(row != null ? row[1] : g);
                parts.Add("músicas populares");
            }
            return string.Join(" ", parts);
        }

        // ---------- Pronto para a tela inicial (futuro) ----------
        // Devolve até "max" músicas da biblioteca que combinam com o que a pessoa marcou e com o que ouve.
        // Para mostrar no Início: crie uma seção "Recomendadas para você" e ligue a lista a este método.
        public List<Item> TasteRecommend(int max)
        {
            TasteEnsureCache();
            LoadSmart();
            SmartCtx ctx = SmartBuildCtx();
            return ctx.Cand
                .Select(i => new KeyValuePair<Item, double>(i, SmartScore(i, ctx, null) + smartRnd.NextDouble() * 0.5))
                .OrderByDescending(p => p.Value).Take(Math.Max(1, max)).Select(p => p.Key).ToList();
        }
    }
}
