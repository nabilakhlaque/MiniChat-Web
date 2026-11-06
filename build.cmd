@echo off
setlocal
rem ------------------------------------------------------------------
rem  Bygger MiniChat.exe med kompilatorn som redan finns i Windows
rem  (.NET Framework 4.x). Ingen Visual Studio, ingen SDK, inga paket.
rem ------------------------------------------------------------------
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo Hittar inte csc.exe. .NET Framework 4.x verkar saknas.
    pause
    exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 ^
    /out:MiniChat.exe ^
    /win32icon:app.ico ^
    /win32manifest:app.manifest ^
    /resource:app.ico,MiniChat.app.ico ^
    /resource:alert.ico,MiniChat.alert.ico ^
    /resource:ding.wav,MiniChat.ding.wav ^
    /resource:app.html,MiniChat.app.html ^
    /resource:prutt.wav,MiniChat.prutt.wav ^
    /resource:skratt.wav,MiniChat.skratt.wav ^
    /resource:knock.wav,MiniChat.knock.wav ^
    /reference:System.dll ^
    /reference:System.Core.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Security.dll ^
    /reference:System.Web.Extensions.dll ^
    AssemblyInfo.cs Program.cs MainForm.cs Network.cs Settings.cs History.cs CloudBridge.cs

if errorlevel 1 (
    echo.
    echo Kompileringen misslyckades.
    pause
    exit /b 1
)

echo.
echo Klart: %~dp0MiniChat.exe
echo Kopiera bara den filen dit du vill ha den - inget behover installeras.
pause
