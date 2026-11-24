using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace AzulGroove
{
    // Idiomas do app.
    //
    // O português é o idioma base: os próprios textos do código/XAML são a "chave" da tradução.
    // Cada idioma é um arquivo JSON simples ("texto em português": "tradução") e existe de dois jeitos:
    //   1) embutido no app (pasta idiomas\ do projeto: en.json, es.json), para funcionar offline desde a primeira abertura;
    //   2) baixado do GitHub (Releases, tag "idiomas", arquivo idiomas.zip) para %AppData%\AzulGroove\idiomas.
    // Vale o arquivo de maior "_versao". Dá para corrigir uma tradução ou criar um idioma novo (ex.: fr.json) só
    // atualizando o idiomas.zip no GitHub, sem lançar uma versão nova do app.
    //
    // Chaves com {0}, {1}... casam com textos montados em tempo de execução (ex.: "Volume {0}%").
    // Chaves que começam com "_" são metadados (_nome, _versao).
    static class T
    {
        public const string BaseLang = "pt";
        const string ZipUrl = "https://github.com/andrsodremiranda/Azul-Groove-Music/releases/download/idiomas/idiomas.zip";
        const int MaxZipBytes = 4 * 1024 * 1024;
        const int MaxJsonBytes = 512 * 1024;

        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzulGroove", "idiomas");
        static readonly Regex FileName = new Regex(@"^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?\.json$", RegexOptions.Compiled);

        sealed class Pat { public Regex Rx; public string Tpl; public int Weight; }

        static Dictionary<string, string> exact = new Dictionary<string, string>(StringComparer.Ordinal);
        static List<Pat> pats = new List<Pat>();
        static bool active;    // idioma atual diferente do português
        static bool touched;   // já traduzimos algo alguma vez (precisa voltar ao original se trocar para o português)
        static bool installed;

        public static string Current { get; private set; } = BaseLang;

        // ---------- Tradução de textos ----------
        public static string Get(string s)
        {
            if (!active || string.IsNullOrEmpty(s)) return s;
            if (exact.TryGetValue(s, out var r)) return r;
            if (s.Length > 400 || pats.Count == 0) return s;
            foreach (var p in pats)
            {
                var m = p.Rx.Match(s);
                if (!m.Success) continue;
                return Regex.Replace(p.Tpl, @"\{(\d)\}", x => m.Groups["g" + x.Groups[1].Value].Success ? m.Groups["g" + x.Groups[1].Value].Value : x.Value);
            }
            return s;
        }

        // T.Get("{0} músicas", n): traduz o modelo e preenche os valores
        public static string Get(string fmt, params object[] args)
        {
            try { return string.Format(Get(fmt), args); } catch (FormatException) { return string.Format(fmt, args); }
        }

        // ---------- Escolha do idioma ----------
        public static string Normalize(string code)
        {
            code = (code ?? "").Trim().ToLowerInvariant().Replace('_', '-');
            if (code.StartsWith("pt")) return BaseLang;
            return code;
        }

        public static string AutoLanguage()
        {
            string two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            if (two == "pt") return BaseLang;
            return Available().Contains(two) ? two : "en";
        }

        // Chamado uma vez na abertura do app
        public static void Init(string saved)
        {
            Install();
            SetLanguage(string.IsNullOrWhiteSpace(saved) ? AutoLanguage() : saved);
        }

        public static void SetLanguage(string code)
        {
            code = Normalize(code);
            var map = code == BaseLang ? null : Resolve(code);
            if (map == null) code = BaseLang;
            var ex = new Dictionary<string, string>(StringComparer.Ordinal);
            var pl = new List<Pat>();
            if (map != null)
            {
                foreach (var kv in map)
                {
                    if (kv.Key.StartsWith("_") || string.IsNullOrEmpty(kv.Value)) continue;
                    if (Regex.IsMatch(kv.Key, @"\{\d\}")) { var p = MakePattern(kv.Key, kv.Value); if (p != null) pl.Add(p); ex[kv.Key] = kv.Value; }
                    else ex[kv.Key] = kv.Value;
                }
                pl.Sort((a, b) => b.Weight.CompareTo(a.Weight));
            }
            exact = ex; pats = pl; Current = code; active = code != BaseLang;
        }

        static Pat MakePattern(string key, string tpl)
        {
            try
            {
                var sb = new StringBuilder("^");
                int weight = 0;
                foreach (var part in Regex.Split(key, @"(\{\d\})"))
                {
                    var m = Regex.Match(part, @"^\{(\d)\}$");
                    if (m.Success) sb.Append("(?<g" + m.Groups[1].Value + @">.+?)");
                    else { sb.Append(Regex.Escape(part)); weight += part.Length; }
                }
                sb.Append("$");
                return new Pat { Rx = new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), Tpl = tpl, Weight = weight };
            }
            catch (Exception ex) { AppLog.Write("Idiomas", "Padrão inválido '" + key + "': " + ex.Message); return null; }
        }

        // Códigos disponíveis (pt sempre; depois en, es e os demais em ordem alfabética)
        public static List<string> Available()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                {
                    var m = Regex.Match(n, @"^idiomas\.([a-z0-9\-]+)\.json$");
                    if (m.Success) set.Add(m.Groups[1].Value);
                }
            }
            catch { }
            try
            {
                if (Directory.Exists(Dir))
                    foreach (var f in Directory.GetFiles(Dir, "*.json")) { var n = Path.GetFileName(f); if (FileName.IsMatch(n)) set.Add(n.Substring(0, n.Length - 5)); }
            }
            catch { }
            set.Remove(BaseLang);
            var order = new[] { "en", "es" };
            var list = new List<string> { BaseLang };
            list.AddRange(order.Where(set.Contains));
            list.AddRange(set.Where(c => !order.Contains(c)).OrderBy(c => c, StringComparer.Ordinal));
            return list;
        }

        public static string NameOf(string code)
        {
            code = Normalize(code);
            if (code == BaseLang) return "Português";
            var map = Resolve(code);
            if (map != null && map.TryGetValue("_nome", out var n) && !string.IsNullOrWhiteSpace(n)) return n;
            return code.ToUpperInvariant();
        }

        // ---------- Leitura dos arquivos ----------
        static Dictionary<string, string> Parse(string json)
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String) d[p.Name] = p.Value.GetString();
                else if (p.Value.ValueKind == JsonValueKind.Number && p.Name == "_versao") d[p.Name] = p.Value.GetRawText();
            }
            return d;
        }

        static int Version(Dictionary<string, string> d) => d != null && d.TryGetValue("_versao", out var v) && int.TryParse(v, out var n) ? n : 0;

        static Dictionary<string, string> ReadEmbedded(string code)
        {
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("idiomas." + code + ".json");
                if (s == null) return null;
                using var r = new StreamReader(s, Encoding.UTF8);
                return Parse(r.ReadToEnd());
            }
            catch (Exception ex) { AppLog.Write("Idiomas", "Falha ao ler idioma embutido " + code + ": " + ex.Message); return null; }
        }

        static Dictionary<string, string> ReadDisk(string code)
        {
            try
            {
                string path = Path.Combine(Dir, code + ".json");
                if (!File.Exists(path) || new FileInfo(path).Length > MaxJsonBytes) return null;
                return Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex) { AppLog.Write("Idiomas", "Falha ao ler idioma baixado " + code + ": " + ex.Message); return null; }
        }

        // Vale o de maior versão (empate: o baixado)
        static Dictionary<string, string> Resolve(string code)
        {
            if (!FileName.IsMatch(code + ".json")) return null;
            var disk = ReadDisk(code);
            var emb = ReadEmbedded(code);
            if (disk == null) return emb;
            if (emb == null) return disk;
            return Version(emb) > Version(disk) ? emb : disk;
        }

        // ---------- Download do GitHub ----------
        static readonly HttpClient http = MakeHttp();
        static HttpClient MakeHttp()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("AzulGrooveMusic/1.0");
            return c;
        }

        // Baixa idiomas.zip do GitHub e guarda os arquivos .json que forem mais novos. Retorna true se algo mudou.
        // Só lê texto JSON: nada baixado aqui é executado.
        public static async Task<bool> UpdateFromGitHubAsync()
        {
            try
            {
                using var resp = await http.GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) { AppLog.Write("Idiomas", "Download dos idiomas: HTTP " + (int)resp.StatusCode); return false; }
                long? len = resp.Content.Headers.ContentLength;
                if (len.HasValue && len.Value > MaxZipBytes) return false;
                byte[] data = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (data.Length > MaxZipBytes) return false;
                return await Task.Run(() => ExtractZip(data)).ConfigureAwait(false);
            }
            catch (Exception ex) { AppLog.Write("Idiomas", "Não foi possível baixar os idiomas: " + ex.Message); return false; }
        }

        static bool ExtractZip(byte[] data)
        {
            bool changed = false;
            Directory.CreateDirectory(Dir);
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                string name = Path.GetFileName(entry.FullName).ToLowerInvariant();   // ignora pastas e tentativas de sair do diretório
                if (!FileName.IsMatch(name) || entry.Length == 0 || entry.Length > MaxJsonBytes) continue;
                string code = name.Substring(0, name.Length - 5);
                if (code == BaseLang) continue;
                try
                {
                    string text;
                    using (var s = entry.Open()) using (var r = new StreamReader(s, Encoding.UTF8)) text = r.ReadToEnd();
                    var map = Parse(text);
                    if (map == null || map.Count < 5) continue;
                    var cur = Resolve(code);
                    if (cur != null && Version(map) <= Version(cur)) continue;
                    string path = Path.Combine(Dir, name), tmp = path + ".tmp";
                    File.WriteAllText(tmp, text, new UTF8Encoding(false));
                    File.Move(tmp, path, true);
                    changed = true;
                }
                catch (Exception ex) { AppLog.Write("Idiomas", "Arquivo de idioma inválido (" + name + "): " + ex.Message); }
            }
            return changed;
        }

        // Depois de baixar: recarrega o idioma atual e refaz a tradução da tela
        public static void Reload()
        {
            SetLanguage(Current);
            Refresh();
        }

        // ---------- Tradução da interface ----------
        // Cada elemento é traduzido quando carrega (XAML, janelas e menus criados no código). Guardamos o texto original,
        // então trocar de idioma depois (inclusive voltar ao português) funciona sem reiniciar.
        static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, string[]>> seen = new ConditionalWeakTable<DependencyObject, Dictionary<string, string[]>>();

        public static void Install()
        {
            if (installed) return;
            installed = true;
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, e) => { if (s is DependencyObject d) One(d); }));
        }

        static string Tr(DependencyObject d, string slot, string cur)
        {
            if (string.IsNullOrEmpty(cur)) return null;
            var dict = seen.GetOrCreateValue(d);
            string orig = cur;
            if (dict.TryGetValue(slot, out var e) && e[1] == cur) orig = e[0];   // ainda é o texto que nós colocamos: parte do original
            string t = Get(orig);
            dict[slot] = new[] { orig, t };
            return t == cur ? null : t;
        }

        static void Runs(InlineCollection inlines)
        {
            foreach (var inl in inlines)
            {
                if (inl is Run run)
                {
                    if (BindingOperations.IsDataBound(run, Run.TextProperty)) continue;
                    var t = Tr(run, "R", run.Text); if (t != null) run.Text = t;
                }
                else if (inl is Span span) Runs(span.Inlines);
            }
        }

        static void One(DependencyObject d)
        {
            if (!active && !touched) return;
            if (active) touched = true;
            try
            {
                if (d is TextBlock tb)
                {
                    if (tb.Inlines.Count == 1 && tb.Inlines.FirstInline is Run)
                    {
                        if (!BindingOperations.IsDataBound(tb, TextBlock.TextProperty)) { var t = Tr(tb, "T", tb.Text); if (t != null) tb.Text = t; }
                    }
                    else if (tb.Inlines.Count > 1) Runs(tb.Inlines);
                }
                else if (d is ContentControl cc && cc.Content is string cs && !BindingOperations.IsDataBound(cc, ContentControl.ContentProperty))
                {
                    var t = Tr(cc, "C", cs); if (t != null) cc.Content = t;
                }
                if (d is HeaderedContentControl hc && hc.Header is string hs && !BindingOperations.IsDataBound(hc, HeaderedContentControl.HeaderProperty))
                {
                    var t = Tr(hc, "H", hs); if (t != null) hc.Header = t;
                }
                else if (d is HeaderedItemsControl hi && hi.Header is string his && !BindingOperations.IsDataBound(hi, HeaderedItemsControl.HeaderProperty))
                {
                    var t = Tr(hi, "H", his); if (t != null) hi.Header = t;
                }
                if (d is FrameworkElement fe && fe.ToolTip is string ts && !BindingOperations.IsDataBound(fe, FrameworkElement.ToolTipProperty))
                {
                    var t = Tr(fe, "P", ts); if (t != null) fe.ToolTip = t;
                }
                if (d is Window w && !BindingOperations.IsDataBound(w, Window.TitleProperty))
                {
                    var t = Tr(w, "W", w.Title); if (t != null) w.Title = t;
                }
            }
            catch (Exception ex) { AppLog.Write("Idiomas", "Falha ao traduzir um elemento: " + ex.Message); }
        }

        // Refaz a tradução de todas as janelas abertas (usado ao trocar de idioma)
        public static void Refresh()
        {
            if (Application.Current == null) return;
            var visited = new HashSet<DependencyObject>();
            foreach (Window w in Application.Current.Windows) Walk(w, visited);
        }

        static void Walk(DependencyObject d, HashSet<DependencyObject> visited)
        {
            if (d == null || !visited.Add(d)) return;
            One(d);
            if (d is FrameworkElement fe)
            {
                if (fe.ContextMenu != null) Walk(fe.ContextMenu, visited);
                if (fe.ToolTip is DependencyObject tt) Walk(tt, visited);
            }
            if (d is Popup pop && pop.Child != null) Walk(pop.Child, visited);
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
            {
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), visited);
            }
            foreach (var c in LogicalTreeHelper.GetChildren(d)) if (c is DependencyObject dc) Walk(dc, visited);
        }
    }
}
