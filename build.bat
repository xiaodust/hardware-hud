@echo off
setlocal
cd /d "%~dp0"

rem ---- usage ----
rem   build.bat          build only, output dist\HardwareHud.exe
rem   build.bat deploy   build, then overwrite the copy in the install dir
rem                       (kills the running instance first)
rem
rem No Visual Studio / .NET SDK needed: uses the .NET Framework 4.x
rem compiler that ships with Windows.

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WPF=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF
set FX=C:\Windows\Microsoft.NET\Framework64\v4.0.30319

rem 32-bit systems have no Framework64; fall back to Framework
if exist "%CSC%" goto havecsc
set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
set WPF=C:\Windows\Microsoft.NET\Framework\v4.0.30319\WPF
set FX=C:\Windows\Microsoft.NET\Framework\v4.0.30319

:havecsc
if not exist "%CSC%" goto nocsc
if not exist "src\HardwareHud.cs" goto nosrc
if not exist "dist" mkdir "dist"

set ICONPARAM=
if exist "assets\HardwareHud.ico" set ICONPARAM=/win32icon:"assets\HardwareHud.ico"

"%CSC%" /nologo /target:winexe /optimize+ /utf8output %ICONPARAM% /out:"dist\HardwareHud.exe" /r:"%FX%\System.dll" /r:"%FX%\System.Core.dll" /r:"%FX%\System.Xaml.dll" /r:"%WPF%\WindowsBase.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\PresentationFramework.dll" "src\HardwareHud.cs"

if errorlevel 1 goto failed

echo [OK] dist\HardwareHud.exe

if /i not "%~1"=="deploy" goto done

rem ---- optional: push to %LOCALAPPDATA%\Programs\HardwareHud ----
set INSTALL=%LOCALAPPDATA%\Programs\HardwareHud
if not exist "%INSTALL%\HardwareHud.exe" goto noinstall

taskkill /im HardwareHud.exe /f >nul 2>&1
copy /y "dist\HardwareHud.exe" "%INSTALL%\HardwareHud.exe" >nul
if exist "assets\HardwareHud.ico" copy /y "assets\HardwareHud.ico" "%INSTALL%\HardwareHud.ico" >nul
echo [OK] deployed to %INSTALL%
goto done

:noinstall
echo [SKIP] no existing install at %INSTALL%
goto done

:done
exit /b 0

:nocsc
echo [ERR] compiler not found: %CSC%
echo        requires .NET Framework 4.x (install it manually on Windows 7)
exit /b 1

:nosrc
echo [ERR] src\HardwareHud.cs not found
exit /b 1

:failed
echo [ERR] build failed
exit /b 1
