@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
title Azul Groove Music - Gerar EXE e instalador

where dotnet >nul 2>nul || (echo Instale o .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0 & pause & exit /b 1)

echo [1/3] Limpando...
if exist publish rmdir /s /q publish
if exist obj rmdir /s /q obj
if exist bin rmdir /s /q bin

echo [2/3] Compilando (a primeira vez baixa o VLC, pode demorar)...
dotnet publish -c Release -r win-x64 --self-contained true -o publish
if errorlevel 1 (
  echo.
  echo Falhou com ReadyToRun. Tentando de novo sem ele...
  dotnet publish -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -o publish
  if errorlevel 1 (echo Falha ao compilar & pause & exit /b 1)
)
if not exist "publish\AzulGrooveMusic.exe" (echo O EXE nao foi gerado & pause & exit /b 1)
if not exist "publish\libvlc" echo AVISO: a pasta publish\libvlc nao foi criada - o app nao vai tocar sem ela.

echo [3/3] Gerando a versao portatil (EXE unico, com o VLC dentro)...
if exist publish-portable rmdir /s /q publish-portable
if exist libvlc.zip del libvlc.zip
if not exist installer mkdir installer
if not exist "publish\libvlc\win-x64" goto noportable
powershell -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::CreateFromDirectory('publish\libvlc\win-x64','libvlc.zip',[IO.Compression.CompressionLevel]::Optimal,$false)"
if errorlevel 1 goto noportable
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -p:EmbedLibVlc=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish-portable
if errorlevel 1 goto noportable
copy /y "publish-portable\AzulGrooveMusic.exe" "installer\AzulGrooveMusic-Portatil-1.0.4.exe" >nul
echo Portatil: installer\AzulGrooveMusic-Portatil-1.0.4.exe
goto portabledone
:noportable
echo AVISO: nao foi possivel gerar o EXE portatil
:portabledone
if exist libvlc.zip del libvlc.zip

echo Gerando o instalador (Inno Setup)...
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if "%ISCC%"=="" (
  echo Inno Setup 6 nao encontrado. Baixe em https://jrsoftware.org/isdl.php e rode este arquivo de novo.
  echo O EXE para teste ja esta em: publish\AzulGrooveMusic.exe
  explorer publish
  pause
  exit /b 0
)
"%ISCC%" AzulGrooveMusic.iss
if errorlevel 1 (echo Falha ao gerar o instalador & pause & exit /b 1)

echo.
echo Pronto!
echo   EXE para testar : publish\AzulGrooveMusic.exe
echo   Portatil (EXE)  : installer\AzulGrooveMusic-Portatil-1.0.4.exe
echo   Instalador      : installer\AzulGrooveMusic-Setup-1.0.4.exe
explorer installer
pause
