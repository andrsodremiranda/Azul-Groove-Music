# 🎵 Azul Groove Music

Player de música e vídeo para Windows, feito em C# (WPF) com visual azul, **fila inteligente** que aprende com o seu uso e integração com o YouTube / YouTube Music.

> Versão atual: **1.0.3**

---

## ✨ Recursos

**Player**
- Toca música e vídeo (motor VLC): biblioteca, artistas, mídia recente, pesquisa e fila de reprodução
- Equalizador, escolha da saída de áudio, velocidade, temporizador para dormir
- Nivelamento de volume entre músicas
- Legendas `.srt`, troca de faixa de áudio e proporção de tela nos vídeos
- Continua de onde parou ao reabrir o app
- Minimiza para a bandeja, inicia com o Windows e usa teclas de mídia em qualquer lugar
- Playlists salvas, playlists automáticas (★) e exportação para `.m3u`
- Modo leve e animações de fundo opcionais

**YouTube**
- Sincroniza playlists do YouTube e do YouTube Music (links, um por linha)
- Busca e baixa músicas direto pelo app (MP3 com capa e metadados)

## 🧠 Fila inteligente

A cada troca de música, o app completa a fila sozinho com base no que você ouve:

| Sinal | Como é usado |
|---|---|
| Reproduções | Faixas e artistas que você mais ouve ganham nota maior |
| Recência | O que você ouviu há pouco pesa mais que o antigo (meia-vida de 30 dias) |
| Hora do dia | Favorece o que você costuma ouvir naquele horário |
| Pulos | Pular uma faixa antes dos 30 s conta como "não gostei" |
| Contexto | Prefere o mesmo artista/álbum da música que está tocando |
| Variedade | Músicas nunca tocadas ou recém-adicionadas têm chance extra, com um pouco de sorteio |

**Músicas novas do YouTube (opcional, ligado por padrão):** a fila inteligente também baixa sozinha músicas que você ainda não tem, usando o mix de recomendações do YouTube a partir das faixas que você mais ouve. As músicas novas:
- ficam em `<pasta de downloads>\Audio\Automatico` (máximo de 60 arquivos);
- entram como a próxima da fila e na playlist **★ Músicas automáticas**;
- geram um aviso (dentro da janela, ou notificação do Windows se o app estiver em segundo plano).

Dá para ligar/desligar a música automática em **Configurações** ou pelo botão na página **Playlists**.

## 📦 Download

Pegue a versão mais recente na aba **Releases**:
- **Instalador:** `AzulGrooveMusic-Setup-x.y.z.exe`
- **Portátil:** `AzulGrooveMusic-Portatil-x.y.z.zip` (extraia e abra `AzulGrooveMusic.exe`)

Requisitos: Windows 10/11 64 bits. O .NET já vem incluso no app.

## 🛠️ Compilar

Requisitos: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) e, opcionalmente, [Inno Setup 6](https://jrsoftware.org/isdl.php) para gerar o instalador.

```bat
build.bat
```

O script gera:
- `publish\AzulGrooveMusic.exe` (para testar)
- `installer\AzulGrooveMusic-Portatil-x.y.z.zip` (versão portátil)
- `installer\AzulGrooveMusic-Setup-x.y.z.exe` (instalador, se o Inno Setup estiver instalado)

Ou direto pelo .NET:

```bat
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

## 🗂️ Estrutura do projeto

| Arquivo | O que faz |
|---|---|
| `MainWindow.xaml` / `.xaml.cs` | Janela principal e inicialização |
| `MainWindow.Playback.cs` | Reprodução, troca de faixas, controles |
| `MainWindow.Library.cs` | Biblioteca, varredura de arquivos e playlists |
| `MainWindow.Collections.cs` | Coleções salvas e histórico |
| `MainWindow.Extras.cs` | Contagem de reproduções e playlists automáticas |
| `MainWindow.SmartQueue.cs` | **Fila inteligente** e download de músicas novas |
| `MainWindow.YouTube.cs` / `YtSearch.cs` | Integração com YouTube (yt-dlp) e busca |
| `MainWindow.Settings.cs` | Configurações do usuário |
| `MainWindow.Tray.cs` | Bandeja do sistema e notificações |
| `MainWindow.Animations.cs` | Animações |
| `AppLog.cs` | Log de erros |
| `AzulGrooveMusic.iss` | Script do instalador (Inno Setup) |

## 💾 Onde ficam os dados

- Configurações, contagens de reprodução e aprendizado da fila: `%AppData%\AzulGroove`
- Log de erros: `%AppData%\AzulGroove\logs\azulgroove.log`
- Músicas baixadas do YouTube (padrão): `Música\Azul Groove YouTube`

## 🧩 Tecnologias

- [.NET 8](https://dotnet.microsoft.com/) + WPF / Windows Forms (bandeja)
- [LibVLCSharp](https://github.com/videolan/libvlcsharp) + VLC
- [TagLibSharp](https://github.com/mono/taglib-sharp) (tags e capas)
- [NAudio](https://github.com/naudio/NAudio)
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) e [FFmpeg](https://ffmpeg.org/) (baixados pelo app na primeira vez que você usa o YouTube)

## ⚠️ Aviso

Baixe apenas conteúdo que você tem direito de baixar e respeite os termos de uso do YouTube e as leis de direitos autorais do seu país. Este projeto não é afiliado ao YouTube, Google ou VideoLAN.

## 📄 Licença

© 2026 **ASTM Software**. Todos os direitos reservados.

O Azul Groove Music é gratuito para uso pessoal e não comercial, conforme a [Licença de Uso](LICENSE.txt). Não é permitido vender, modificar ou redistribuir versões alteradas sem autorização. Os componentes de terceiros seguem suas próprias licenças.
