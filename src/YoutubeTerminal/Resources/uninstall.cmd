@echo off
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul
title Deinstalator YouTube Terminal

rem  Deinstalator YouTube Terminal
rem  Usuwa program, wypakowany yt-dlp (wraz z jego cache i plikami tymczasowymi),
rem  ustawienia oraz bibliotekę (subskrypcje, historia) — opcjonalnie je zachowuje.
rem
rem  Użycie:  uninstall.cmd          — tryb interaktywny
rem           uninstall.cmd /q       — bez pytań, usuwa wszystko
rem           uninstall.cmd /keep    — bez pytań, zachowuje subskrypcje, historię i ustawienia

set "APPDIR=%~dp0"
set "EXE=%APPDIR%__EXE_NAME__"
set "DATA=%LOCALAPPDATA%\YoutubeTerminal"
set "QUIET=0"
set "KEEP=0"
if /i "%~1"=="/q" set "QUIET=1"
if /i "%~1"=="/keep" set "QUIET=1"
if /i "%~1"=="/keep" set "KEEP=1"

echo.
echo   ==========================================
echo           Deinstalacja YouTube Terminal
echo   ==========================================
echo.
echo   Zostaną usunięte:
if exist "%EXE%" echo     - program:   %EXE%
echo     - dane:      %DATA%
echo                  (yt-dlp, cache yt-dlp, pliki tymczasowe, log)
echo     - ten skrypt
echo.
if "%QUIET%"=="1" goto :run
choice /C TN /N /M "  Kontynuować? [T/N] "
if errorlevel 2 goto :cancel

if exist "%DATA%\library.json" (
  echo.
  choice /C TN /N /M "  Zachować subskrypcje, historię i ustawienia? [T/N] "
  if not errorlevel 2 set "KEEP=1"
)

:run
echo.
echo   Zamykanie uruchomionych procesów programu i yt-dlp...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$d = Join-Path $env:LOCALAPPDATA 'YoutubeTerminal'; $e = $env:EXE;" ^
  "Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and (($_.Path -like ($d + '\*')) -or ($_.Path -ieq $e)) } | Stop-Process -Force -ErrorAction SilentlyContinue" >nul 2>&1
ping -n 2 127.0.0.1 >nul

if exist "%DATA%" (
  if "%KEEP%"=="1" (
    call :clean_keep
  ) else (
    rd /s /q "%DATA%" 2>nul
    if exist "%DATA%" (
      ping -n 3 127.0.0.1 >nul
      rd /s /q "%DATA%" 2>nul
    )
    if exist "%DATA%" (echo   [!] Nie udało się usunąć całego folderu danych: %DATA%) else (echo   Usunięto dane aplikacji i yt-dlp.)
  )
) else (
  echo   Brak folderu danych — pomijam.
)

if exist "%EXE%" (
  del /f /q "%EXE%" 2>nul
  if exist "%EXE%" (echo   [!] Nie udało się usunąć programu — czy jest uruchomiony?) else (echo   Usunięto program.)
)

echo.
echo   Deinstalacja zakończona.
echo.
if "%QUIET%"=="0" pause
(goto) 2>nul & del /f /q "%~f0"
exit /b 0

:clean_keep
for /d %%D in ("%DATA%\*") do rd /s /q "%%D" 2>nul
for %%F in ("%DATA%\*") do (
  if /i not "%%~nxF"=="settings.json" if /i not "%%~nxF"=="library.json" del /f /q "%%F" 2>nul
)
echo   Usunięto yt-dlp, cache i pliki tymczasowe.
echo   Zachowano ustawienia i bibliotekę w: %DATA%
exit /b 0

:cancel
echo.
echo   Anulowano. Nic nie zostało usunięte.
echo.
if "%QUIET%"=="0" pause
exit /b 1
