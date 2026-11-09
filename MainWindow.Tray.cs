using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SD = System.Drawing;
using SD2 = System.Drawing.Drawing2D;
using WF = System.Windows.Forms;

namespace AzulGroove
{
    public partial class MainWindow
    {
        // ---------- Janela de vídeo sob demanda ----------
        // Antes ela era criada na abertura, mesmo que você só ouvisse música. Agora nasce no primeiro vídeo.
        void EnsureVideoPanel()
        {
            if (vpanel != null) return;
            vpanel = new WF.Panel { BackColor = SD.Color.Black, Dock = WF.DockStyle.Fill };
            vpanel.HandleCreated += (a, b) => { if (_mp != null) _mp.Hwnd = vpanel.Handle; };
            var clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            clickTimer.Tick += (a, b) => { clickTimer.Stop(); if (cur != null && _mp != null) { if (_mp.IsPlaying) _mp.Pause(); else _mp.Play(); } };
            vpanel.Click += (a, b) => { clickTimer.Stop(); clickTimer.Start(); };               // 1 clique: pausa/retoma
            vpanel.MouseDoubleClick += (a, b) => { clickTimer.Stop(); ToggleFull(); };          // 2 cliques: tela cheia
            vpanel.MouseMove += (a, b) => { if (!full) return; long t = Environment.TickCount64; if (t - lastVpMove < 50) return; lastVpMove = t; Dispatcher.BeginInvoke(new Action(Moved)); };
            vpanel.MouseWheel += (a, b) => Dispatcher.BeginInvoke(new Action(() => { VolSlider.Value = Math.Max(0, Math.Min(100, VolSlider.Value + (b.Delta > 0 ? 5 : -5))); Toast("Volume " + (int)VolSlider.Value + "%"); }));
            vpanel.MouseDown += (a, b) => Dispatcher.BeginInvoke(new Action(() => { ClosePops(); Keyboard.Focus(this); }));
            _ = vpanel.Handle; // força o handle já: o VLC nunca abre janela própria, mesmo com a janela na bandeja
            videoHost = new WindowsFormsHost { Child = vpanel, Background = Brushes.Black };
            VideoHost.Children.Add(videoHost);
        }

        // ---------- Bandeja ----------
        static readonly SD.Color TrayTop = SD.Color.FromArgb(0x0B, 0x1B, 0x3F);
        static readonly SD.Color TrayBottom = SD.Color.FromArgb(0x05, 0x0E, 0x24);
        static readonly SD.Color TrayFoam = SD.Color.FromArgb(0xE4, 0xF1, 0xFF);
        static readonly SD.Color TrayMute = SD.Color.FromArgb(0xAF, 0xC6, 0xE5);
        static readonly SD.Color TrayCyan = SD.Color.FromArgb(0x4F, 0xD1, 0xFF);
        static readonly SD.Color TrayAzure = SD.Color.FromArgb(0x2F, 0x8C, 0xFF);
        static readonly Dictionary<string, SD.Bitmap> glyphCache = new Dictionary<string, SD.Bitmap>();

