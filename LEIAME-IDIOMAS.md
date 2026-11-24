# Idiomas do Azul Groove Music

O português é o idioma base (os textos do próprio app são a "chave"). Cada outro idioma é um arquivo JSON:

```json
{
  "_nome": "Français",
  "_versao": 1,
  "Pesquisar": "Rechercher",
  "Início": "Accueil",
  "Volume {0}%": "Volume {0} %"
}
```

- `_nome`: nome mostrado no botão de idioma. `_versao`: número inteiro; **suba a cada correção**, o app só troca o arquivo se a versão for maior.
- Chaves com `{0}`, `{1}`... valem para textos montados em tempo de execução (o número/nome entra no lugar). Mantenha os mesmos `{n}` na tradução.
- `\n` é quebra de linha. Texto que não está no arquivo aparece em português.
- O nome do arquivo é o código do idioma: `en.json`, `es.json`, `fr.json`...

## Como publicar traduções no GitHub (sem lançar versão nova do app)

1. Coloque os `.json` dentro de um `idiomas.zip` (sem pastas).
2. Em https://github.com/andrsodremiranda/Azul-Groove-Music/releases crie um release com a tag **`idiomas`**.
3. **Marque como "pre-release" e desmarque "Set as the latest release".** Isso é obrigatório: o verificador de atualizações do app usa `releases/latest`, e um release de idiomas como "latest" atrapalharia as atualizações do app.
4. Anexe o `idiomas.zip` como arquivo do release (o nome tem que ser exatamente `idiomas.zip`).

O app baixa `https://github.com/andrsodremiranda/Azul-Groove-Music/releases/download/idiomas/idiomas.zip` ao abrir (se "Buscar atualizações ao abrir" estiver ligado) ou em Configurações → Idioma → Atualizar idiomas. Para corrigir uma tradução ou adicionar um idioma, basta enviar um `idiomas.zip` novo para o mesmo release (substituindo o arquivo) e subir o `_versao`.

Só arquivos `.json` pequenos são lidos do zip; nada baixado é executado.

`en.json` e `es.json` desta pasta também vão **dentro do app** (funcionam offline desde a primeira abertura).
