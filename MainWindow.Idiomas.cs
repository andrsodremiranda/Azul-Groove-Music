using System;
using System.Threading.Tasks;
using System.Windows;

namespace AzulGroove
{
    // Idioma da interface: escolha nas Configurações e download das traduções do GitHub (ver Localization.cs)
    public partial class MainWindow
    {
        void RefreshLanguageUi()
        {
            if (PrefLangBtn == null) return;
            PrefLangBtn.Content = T.NameOf(T.Current);
            RefreshSettingsView();
            UpdateNow();
            UpdateTrayText();
        }

        void PrefLang_Click(object s, RoutedEventArgs e)
        {
            var list = T.Available();
            int i = Math.Max(0, list.IndexOf(T.Current));
            string next = list[(i + 1) % list.Count];
            prefs.Language = next;
            prefs.Save();
            T.SetLanguage(next);
            T.Refresh();
            RefreshLanguageUi();
            Toast(T.Get("Idioma: {0}", T.NameOf(next)));
        }

        async void PrefLangUpd_Click(object s, RoutedEventArgs e)
        {
            Toast("Baixando idiomas…");
            bool changed = await T.UpdateFromGitHubAsync();
            if (changed) { T.Reload(); RefreshLanguageUi(); Toast("Idiomas atualizados"); }
            else Toast("Os idiomas já estão em dia");
        }

        // Ao abrir o app (com "Buscar atualizações ao abrir" ligado): baixa traduções novas sem atrapalhar a abertura
        async Task RefreshLanguagesAsync()
        {
            try
            {
                await Task.Delay(4000);
                if (await T.UpdateFromGitHubAsync())
                    await Dispatcher.InvokeAsync(() => { T.Reload(); RefreshLanguageUi(); });
            }
            catch (Exception ex) { AppLog.Write("Idiomas", "RefreshLanguagesAsync: " + ex.Message); }
        }
    }
}
