using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AzulGroove
{
    // Tela de boas-vindas (primeira vez) e de novidades (depois de cada atualização). Dá para rever em Sobre → Ver novidades.
    public partial class MainWindow
    {
        sealed class ChangeEntry
        {
            public string Version;
            public string Title;
            public string[] Items;
        }

        // Histórico de novidades, da versão mais nova para a mais antiga.
        // Ao lançar uma versão nova: suba o <Version> do AzulGrooveMusic.csproj (e do .iss / build.bat) e acrescente uma entrada AQUI, no topo.
        static readonly ChangeEntry[] Changelog =
        {
            new ChangeEntry
            {
                Version = "1.0.6",
                Title = "Configurações, seus gostos e correções",
                Items = new[]
                {
                    "Novo: \"Baixar em lote a cada hora\" na fila inteligente: de hora em hora baixa de uma vez 10, 25, 50 ou 100 músicas novas pelo seu gosto, sem mexer na fila (Configurações → Fila inteligente). O limite de músicas automáticas agora também pode ser de 500 arquivos",
                    "Resumo de uso redesenhado: números em cartões (músicas, ouvidas, reproduções, tempo, favoritas e artistas), ranking com barras e contagem sempre visível, gráfico de horários em toda a largura com o pico destacado, e a janela rola em telas baixas",
                    "Correção no resumo de uso: o artista não aparece mais duplicado no nome da música (ex.: \"Dj Kantik – Dj Kantik - …\") e artistas com espaço sobrando no nome não são contados separados; o nome completo aparece ao passar o mouse",
                    "Correção: o título de músicas com nome longo em \"Tocando agora\" agora quebra em até duas linhas (nome completo ao passar o mouse) em vez de ficar cortado",
                    "Mais leve no PC: o download automático da fila inteligente roda com prioridade mínima, a cada 5 minutos (10 em PC fraco), e não baixa com vídeo tocando nem com o notebook na bateria",
                    "Mais leve no PC: as animações de fundo (luzes e ondas) param quando você troca para outro programa e voltam ao clicar no app; o visualizador de \"Tocando agora\" também gasta menos fora de foco",
                    "Mais leve no PC: a abertura usa prioridade um pouco acima do normal (antes era alta) e não corta mais a memória logo depois de abrir; a atualização da biblioteca consulta o disco uma vez por arquivo (eram duas), roda com prioridade baixa e, em PC de 2 núcleos, usa só 1 para ler as informações das músicas",
                    "Correção: a tela travava por um instante a cada música baixada pela fila inteligente em bibliotecas grandes; agora a conferência é feita em segundo plano",
                    "Configuração inicial em janela, no estilo do Windows, na primeira abertura e ao atualizar: estilos, artistas, idioma e quanto você quer descobrir (refaça em Configurações → Fila inteligente → Seus gostos)",
                    "A fila inteligente usa o que você marcou desde o primeiro dia e, sem histórico, as músicas novas do YouTube partem dos seus artistas e estilos",
                    "Correção: a música parava quando a próxima da fila era um arquivo apagado; agora o app avisa, tira da biblioteca e segue para a próxima",
                    "Correção: ao pular ou terminar uma música cuja próxima da biblioteca era um arquivo apagado, o app agora segue mesmo sem fila (antes parava); clicar direto num arquivo apagado só avisa e não interrompe o que está tocando",
                    "Correção: as músicas do lote de hora em hora podiam entrar na fila sozinhas pelo vigia de pastas; agora o lote realmente não mexe na fila",
                    "Correção: se a conexão caía no download do yt-dlp ou do ffmpeg, a ferramenta ficava quebrada para sempre; agora o app detecta e baixa de novo",
                    "Atualização automática mais segura: só instala sozinha quando o GitHub informa o código de verificação (hash) do arquivo, e o download tem limite de 20 minutos (antes uma conexão parada travava as próximas buscas)",
                    "Correção: a prioridade extra da abertura agora volta ao normal também em PC lento, onde a abertura passa de 20 s",
                    "Correção: as frases novas das traduções (inglês e espanhol) não apareciam para quem já tinha baixado idiomas antes",
                    "O assistente de gostos não aparece mais por cima de um arquivo aberto com \"Abrir com\"",
                    "Configurações reorganizadas em abas: Aparência, Reprodução, Fila inteligente, Biblioteca, Sistema e Dados",
                    "Ferramentas e opções de dados agora são linhas de cartão, com título, descrição e botão",
                    "Janelas de confirmação e de erro no visual do app (limpar músicas automáticas, falha do VLC), no lugar das caixas do Windows",
                    "Tela Sobre redesenhada: cartão com versão do app, ações de atualização em linhas e lista de componentes mais limpa",
                    "Instalador mais leve: o VLC agora vai sem os componentes que o player não usa (interface, Lua, transcodificação)"
                }
            },
            new ChangeEntry
            {
                Version = "1.0.5",
                Title = "Letras, favoritos e mini player",
                Items = new[]
                {
                    "Tela de boas-vindas e de novidades (esta janela): aparece na primeira vez e depois de cada atualização. Dá para rever em Sobre.",
                    "O app abre mais rápido: reforço de processador e memória só durante a abertura e Modo leve automático em PC fraco",
                    "Cor de destaque corrigida: botões, barras, menus e campos agora mudam juntos",
                    "Menu de copiar e colar da busca e dos campos de texto refeito, no visual do app",
                    "Letras sincronizadas: coloque um arquivo .lrc com o mesmo nome da música e a letra aparece em Tocando agora",
                    "Favoritos ♥ (botão na barra, tecla H ou Ctrl+D) com a playlist automática ♥ Favoritas",
                    "Mini player sempre no topo (Ctrl+M)",
                    "Transição suave entre músicas (fade de 2, 4 ou 6 s)",
                    "Cor de destaque: azul, roxo, verde, rosa ou laranja",
                    "Achar músicas duplicadas e importar playlists .m3u / .m3u8",
                    "Meu resumo de uso: mais ouvidas, artistas e horários",
                    "Fila inteligente: mostra por que escolheu cada música, deixa bloquear faixa ou artista e ficou mais rápida",
                    "Limite configurável e limpeza das músicas automáticas",
                    "Novos atalhos: 0–9, Shift+setas, Ctrl+Q, Ctrl+L, Ctrl+M",
                    "O aviso de atualização agora mostra as novidades da versão"
                }
            }
        };

        bool firstEverRun;   // definido no construtor: ainda não existia o settings.json (app nunca foi aberto neste PC)

        async Task ShowWelcomeIfNeededAsync()
        {
            try
            {
                string current = UpdCurrent().ToString(3);
                string last = prefs.LastSeenVersion ?? string.Empty;
                if (string.Equals(last, current, StringComparison.Ordinal)) { if (launchPaths.Length == 0) await ShowTasteIfNeededAsync(); return; }   // versão já apresentada: só falta o assistente de gostos, se for o caso
                await Task.Delay(StartupBoost.IsLowEnd ? 1800 : 900);                 // deixa a janela principal assentar primeiro
                if (appClosing || !IsVisible || WindowState == WindowState.Minimized) return;   // na bandeja: mostra na próxima abertura
                if (launchPaths.Length > 0 && !firstEverRun) return;                  // aberto por "Abrir com": não atrapalha
                bool first = last.Length == 0 && firstEverRun;
                if (first) await ShowTasteIfNeededAsync();   // primeira vez: a configuração inicial vem antes dos primeiros passos
                ShowWhatsNew(first, last, false);
                prefs.LastSeenVersion = current;   // só marca como vista depois que a janela foi mostrada e fechada
                prefs.Save();
                if (!first) await ShowTasteIfNeededAsync();   // depois de atualizar: novidades e, se houver pergunta nova, a configuração
            }
            catch (Exception ex) { App.LogError("Boas-vindas", ex); }
        }

        void PrefWhatsNew_Click(object s, RoutedEventArgs e)
        {
            try { ShowWhatsNew(false, string.Empty, true); }
            catch (Exception ex) { App.LogError("Novidades", ex); }
        }

        TextBlock WelcomeSection(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 8) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Cyan");
            return t;
        }

        UIElement WelcomeTip(string glyph, string title, string text)
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
            icon.SetResourceReference(Border.BackgroundProperty, "Az33");
            var g = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            g.SetResourceReference(TextBlock.ForegroundProperty, "Cyan");
            icon.Child = g;
            var texts = new StackPanel();
            texts.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Foam") });
            texts.Children.Add(new TextBlock { Text = text, FontSize = 12.5, LineHeight = 18, Foreground = (Brush)FindResource("Mute"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(texts, 1);
            row.Children.Add(icon);
            row.Children.Add(texts);
            return row;
        }

        UIElement WelcomeBullet(string text)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 7, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Cyan");
            var t = new TextBlock { Text = text, FontSize = 13, LineHeight = 20, Foreground = (Brush)FindResource("Foam"), TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(t, 1);
            row.Children.Add(dot);
            row.Children.Add(t);
            return row;
        }

        // first = primeira vez no PC (boas-vindas + primeiros passos); all = histórico completo (botão em Sobre)
        void ShowWhatsNew(bool first, string fromVersion, bool all)
        {
            string current = UpdCurrent().ToString(3);
            Version previous = null;
            if (!Version.TryParse(fromVersion, out previous)) previous = null;

            // quais versões mostrar: só as que o usuário ainda não viu (ou a atual, na primeira vez)
            var entries = Changelog.Where(c =>
            {
                if (all) return true;
                if (previous == null) return c.Version == current;
                return Version.TryParse(c.Version, out Version v) && v > previous;
            }).Take(all ? 20 : 3).ToList();
            if (entries.Count == 0) entries = Changelog.Take(1).ToList();

            var body = new StackPanel { Margin = new Thickness(30, 10, 30, 24) };

            // ---- cabeçalho ----
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var badge = new Border
            {
                Width = 60, Height = 60, CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 18, 0),
                Child = new TextBlock { Text = first ? "\uE8D6" : "\uE946", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 26, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            badge.SetResourceReference(Border.BackgroundProperty, "Grad");
            head.Children.Add(badge);

            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            string title = first ? "Bem-vindo ao Azul Groove Music" : all ? "Novidades do Azul Groove" : "Novidades da versão " + current;
            titles.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            string subtitle = first ? "Versão " + current + " · seu player de música e vídeo com fila inteligente"
                : (!all && !string.IsNullOrEmpty(fromVersion) ? "Você atualizou da " + fromVersion + " para a " + current : "Você está na versão " + current);
            titles.Children.Add(new TextBlock { Text = subtitle, FontSize = 13.5, Margin = new Thickness(0, 4, 0, 0), Foreground = (Brush)FindResource("Mute"), TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(titles, 1);
            head.Children.Add(titles);
            body.Children.Add(head);

            // ---- primeiros passos (só na primeira vez) ----
            if (first)
            {
                body.Children.Add(WelcomeSection("Primeiros passos"));
                body.Children.Add(WelcomeTip("\uE8D6", "Adicione suas músicas", "Clique em “Abrir arquivo(s)” ou arraste arquivos e pastas para dentro da janela."));
                body.Children.Add(WelcomeTip("\uE721", "Pesquise rápido", "Ctrl+F vai direto para a busca. Em Configurações dá para buscar também no YouTube."));
                body.Children.Add(WelcomeTip("\uE8FD", "Fila inteligente", "O app aprende o que você ouve e completa a fila sozinho."));
                body.Children.Add(WelcomeTip("\uE713", "Do seu jeito", "Em Configurações: cor de destaque, Modo leve para PC fraco e atualizações automáticas."));
            }

            // ---- novidades ----
            body.Children.Add(WelcomeSection(all ? "Histórico de novidades" : "Novidades"));
            var list = new StackPanel();
            foreach (ChangeEntry entry in entries)
            {
                var versionLine = new TextBlock
                {
                    Text = "Versão " + entry.Version + (string.IsNullOrEmpty(entry.Title) ? string.Empty : " · " + entry.Title),
                    FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Foam"),
                    Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 16, 0, 4)
                };
                list.Children.Add(versionLine);
                foreach (string item in entry.Items) list.Children.Add(WelcomeBullet(item));
            }
            body.Children.Add(new ScrollViewer
            {
                MaxHeight = first ? 190 : 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 10, 0), Content = list
            });

            // ---- botão ----
            Button go = BrandedButton(first ? "Começar" : "Entendi", true, false);
            go.IsDefault = true; go.IsCancel = true;
            go.HorizontalAlignment = HorizontalAlignment.Right; go.Margin = new Thickness(0, 24, 0, 0);
            body.Children.Add(go);

            Button closeX;
            Window win = NewBrandedWindow(540, body, out closeX);
            win.Title = T.Get(first ? "Bem-vindo" : "Novidades") + " · Azul Groove Music";
            go.Click += (a, b) => win.Close();
            win.ShowDialog();
        }
    }
}
