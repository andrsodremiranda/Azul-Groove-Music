using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AzulGroove
{
    // "Boost" só durante a abertura: sobe a prioridade do processo, tira o app do modo econômico do Windows
    // e reserva memória para o .NET não parar para limpar lixo enquanto a janela carrega.
    // Quando a abertura termina (ou depois de no máximo 20 s), tudo volta ao normal e a memória sobrando é devolvida ao sistema.
    static class StartupBoost
    {
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);
        [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState info, int size);

        [StructLayout(LayoutKind.Sequential)]
        struct PowerThrottlingState { public uint Version; public uint ControlMask; public uint StateMask; }

        const int ProcessPowerThrottling = 4;
        const uint ThrottlingExecutionSpeed = 0x1;

        static int started, ended;
        static Thread uiThread;   // a thread da tela, para devolver a prioridade dela mesmo quando End() roda em outra thread

        // PC fraco: pouca memória (até ~4 GB), 2 núcleos ou menos, ou placa de vídeo sem aceleração
        public static readonly bool IsLowEnd = DetectLowEnd();

        static bool DetectLowEnd()
        {
            try
            {
                long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                if (ram > 0 && ram <= 4500L * 1024 * 1024) return true;
                if (Environment.ProcessorCount <= 2) return true;
                if ((RenderCapability.Tier >> 16) == 0) return true;
            }
            catch (Exception ex) { Trace.WriteLine("Could not detect hardware: " + ex.Message); }
            return false;
        }

        // Em PC fraco, limita TODAS as animações a 30 quadros por segundo (menos CPU/GPU, mesma fluidez para o olho)
        // Precisa rodar antes de qualquer animação existir.
        public static void ApplyRenderTuning()
        {
            if (!IsLowEnd) return;
            try
            {
                Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata(30));
            }
            catch (Exception ex) { Trace.WriteLine("Could not cap frame rate: " + ex.Message); }
        }

        // Primeira abertura em PC fraco: já liga o modo leve (o usuário pode desligar depois em Configurações)
        public static void TuneFirstRun(AppPrefs prefs)
        {
            try
            {
                if (prefs.AutoTuned) return;
                prefs.AutoTuned = true;
                if (IsLowEnd) prefs.LightMode = true;
                prefs.Save();
            }
            catch (Exception ex) { Trace.WriteLine("Could not tune first run: " + ex.Message); }
        }

        public static void Begin()
        {
            if (Interlocked.Exchange(ref started, 1) != 0) return;
            try { using (var p = Process.GetCurrentProcess()) p.PriorityClass = ProcessPriorityClass.AboveNormal; }
            catch (Exception ex) { Trace.WriteLine("Could not raise priority: " + ex.Message); }
            try { uiThread = Thread.CurrentThread; Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; }
            catch (Exception ex) { Trace.WriteLine("Could not raise UI thread priority: " + ex.Message); }
            SetPowerThrottling(false);
            try { GC.TryStartNoGCRegion(IsLowEnd ? 32L * 1024 * 1024 : 48L * 1024 * 1024); }   // memória reservada: sem pausas de coleta durante a abertura
            catch (Exception ex) { Trace.WriteLine("Could not reserve startup memory: " + ex.Message); }
            Task.Delay(20000).ContinueWith(_ => End());   // trava de segurança: o boost nunca fica ligado para sempre
        }

        // Chamar quando a abertura terminou
        public static void End()
        {
            if (Interlocked.Exchange(ref ended, 1) != 0) return;
            try { if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) GC.EndNoGCRegion(); }
            catch (InvalidOperationException) { /* a reserva já tinha acabado (usou mais que o previsto): normal */ }
            catch (Exception ex) { Trace.WriteLine("Could not end no-GC region: " + ex.Message); }
            try { using (var p = Process.GetCurrentProcess()) p.PriorityClass = ProcessPriorityClass.Normal; }
            catch (Exception ex) { Trace.WriteLine("Could not restore priority: " + ex.Message); }
            try { (uiThread ?? Thread.CurrentThread).Priority = ThreadPriority.Normal; }
            catch (Exception ex) { Trace.WriteLine("Could not restore thread priority: " + ex.Message); }
            SetPowerThrottling(true);
            // Depois que a tela assentou, devolve ao Windows a memória que não está em uso
            Task.Delay(2500).ContinueWith(_ => TrimMemory(false));   // só o coletor: cortar o working set aqui forçaria o Windows a reler a memória do disco logo depois
        }

        // Também útil quando o app vai para a bandeja: libera memória de verdade
        public static void TrimMemory(bool trimWorkingSet = true)
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                if (trimWorkingSet)
                    using (var p = Process.GetCurrentProcess())
                        SetProcessWorkingSetSize(p.Handle, new IntPtr(-1), new IntPtr(-1));
            }
            catch (Exception ex) { Trace.WriteLine("Could not trim memory: " + ex.Message); }
        }

        // enable = true devolve o comportamento padrão do Windows; false impede o modo econômico (EcoQoS) durante a abertura
        static void SetPowerThrottling(bool enable)
        {
            try
            {
                var state = new PowerThrottlingState { Version = 1, ControlMask = enable ? 0u : ThrottlingExecutionSpeed, StateMask = 0 };
                using (var p = Process.GetCurrentProcess())
                    SetProcessInformation(p.Handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
            }
            catch (Exception ex) { Trace.WriteLine("Could not change power throttling: " + ex.Message); }
        }
    }
}
