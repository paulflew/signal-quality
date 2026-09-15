@echo off
rem Regenerates assets\SignalQuality.ico and docs\lights.png from the tray drawing code.
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Could not find the .NET Framework 4 C# compiler ^(csc.exe^).
    exit /b 1
)

pushd "%~dp0.."
if not exist build mkdir build
"%CSC%" /nologo /target:exe /main:SignalQuality.MakeIcon /out:build\MakeIcon.exe ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    src\SignalQuality.cs tools\MakeIcon.cs
if errorlevel 1 goto :failed
build\MakeIcon.exe assets\SignalQuality.ico docs\lights.png
if errorlevel 1 goto :failed
popd
exit /b 0

:failed
popd
exit /b 1
