using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace AzulGroove
{
    // Peças compartilhadas pelas janelas flutuantes do app (diálogos e popups), para ficarem todas no mesmo visual
    public partial class MainWindow
    {
        // Caixa de pergunta/aviso no visual do app (no lugar do MessageBox do Windows).
        // noText == null: só um botão (aviso). Devolve true se o usuário confirmou.
        bool BrandedAsk(string heading, string message, string yesText, string noText = null, string glyph = "\uE897", bool danger = false)
        {
            var mute = (Brush)FindResource("Mute");
            var body = new StackPanel { Margin = new Thickness(30, 10, 30, 24) };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Brush badge = danger ? (Brush)new BrushConverter().ConvertFromString("#E0555F") : (Brush)FindResource("Grad");
            head.Children.Add(new Border
            {
                Width = 52, Height = 52, CornerRadius = new CornerRadius(16), Background = badge, Margin = new Thickness(0, 0, 16, 0),
                Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 22, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
            var title = new TextBlock { Text = heading, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1); head.Children.Add(title);
            body.Children.Add(head);

            body.Children.Add(new TextBlock { Text = message, FontSize = 13.5, Foreground = mute, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(2, 18, 0, 0) });

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
            Button no = null;
            if (noText != null) { no = BrandedButton(noText, false, false); no.IsCancel = true; no.Margin = new Thickness(0, 0, 10, 0); actions.Children.Add(no); }
            var yes = BrandedButton(yesText, true, false); yes.IsDefault = true;
            if (danger) yes.Background = (Brush)new BrushConverter().ConvertFromString("#E0555F");
            actions.Children.Add(yes);
            body.Children.Add(actions);

            Button closeX;
            Window win = NewBrandedWindow(470, body, out closeX);
            win.Title = "Azul Groove Music";
            bool result = false;
            yes.Click += (a, b) => { result = true; win.Close(); };
            if (no != null) no.Click += (a, b) => win.Close();
            else yes.IsCancel = true;
            win.ShowDialog();
            return result;
        }

        // Botão no estilo do app: primário (degradê), discreto (sem contorno) ou com contorno
        Button BrandedButton(string text, bool primary, bool flat)
        {
            var foam = (Brush)FindResource("Foam");
            var mute = (Brush)FindResource("Mute");
            var grad = (Brush)FindResource("Grad");
            var b = new Button
            {
                Content = text, Cursor = System.Windows.Input.Cursors.Hand, FocusVisualStyle = (Style)FindResource("FocusRing"),
                Foreground = primary ? Brushes.White : (flat ? mute : foam)
            };
            if (primary)
            {
                b.Background = grad; b.Padding = new Thickness(26, 11, 26, 11); b.FontWeight = FontWeights.SemiBold;
                b.Template = ParseButtonTemplate("<Border x:Name='b' CornerRadius='12' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
                    "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.88'/></Trigger>" +
                    "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.4'/></Trigger></ControlTemplate.Triggers>");
            }
            else if (flat)
            {
                b.Padding = new Thickness(12, 9, 12, 9); b.FontSize = 12.5;
                b.Template = ParseButtonTemplate("<Border x:Name='b' CornerRadius='10' Background='Transparent' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
                    "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='#14FFFFFF'/></Trigger></ControlTemplate.Triggers>");
            }
            else { b.Style = (Style)FindResource("PillBtn"); b.Padding = new Thickness(22, 10, 22, 10); }
            return b;
        }

        // Caixa de texto no estilo do app
        TextBox BrandedTextBox(string text)
        {
            var tb = new TextBox
            {
                Text = text ?? string.Empty, FontSize = 14, Padding = new Thickness(12, 9, 12, 9),
                Foreground = (Brush)FindResource("Foam"), CaretBrush = (Brush)FindResource("Cyan"),
                SelectionBrush = (Brush)FindResource("Azure"), VerticalContentAlignment = VerticalAlignment.Center
            };
            tb.Template = ParseTemplate("TextBox",
                "<Border x:Name='b' CornerRadius='10' Background='#1BFFFFFF' BorderBrush='#2678AAFF' BorderThickness='1' Padding='{TemplateBinding Padding}'>" +
                "<ScrollViewer x:Name='PART_ContentHost' Focusable='False' VerticalScrollBarVisibility='Hidden' HorizontalScrollBarVisibility='Hidden'/></Border>" +
                "<ControlTemplate.Triggers><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{DynamicResource Cyan}'/><Setter TargetName='b' Property='Background' Value='#26FFFFFF'/></Trigger></ControlTemplate.Triggers>");
            return tb;
        }

        // Caixa de seleção no estilo do app
        CheckBox BrandedCheckBox(string text)
        {
            var cb = new CheckBox { Content = text, Foreground = (Brush)FindResource("Foam"), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            cb.Template = ParseTemplate("CheckBox",
                "<StackPanel Orientation='Horizontal' Background='Transparent'>" +
                "<Border x:Name='bx' Width='20' Height='20' CornerRadius='6' BorderThickness='1.2' BorderBrush='{DynamicResource Az66}' Background='{DynamicResource Az1A}' VerticalAlignment='Center'>" +
                "<TextBlock x:Name='ck' Text='&#xE73E;' FontFamily='Segoe MDL2 Assets' FontSize='12' Foreground='White' HorizontalAlignment='Center' VerticalAlignment='Center' Visibility='Collapsed'/></Border>" +
                "<ContentPresenter Margin='10,0,0,0' VerticalAlignment='Center'/></StackPanel>" +
                "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bx' Property='BorderBrush' Value='{DynamicResource Cyan}'/></Trigger>" +
                "<Trigger Property='IsChecked' Value='True'><Setter TargetName='bx' Property='Background' Value='{DynamicResource Azure}'/><Setter TargetName='bx' Property='BorderBrush' Value='{DynamicResource Cyan}'/><Setter TargetName='ck' Property='Visibility' Value='Visible'/></Trigger></ControlTemplate.Triggers>");
            return cb;
        }

        TextBlock BrandedLabel(string text) =>
            new TextBlock { Text = text, FontSize = 12.5, Foreground = (Brush)FindResource("Mute"), Margin = new Thickness(2, 12, 0, 5) };

        // Os popups do WPF são janelas "sempre no topo" do Windows: ficavam por cima de qualquer programa (ex.: navegador).
        // Tira o "sempre no topo" do popup; ele continua acima do app, mas some atrás das outras janelas.
        // Em tela cheia o app em si é "sempre no topo", então ali o popup precisa continuar acima dele.
        void PopupToNormalLayer(System.Windows.Controls.Primitives.Popup p)
        {
            try
            {
                if (full || p == null || p.Child == null) return;
                var src = PresentationSource.FromVisual(p.Child) as HwndSource;
                if (src != null) SetWindowPos(src.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not lower popup: " + ex.Message); }
        }

        void Toast_Opened(object s, EventArgs e) => PopupToNormalLayer((System.Windows.Controls.Primitives.Popup)s);
    }
}
