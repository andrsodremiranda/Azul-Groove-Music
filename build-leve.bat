@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
title Azul Groove Music - Gerar versao LEVE (sem .NET embutido)

rem Gera o app e o instalador o menor possivel: o .NET 8 Desktop Runtime NAO vai dentro.
rem O instalador confere se o runtime existe e, se faltar, abre a pagina de download.

where dotnet >nul 2>nul || (echo Instale o .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0 & pause & exit /b 1)

echo [1/3] Limpando...
if exist publish rmdir /s /q publish
if exist obj rmdir /s /q obj
if exist bin rmdir /s /q bin

echo [2/3] Compilando sem .NET embutido (a primeira vez baixa o VLC, pode demorar)...
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o publish
if errorlevel 1 (echo Falha ao compilar & pause & exit /b 1)
if not exist "publish\AzulGrooveMusic.exe" (echo O EXE nao foi gerado & pause & exit /b 1)

echo [3/3] Gerando o instalador leve (Inno Setup)...
if not exist installer mkdir installer
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
"%ISCC%" /DLeve=1 AzulGrooveMusic.iss
if errorlevel 1 (echo Falha ao gerar o instalador & pause & exit /b 1)

echo.
echo Pronto!
echo   Instalador leve : installer\AzulGrooveMusic-Leve-Setup-1.0.6.exe
echo   (precisa do .NET 8 Desktop Runtime no PC; o instalador avisa se faltar)
explorer installer
pause
