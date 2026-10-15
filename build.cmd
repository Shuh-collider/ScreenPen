@echo off
rem Builds ScreenPen.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
setlocal
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WPF=%FW%\WPF

"%FW%\csc.exe" /nologo /target:winexe /codepage:65001 /optimize+ /out:ScreenPen.exe ^
  /win32icon:ScreenPen.ico /resource:ScreenPen.ico,ScreenPen.ico ^
  /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" ^
  /r:"%WPF%\UIAutomationClient.dll" /r:"%WPF%\UIAutomationTypes.dll" ^
  /r:System.Xaml.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.VisualBasic.dll ^
  src\*.cs
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo Built ScreenPen.exe