        WF.ToolStripLabel trayTitle, trayArtist;
        WF.ToolStripMenuItem trayPlay, trayShuf, trayRep, trayMute;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Ícones do próprio Segoe MDL2 (o mesmo do app), desenhados uma vez e guardados
        static SD.Bitmap GlyphImg(string glyph, SD.Color color)
        {
            string key = glyph + color.ToArgb();
            if (glyphCache.TryGetValue(key, out SD.Bitmap cached)) return cached;
            var bmp = new SD.Bitmap(18, 18);
            try
            {
                using (var g = SD.Graphics.FromImage(bmp))
                using (var font = new SD.Font("Segoe MDL2 Assets", 14f, SD.GraphicsUnit.Pixel))
                using (var brush = new SD.SolidBrush(color))
                using (var fmt = new SD.StringFormat { Alignment = SD.StringAlignment.Center, LineAlignment = SD.StringAlignment.Center })
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.DrawString(glyph, font, brush, new SD.RectangleF(0, 0, 18, 18), fmt);
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not draw tray glyph: " + ex.Message); }
            glyphCache[key] = bmp;
            return bmp;
        }

        static SD.Icon LoadTrayIcon()
        {
            try
            {
                var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/icon.ico"));
                if (info != null)
                    using (var stream = info.Stream)
                        return new SD.Icon(stream, WF.SystemInformation.SmallIconSize);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not load tray icon resource: " + ex.Message); }
            try { return SD.Icon.ExtractAssociatedIcon(Environment.ProcessPath); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not extract tray icon: " + ex.Message); }
            return SD.SystemIcons.Application;
        }

        WF.ToolStripMenuItem TrayItem(string text, string glyph, Action action)
        {
            var item = new WF.ToolStripMenuItem(text) { Image = GlyphImg(glyph, TrayCyan), ForeColor = TrayFoam, Padding = new WF.Padding(0, 5, 0, 5) };
            item.Click += (a, b) => action();
            return item;
        }

        // Só cria a bandeja na primeira vez que a janela vai para ela (antes era criada sempre na abertura)
        void EnsureTray()
        {
            if (trayIcon != null) return;
            trayIcon = new WF.NotifyIcon { Text = "Azul Groove Music", Icon = LoadTrayIcon(), Visible = false };

            var menu = new WF.ContextMenuStrip
            {
                Renderer = new TrayRenderer(),
                BackColor = TrayBottom,
                ForeColor = TrayFoam,
                Font = new SD.Font("Segoe UI", 9.5f),
                ImageScalingSize = new SD.Size(18, 18),
                ShowCheckMargin = false,
                ShowImageMargin = true,
                MinimumSize = new SD.Size(250, 0),
                Padding = new WF.Padding(0, 6, 0, 6)
            };

            trayTitle = new WF.ToolStripLabel("Nenhuma faixa") { Image = GlyphImg("\uE8D6", TrayCyan), ForeColor = TrayFoam, Font = new SD.Font("Segoe UI Semibold", 10f), Padding = new WF.Padding(0, 4, 0, 0) };
            trayArtist = new WF.ToolStripLabel("Azul Groove Music") { ForeColor = TrayMute, Font = new SD.Font("Segoe UI", 8.5f), Padding = new WF.Padding(0, 0, 0, 4) };
            trayPlay = TrayItem("Reproduzir", "\uE768", () => Play_Click(null, null));
            trayShuf = TrayItem("Aleatório", "\uE8B1", () => Shuf_Click(null, null));
            trayRep = TrayItem("Repetir", "\uE8EE", () => Rep_Click(null, null));
            trayMute = TrayItem("Silenciar", "\uE74F", () => Mute_Click(null, null));

            menu.Items.Add(trayTitle);
            menu.Items.Add(trayArtist);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(TrayItem("Abrir Azul Groove", "\uE8A7", RestoreFromTray));
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(TrayItem("Faixa anterior", "\uE892", () => Prev_Click(null, null)));
            menu.Items.Add(trayPlay);
            menu.Items.Add(TrayItem("Próxima faixa", "\uE893", () => Step(1)));
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(trayShuf);
            menu.Items.Add(trayRep);
            menu.Items.Add(trayMute);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(TrayItem("Sair", "\uE7E8", () => Close()));

            menu.Opening += (a, b) => RefreshTrayMenu(menu);
            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += (a, b) => RestoreFromTray();
            trayIcon.MouseClick += (a, b) => { if (b.Button == WF.MouseButtons.Left) RestoreFromTray(); };
            UpdateTrayText();
        }

        static string Clip(string text, int max) => text == null ? string.Empty : text.Length <= max ? text : text.Substring(0, max - 1) + "…";

        // Atualiza o menu só quando ele abre (nada roda em segundo plano)
        void RefreshTrayMenu(WF.ContextMenuStrip menu)
        {
            try
            {
                trayTitle.Text = cur != null ? Clip(cur.Name, 32) : "Nenhuma faixa";
                string detail = cur != null ? (string.IsNullOrWhiteSpace(cur.Artist) ? cur.Kind : cur.Artist) : "Azul Groove Music";
                trayArtist.Text = Clip(detail, 40);
                trayPlay.Text = isPlaying ? "Pausar" : "Reproduzir";
                trayPlay.Image = GlyphImg(isPlaying ? "\uE769" : "\uE768", TrayCyan);
                trayShuf.Text = shuffle ? "Aleatório  ·  ligado" : "Aleatório";
                trayShuf.Image = GlyphImg("\uE8B1", shuffle ? TrayCyan : TrayMute);
                trayRep.Text = repeat == 0 ? "Repetir  ·  desligado" : repeat == 1 ? "Repetir  ·  todas" : "Repetir  ·  esta faixa";
                trayRep.Image = GlyphImg(repeat == 2 ? "\uE8ED" : "\uE8EE", repeat > 0 ? TrayCyan : TrayMute);
                trayMute.Text = muted ? "Reativar o som" : "Silenciar";
                trayMute.Image = GlyphImg(muted ? "\uE74F" : "\uE767", muted ? TrayCyan : TrayMute);

                // Windows 11: cantos arredondados e borda azul-escura (no Windows 10 simplesmente é ignorado)
                if (menu.IsHandleCreated || menu.Handle != IntPtr.Zero)
                {
                    int round = 2, border = 0x82461E;
                    DwmSetWindowAttribute(menu.Handle, 33, ref round, 4);
                    DwmSetWindowAttribute(menu.Handle, 34, ref border, 4);
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not refresh tray menu: " + ex.Message); }
        }

        void UpdateTrayText()
        {
            if (trayIcon == null) return;
            try { trayIcon.Text = Clip(cur != null ? cur.Name : "Azul Groove Music", 60); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not update tray text: " + ex.Message); }
        }

        void RestoreFromTray()
        {
            if (trayIcon == null) return;
            trayIcon.Visible = false;
            Show();
            WindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            Activate();
            SyncTimer(); UpdateTimeUi(); UpdateViz();
        }

        // Cantos arredondados, destaque azul no item selecionado e separadores finos
        sealed class TrayRenderer : WF.ToolStripProfessionalRenderer
        {
            public TrayRenderer() : base(new TrayColors()) { RoundedEdges = false; }

            static SD2.GraphicsPath Round(SD.Rectangle r, int radius)
            {
                int d = radius * 2;
                var path = new SD2.GraphicsPath();
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }

            protected override void OnRenderToolStripBackground(WF.ToolStripRenderEventArgs e)
            {
                if (e.AffectedBounds.Width <= 0 || e.AffectedBounds.Height <= 0) return;
                using (var brush = new SD2.LinearGradientBrush(e.AffectedBounds, TrayTop, TrayBottom, 90f))
                    e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(WF.ToolStripRenderEventArgs e)
            {
                using (var pen = new SD.Pen(SD.Color.FromArgb(90, TrayAzure)))
                    e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }

            protected override void OnRenderImageMargin(WF.ToolStripRenderEventArgs e) { }

            protected override void OnRenderMenuItemBackground(WF.ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected) return;
                var g = e.Graphics;
                g.SmoothingMode = SD2.SmoothingMode.AntiAlias;
                var rect = new SD.Rectangle(5, 1, e.Item.Width - 10, e.Item.Height - 2);
                using (var path = Round(rect, 8))
                using (var brush = new SD2.LinearGradientBrush(rect, SD.Color.FromArgb(120, TrayAzure), SD.Color.FromArgb(60, TrayCyan), 0f))
                    g.FillPath(brush, path);
            }

            protected override void OnRenderSeparator(WF.ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using (var pen = new SD.Pen(SD.Color.FromArgb(46, TrayFoam)))
                    e.Graphics.DrawLine(pen, 14, y, e.Item.Width - 14, y);
            }

            protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.ForeColor;
                base.OnRenderItemText(e);
            }
        }

        sealed class TrayColors : WF.ProfessionalColorTable
        {
            public override SD.Color ToolStripDropDownBackground => TrayBottom;
            public override SD.Color ImageMarginGradientBegin => TrayBottom;
            public override SD.Color ImageMarginGradientMiddle => TrayBottom;
            public override SD.Color ImageMarginGradientEnd => TrayBottom;
            public override SD.Color MenuBorder => TrayAzure;
            public override SD.Color MenuItemBorder => SD.Color.Transparent;
            public override SD.Color MenuItemSelected => SD.Color.Transparent;
            public override SD.Color SeparatorDark => TrayMute;
            public override SD.Color SeparatorLight => TrayMute;
        }
    }
}
