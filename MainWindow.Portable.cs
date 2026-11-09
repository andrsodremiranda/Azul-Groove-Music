using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace AzulGroove
{
    // Versão portátil (EXE único): o VLC vai embutido no próprio EXE como "libvlc.zip" e é aberto na primeira
    // execução em %LocalAppData%\AzulGroove\vlc\<versão>. Na versão instalada o VLC fica na pasta libvlc ao lado do EXE.
    public partial class MainWindow
    {
        static string ResolveLibVlcDir()
        {
            string loose = System.IO.Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
            if (Directory.Exists(loose)) return loose;   // instalada / pasta publish

            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream res = asm.GetManifestResourceStream("libvlc.zip"))
            {
                if (res == null) return null;   // sem VLC embutido: deixa o LibVLCSharp procurar sozinho

                string ver = (asm.GetName().Version ?? new Version(1, 0, 0)).ToString(3);
                string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzulGroove", "vlc");
                string dir = System.IO.Path.Combine(root, ver);
                if (File.Exists(System.IO.Path.Combine(dir, ".ok"))) return dir;   // já preparado numa execução anterior

                string tmp = dir + ".tmp";
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(root);
                using (var zip = new ZipArchive(res, ZipArchiveMode.Read)) zip.ExtractToDirectory(tmp);
                File.WriteAllText(System.IO.Path.Combine(tmp, ".ok"), ver);
                Directory.Move(tmp, dir);

                try   // apaga a cópia de versões antigas
                {
                    foreach (string old in Directory.GetDirectories(root))
                        if (!string.Equals(System.IO.Path.GetFileName(old), ver, StringComparison.OrdinalIgnoreCase)) Directory.Delete(old, true);
                }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Could not clean old VLC copies: " + ex.Message); }
                return dir;
            }
        }
    }
}
