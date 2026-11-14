@echo off
setlocal
rem  Buduje pojedynczy plik dist\YoutubeTerminal.exe (Native AOT, win-x64).
rem  Wymagania: .NET SDK 10 oraz Visual Studio (Build Tools) z obciążeniem
rem  "Programowanie aplikacji klasycznych w języku C++" (linker MSVC).
rem  Plik tools\yt-dlp.exe (jeśli istnieje) zostanie wbudowany w program.

chcp 65001 >nul
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"

if not exist "%~dp0tools\yt-dlp.exe" (
  echo [!] Brak tools\yt-dlp.exe — program zostanie zbudowany bez wbudowanego yt-dlp.
  echo     Pobierz go z https://github.com/yt-dlp/yt-dlp/releases i umieść w folderze tools.
)

dotnet publish "%~dp0src\YoutubeTerminal\YoutubeTerminal.csproj" -c Release -r win-x64 -o "%~dp0dist" -nologo
if errorlevel 1 exit /b 1

if exist "%~dp0dist\odinstaluj.cmd" del /q "%~dp0dist\odinstaluj.cmd"
echo.
echo Gotowe: %~dp0dist\YoutubeTerminal.exe
