@echo off
REM ===========================================================================
REM F2 real-read harness build (x86 v143) - NOT part of CI.
REM Produces F2RealReadHarness.exe under the native run directory.
REM Mirrors build.cmd toolchain conventions (vcvarsall x86, VC90 runtime copies).
REM Usage: f2build.cmd [run-dir]
REM ===========================================================================
setlocal
set ROOT=%~dp0..
if "%PLCMCP_NATIVE_RUN%"=="" (
  if not "%~1"=="" (
    set PLCMCP_NATIVE_RUN=%~1
  ) else (
    set PLCMCP_NATIVE_RUN=%LOCALAPPDATA%\AutoShopNativeBridge-run
  )
)
set RUN=%PLCMCP_NATIVE_RUN%
if not exist "%RUN%" mkdir "%RUN%"

set VCVARS="C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat"
call %VCVARS% x86 >nul
if errorlevel 1 (
  echo [f2build] vcvarsall x86 failed
  exit /b 1
)

cl /nologo /W3 /EHsc /MT "%~dp0F2RealReadHarness.cpp" /Fe:%RUN%\F2RealReadHarness.exe /link ws2_32.lib
if errorlevel 1 exit /b 1

set VC90DIR=C:\Windows\WinSxS\Fusion\x86_microsoft.vc90.crt_1fc8b3b9a1e18e3b_none_ea33c8f0b247cd77\9.0\9.0.30729.4974
set MFC90DIR=C:\Windows\WinSxS\Fusion\x86_microsoft.vc90.mfc_1fc8b3b9a1e18e3b_none_eaa49612b1f5b2ea\9.0\9.0.30729.4974
if not exist "%RUN%\mfc90u.dll" if exist "%MFC90DIR%\mfc90u.dll" copy /y "%MFC90DIR%\mfc90u.dll" "%RUN%\" >nul
if not exist "%RUN%\msvcp90.dll" if exist "%VC90DIR%\msvcp90.dll" copy /y "%VC90DIR%\msvcp90.dll" "%RUN%\" >nul
if not exist "%RUN%\msvcr90.dll" if exist "%VC90DIR%\msvcr90.dll" copy /y "%VC90DIR%\msvcr90.dll" "%RUN%\" >nul

del /q "%~dp0*.obj" >nul 2>&1
echo [f2build] OK - %RUN%\F2RealReadHarness.exe
endlocal
