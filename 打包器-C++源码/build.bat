@echo off
rem Dainric Packer - native C++ build script
rem Author: Dainric
rem
rem NOTE: keep this file pure ASCII. cmd.exe reads .bat using the system
rem ANSI codepage, so UTF-8 Chinese comments turn into garbage commands.
rem
rem /MT  -> static CRT, target machine needs no vcruntime140.dll
setlocal

set "VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
if not exist "%VCVARS%" (
  echo [x] vcvars64.bat not found - install VS Build Tools first
  exit /b 1
)

set "ROOT=%~dp0"
set "SRC=%ROOT%src"
set "OUT=%ROOT%build"
if not exist "%OUT%" mkdir "%OUT%"
rem Link uses "%OUT%\*.obj": clear stale objects first so leftovers from
rem other builds (e.g. tests) cannot cause duplicate-symbol link errors.
del /q "%OUT%\*.obj" 2>nul

call "%VCVARS%" >nul 2>&1
if errorlevel 1 (
  echo [x] vcvars64 init failed
  exit /b 1
)

set "CL_FLAGS=/nologo /std:c++17 /EHsc /MT /O2 /GS- /W3 /DNDEBUG /DUNICODE /D_UNICODE /utf-8"
set "SOURCES=%SRC%\main.cpp %SRC%\platform.cpp %SRC%\diagnose_rules.cpp %SRC%\deps_check.cpp %SRC%\runtime_locate.cpp %SRC%\build_engine.cpp %SRC%\ui_shell.cpp %SRC%\version_info.cpp"
set "LIBS=user32.lib gdi32.lib shell32.lib comdlg32.lib ole32.lib d2d1.lib dwrite.lib dwmapi.lib shlwapi.lib advapi32.lib"

echo [1/2] compiling...
cl %CL_FLAGS% /Fo"%OUT%\\" /Fd"%OUT%\vc.pdb" /c %SOURCES%
if errorlevel 1 (
  echo [x] compile failed
  exit /b 1
)

echo [2/2] linking...
link /nologo /SUBSYSTEM:WINDOWS /ENTRY:mainCRTStartup /OPT:REF /OPT:ICF /OUT:"%OUT%\DainricPacker.exe" "%OUT%\*.obj" %LIBS%
if errorlevel 1 (
  echo [x] link failed
  exit /b 1
)

echo.
echo [+] built: %OUT%\DainricPacker.exe
for %%F in ("%OUT%\DainricPacker.exe") do echo     size: %%~zF bytes
endlocal
