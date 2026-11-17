@echo off
setlocal
cd /d "%~dp0"

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WPF=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF
set FX=C:\Windows\Microsoft.NET\Framework64\v4.0.30319

if not exist "%CSC%" goto nocsc
if not exist "dist" mkdir "dist"

set ICONPARAM=
if exist "assets\HardwareHud.ico" set ICONPARAM=/win32icon:"assets\HardwareHud.ico"

"%CSC%" /nologo /target:winexe /optimize+ /utf8output %ICONPARAM% /out:"dist\HardwareHud.exe" /r:"%FX%\System.dll" /r:"%FX%\System.Core.dll" /r:"%FX%\System.Xaml.dll" /r:"%WPF%\WindowsBase.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\PresentationFramework.dll" "src\HardwareHud.cs"

if errorlevel 1 goto failed

rem ---- 若已安装到 %LOCALAPPDATA%\Programs\HardwareHud，顺手把新版本推过去 ----
set INSTALL=%LOCALAPPDATA%\Programs\HardwareHud
if not exist "%INSTALL%\HardwareHud.exe" goto built
taskkill /im HardwareHud.exe /f >nul 2>&1
copy /y "dist\HardwareHud.exe" "%INSTALL%\HardwareHud.exe" >nul
if exist "assets\HardwareHud.ico" copy /y "assets\HardwareHud.ico" "%INSTALL%\HardwareHud.ico" >nul
echo [OK] deployed to %INSTALL%

:built
echo [OK] dist\HardwareHud.exe
exit /b 0

:nocsc
echo [ERR] compiler not found: %CSC%
exit /b 1

:failed
echo [ERR] build failed
exit /b 1
