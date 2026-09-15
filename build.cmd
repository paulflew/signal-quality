@echo off
rem Builds SignalQuality.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No SDK required. See SignalQuality.csproj if you'd rather use dotnet build or Visual Studio.
rem   build.cmd        build the tray app
rem   build.cmd test   also build and run the test harness
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Could not find the .NET Framework 4 C# compiler ^(csc.exe^).
    exit /b 1
)

pushd "%~dp0"
set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
set ICON=
if exist assets\SignalQuality.ico set ICON=/win32icon:assets\SignalQuality.ico

"%CSC%" /nologo /target:winexe /optimize+ %REFS% %ICON% /out:SignalQuality.exe src\SignalQuality.cs
if errorlevel 1 goto :failed
echo Built SignalQuality.exe

if /i "%~1"=="test" (
    if not exist build mkdir build
    "%CSC%" /nologo /target:exe %REFS% /main:SignalQuality.Tests /out:build\Tests.exe src\SignalQuality.cs tests\Tests.cs
    if errorlevel 1 goto :failed
    build\Tests.exe build
    if errorlevel 1 goto :failed
)

popd
exit /b 0

:failed
popd
exit /b 1
