@echo off
setlocal
cd /d "%~dp0"

rem ---- usage ----
rem   build.bat          build only, output dist\HardwareHud.exe
rem   build.bat setup    build app, then dist\HardwareHudSetup.exe (installer)
rem   build.bat deploy   build, then overwrite the copy in the install dir
rem                       (kills the running instance first)
rem   build.bat all      build app + installer, then deploy
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

if /i "%~1"=="deploy" goto deploypart
if /i "%~1"=="setup" goto setuppart
if /i "%~1"=="all" goto setuppart
goto done

rem ---- installer: app exe embedded as a resource, no admin needed ----
:setuppart
if not exist "installer\Setup.cs" goto nosetup
set SETUPRES=/resource:"dist\HardwareHud.exe",HardwareHud.exe
if exist "assets\HardwareHud.ico" set SETUPRES=%SETUPRES% /resource:"assets\HardwareHud.ico",HardwareHud.ico

"%CSC%" /nologo /target:winexe /optimize+ /utf8output %ICONPARAM% /out:"dist\HardwareHudSetup.exe" /r:"%FX%\System.dll" /r:"%FX%\System.Core.dll" /r:"%FX%\System.Drawing.dll" /r:"%FX%\System.Windows.Forms.dll" %SETUPRES% "installer\Setup.cs"

if errorlevel 1 goto failed

echo [OK] dist\HardwareHudSetup.exe
if /i not "%~1"=="all" goto done

rem ---- optional: push to %LOCALAPPDATA%\Programs\HardwareHud ----
:deploypart
set INSTALL=%LOCALAPPDATA%\Programs\HardwareHud
if not exist "%INSTALL%\HardwareHud.exe" goto noinstall

rem the old instance may keep the file locked for a moment after taskkill
taskkill /im HardwareHud.exe /f >nul 2>&1
set RETRY=0

:copytry
copy /y "dist\HardwareHud.exe" "%INSTALL%\HardwareHud.exe" >nul 2>&1
if not errorlevel 1 goto copydone
set /a RETRY=RETRY+1
if %RETRY% GEQ 12 goto deployfail
ping -n 1 -w 400 127.0.0.1 >nul
goto copytry

:copydone
if exist "assets\HardwareHud.ico" copy /y "assets\HardwareHud.ico" "%INSTALL%\HardwareHud.ico" >nul 2>&1
echo [OK] deployed to %INSTALL%
goto done

:deployfail
echo [ERR] could not overwrite %INSTALL%\HardwareHud.exe (still running or locked)
exit /b 1

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

:nosetup
echo [ERR] installer\Setup.cs not found
exit /b 1

:failed
echo [ERR] build failed
exit /b 1
