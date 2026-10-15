@echo off
rem Builds and runs the tests: shapes and undo, and stitching of long screenshots.
rem Uses the C# compiler that ships with Windows (.NET Framework 4.8), like build.cmd.
setlocal
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WPF=%FW%\WPF
set REFS=/r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" /r:"%WPF%\UIAutomationClient.dll" /r:"%WPF%\UIAutomationTypes.dll" /r:System.Xaml.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.VisualBasic.dll
if not exist tests\bin mkdir tests\bin

"%FW%\csc.exe" /nologo /target:exe /codepage:65001 /optimize+ /out:tests\bin\StitchTest.exe %REFS% ^
  src\ScrollCapture.cs src\Log.cs tests\StitchTest.cs tests\StitchDebug.cs
if errorlevel 1 goto build_failed
"%FW%\csc.exe" /nologo /target:exe /codepage:65001 /main:ShapeTest /out:tests\bin\ShapeTest.exe %REFS% ^
  src\*.cs tests\ShapeTest.cs
if errorlevel 1 goto build_failed

set FAILED=0
echo.
echo === Shapes and undo ===
tests\bin\ShapeTest.exe || set FAILED=1
echo.
echo === Long screenshot stitching ===
tests\bin\StitchTest.exe || set FAILED=1
echo.
if %FAILED%==0 (echo TESTS PASSED) else (echo TESTS FAILED)
exit /b %FAILED%

:build_failed
echo BUILD FAILED
exit /b 1
