using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace AzulGroove
{
    // Preferências do app (separadas do estado do player). Ficam em %AppData%\AzulGroove\prefs.json
    sealed class AppPrefs
    {
        public bool Animations { get; set; } = true;       // luzes de fundo e ondas do Início
        public bool MinimizeToTray { get; set; } = true;   // minimizar esconde a janela na bandeja
        public bool ResumePlayback { get; set; } = true;   // voltar à última faixa ao abrir
        public bool LightMode { get; set; } = false;       // modo leve: sem animações de fundo e visualizador mais simples
        public bool GlobalMediaKeys { get; set; } = true;  // teclas de mídia do teclado funcionam mesmo com o app em segundo plano
        public bool LevelVolume { get; set; } = false;     // nivela o volume entre músicas (filtro do VLC; vale após reiniciar)
        public bool YtSearch { get; set; } = false;        // mostrar resultados do YouTube ao buscar (opcional: usa a internet)
        public bool SmartQueue { get; set; } = true;       // fila inteligente: a cada troca de música a fila se completa sozinha pelo seu gosto
        public int SmartAhead { get; set; } = 2;           // quantas músicas à frente a fila inteligente mantém
        public bool SmartDownload { get; set; } = true;    // a fila inteligente também baixa músicas novas do YouTube pelo seu gosto (usa a internet)
        public bool AutoQueueNew { get; set; } = true;     // músicas novas (download do YouTube / pasta da biblioteca) entram sozinhas na fila

        static readonly string PrefsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove", "prefs.json");

        public static AppPrefs Load()
        {
            try { if (File.Exists(PrefsPath)) return JsonSerializer.Deserialize<AppPrefs>(File.ReadAllText(PrefsPath)) ?? new AppPrefs(); }
            catch (Exception ex) { Trace.WriteLine("Could not load prefs: " + ex.Message); }
            return new AppPrefs();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath));
                string temp = PrefsPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this));
                File.Move(temp, PrefsPath, true);
            }
            catch (Exception ex) { Trace.WriteLine("Could not save prefs: " + ex.Message); }
        }
    }

    public partial class MainWindow
    {
        readonly AppPrefs prefs = AppPrefs.Load();
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "AzulGrooveMusic";

        static bool GetAutoStart()
        {
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath)) return k?.GetValue(RunValueName) != null; }
            catch (Exception ex) { Trace.WriteLine("Could not read autostart: " + ex.Message); return false; }
        }

        static bool SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (k == null) return false;
                    if (on)
                    {
                        string exe = Environment.ProcessPath;
                        if (string.IsNullOrEmpty(exe)) return false;
                        k.SetValue(RunValueName, "\"" + exe + "\"");
                    }
                    else k.DeleteValue(RunValueName, false);
                }
                return true;
            }
            catch (Exception ex) { Trace.WriteLine("Could not change autostart: " + ex.Message); return false; }
        }

        // Ícone do app na barra de título: usa o maior tamanho do icon.ico (o padrão do WPF pegaria o de 16 px e ficaria borrado)
        void LoadTitleIcon()
        {
            try
            {
                var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/icon.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                BitmapFrame best = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
                if (best != null) { TitleIcon.Source = best; Icon = best; }   // barra de título + janela/barra de tarefas
            }
            catch (Exception ex)
            {
                App.LogError("Ícone do app", ex);
                try { TitleIcon.Source = new BitmapImage(new Uri("pack://application:,,,/icon.ico", UriKind.Absolute)); }
                catch (Exception ex2) { App.LogError("Ícone do app (alternativo)", ex2); }
            }
        }

        void RefreshSettingsView()
        {
            if (PrefAnim == null) return;
            PrefLight.IsChecked = prefs.LightMode;
            PrefMediaKeys.IsChecked = prefs.GlobalMediaKeys;
            PrefLevel.IsChecked = prefs.LevelVolume;
            PrefAnim.IsChecked = prefs.Animations;
            PrefResume.IsChecked = prefs.ResumePlayback;
            PrefAutoQueue.IsChecked = prefs.AutoQueueNew;
            PrefSmart.IsChecked = prefs.SmartQueue;
            PrefSmartDl.IsChecked = prefs.SmartDownload;
            PrefSmartAheadBtn.Content = "Músicas à frente: " + prefs.SmartAhead;
            PrefTray.IsChecked = prefs.MinimizeToTray;
            PrefYtSearch.IsChecked = prefs.YtSearch;
            PrefYtFolderTxt.Text = "Downloads do YouTube: " + ytFolder;
            PrefStartup.IsChecked = GetAutoStart();
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            AboutTxt.Text = "Azul Groove Music · versão " + (v != null ? v.ToString(3) : "?");
        }

        // Liga/desliga as animações de fundo na hora, respeitando a página e o estado da janela
        void ApplyAnimPrefs()
        {
            bool vis = WindowState != WindowState.Minimized;
            Drift(vis && page != 4);
            Fx(vis && page == 0);
        }

        void PrefAnim_Click(object s, RoutedEventArgs e)
        {
            prefs.Animations = ((CheckBox)s).IsChecked == true;
            prefs.Save();
            ApplyAnimPrefs();
        }

        // Animações de fundo só rodam se estiverem ligadas e o modo leve estiver desligado
        bool AnimationsAllowed => prefs.Animations && !prefs.LightMode;

        void PrefLight_Click(object s, RoutedEventArgs e)
        {
            prefs.LightMode = ((CheckBox)s).IsChecked == true;
            prefs.Save();
            ApplyAnimPrefs();
            Toast(prefs.LightMode ? "Modo leve ligado" : "Modo leve desligado");
        }

        void PrefResume_Click(object s, RoutedEventArgs e)
        {
            prefs.ResumePlayback = ((CheckBox)s).IsChecked == true;
            prefs.Save();
        }

        void PrefAutoQueue_Click(object s, RoutedEventArgs e)
        {
            prefs.AutoQueueNew = ((CheckBox)s).IsChecked == true;
            prefs.Save();
        }

        void PrefTray_Click(object s, RoutedEventArgs e)
        {
            prefs.MinimizeToTray = ((CheckBox)s).IsChecked == true;
            prefs.Save();
        }

        // ---------- Teclas de mídia globais (play/pausa, próxima, anterior, parar) ----------
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        static readonly int[] MediaHotkeyIds = { 9101, 9102, 9103, 9104 };
        static readonly uint[] MediaHotkeyKeys = { 0xB3, 0xB0, 0xB1, 0xB2 };   // play/pausa, próxima, anterior, parar
        bool mediaKeysOn;

        void ApplyMediaKeys()
        {
            if (prefs.GlobalMediaKeys && !mediaKeysOn)
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;
                // se outro app já registrou a tecla, o Windows recusa e ela simplesmente continua com o outro app
                for (int i = 0; i < MediaHotkeyIds.Length; i++) RegisterHotKey(h, MediaHotkeyIds[i], 0, MediaHotkeyKeys[i]);
                mediaKeysOn = true;
            }
            else if (!prefs.GlobalMediaKeys) UnregisterMediaKeys();
        }

        void UnregisterMediaKeys()
        {
            if (!mediaKeysOn) return;
            IntPtr h = new WindowInteropHelper(this).Handle;
            for (int i = 0; i < MediaHotkeyIds.Length; i++) UnregisterHotKey(h, MediaHotkeyIds[i]);
            mediaKeysOn = false;
        }

        bool HandleMediaHotkey(int id)
        {
            switch (id)
            {
                case 9101: Play_Click(null, null); return true;
                case 9102: Step(1); return true;
                case 9103: Prev_Click(null, null); return true;
                case 9104: _mp?.Stop(); return true;
            }
            return false;
        }

        void PrefMediaKeys_Click(object s, RoutedEventArgs e)
        {
            prefs.GlobalMediaKeys = ((CheckBox)s).IsChecked == true;
            prefs.Save();
            ApplyMediaKeys();
        }

        void PrefLevel_Click(object s, RoutedEventArgs e)
        {
            prefs.LevelVolume = ((CheckBox)s).IsChecked == true;
            prefs.Save();
            Toast("Reinicie o app para aplicar");
        }

        // ---------- Temporizador para dormir (menu "Mais" do player) ----------
        static readonly int[] SleepSteps = { 0, 15, 30, 45, 60, 90 };
        DispatcherTimer sleepTimer;
        DateTime sleepEnd;
        int sleepIdx;

        void Sleep_Click(object s, RoutedEventArgs e)
        {
            MorePop.IsOpen = false;
            sleepIdx = (sleepIdx + 1) % SleepSteps.Length;
            int min = SleepSteps[sleepIdx];
            sleepTimer?.Stop();
            if (min == 0) { SleepTxt.Text = "Temporizador: desligado"; Toast("Temporizador desligado"); return; }
            sleepEnd = DateTime.UtcNow.AddMinutes(min);
            if (sleepTimer == null)
            {
                sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                sleepTimer.Tick += SleepTick;
            }
            sleepTimer.Start();
            SleepTxt.Text = "Temporizador: " + min + " min";
            Toast("Pausa automática em " + min + " min");
        }

        void SleepTick(object s, EventArgs e)
        {
            TimeSpan left = sleepEnd - DateTime.UtcNow;
            if (left > TimeSpan.Zero) { SleepTxt.Text = "Temporizador: " + (int)Math.Ceiling(left.TotalMinutes) + " min"; return; }
            sleepTimer.Stop();
            sleepIdx = 0;
            SleepTxt.Text = "Temporizador: desligado";
            if (_mp != null && _mp.IsPlaying) _mp.Pause();
            Toast("Temporizador: música pausada");
        }

        void PrefYtSearch_Click(object s, RoutedEventArgs e)
        {
            prefs.YtSearch = ((CheckBox)s).IsChecked == true;
            prefs.Save();
            YtSearchSchedule();
        }

        void PrefYtFolder_Click(object s, RoutedEventArgs e)
        {
            YtFolder_Click(s, e);
            PrefYtFolderTxt.Text = "Downloads do YouTube: " + ytFolder;
        }

        void PrefStartup_Click(object s, RoutedEventArgs e)
        {
            var cb = (CheckBox)s;
            bool want = cb.IsChecked == true;
            if (!SetAutoStart(want)) { cb.IsChecked = GetAutoStart(); Toast("Não foi possível alterar a inicialização com o Windows"); }
        }

        void PrefDataFolder_Click(object s, RoutedEventArgs e)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove");
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Trace.WriteLine("Could not open data folder: " + ex.Message); }
        }

        void PrefReset_Click(object s, RoutedEventArgs e)
        {
            prefs.Animations = true; prefs.MinimizeToTray = true; prefs.ResumePlayback = true; prefs.YtSearch = false; prefs.GlobalMediaKeys = true; prefs.LevelVolume = false; prefs.LightMode = false; prefs.AutoQueueNew = true; prefs.SmartQueue = true; prefs.SmartAhead = 2; prefs.SmartDownload = true;
            prefs.Save();
            SetAutoStart(false);
            RefreshSettingsView();
            ApplyAnimPrefs();
            YtSearchSchedule();
            ApplyMediaKeys();
            Toast("Configurações restauradas");
        }
    }
}
