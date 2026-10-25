@echo off
rem Dainric Packer - native C++ backend self-test
rem Author: Dainric
rem
rem Pure ASCII on purpose: cmd.exe reads .bat with the system ANSI codepage,
rem so UTF-8 comments here turn into garbage commands.
setlocal

set "VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
if not exist "%VCVARS%" (
  echo [x] vcvars64.bat not found
  exit /b 1
)

set "ROOT=%~dp0"
set "SRC=%ROOT%..\src"
set "OUT=%ROOT%..\build\tests"
if not exist "%OUT%" mkdir "%OUT%"

call "%VCVARS%" >nul 2>&1

set "FLAGS=/nologo /std:c++17 /EHsc /MT /O2 /W3 /DUNICODE /D_UNICODE /utf-8"

echo compiling backend + test...
cl %FLAGS% /Fo"%OUT%\\" /Fe"%OUT%\dtest.exe" ^
   "%ROOT%dtest.cpp" "%SRC%\platform.cpp" "%SRC%\diagnose_rules.cpp" ^
   "%SRC%\deps_check.cpp" "%SRC%\runtime_locate.cpp" "%SRC%\build_engine.cpp" "%SRC%\version_info.cpp" ^
   /link /SUBSYSTEM:CONSOLE user32.lib shell32.lib ole32.lib advapi32.lib
if errorlevel 1 (
  echo [x] build failed
  exit /b 1
)

echo.
"%OUT%\dtest.exe" "%~1" "%~2"
exit /b %errorlevel%
