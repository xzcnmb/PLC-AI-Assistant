@echo off
REM ===========================================================================
REM  F1 write-surface verification harness build (VS2022 v143, x86)
REM
REM  Produces, under %%LOCALAPPDATA%%\AutoShopNativeBridge-f1-run\ (NOT committed):
REM    WriteProbe.exe              F1 write-surface verification harness
REM    CommStackWrite.def/.lib     import lib generated from the vendor export table
REM    mfc90u/msvcp90/msvcr90.dll  VC90 runtimes (copied from WinSxS)
REM
REM  LOCAL SIMULATOR ONLY (LocalSimulator): WriteProbe.exe hard-checks the
REM  target to 127.0.0.0/8 and never runs in CI.
REM
REM  Requirements: VS2022 Community with "Desktop development with C++"
REM ===========================================================================
setlocal enabledelayedexpansion

set ROOT=%~dp0
if "%PLCMCP_F1_RUN%"=="" (
  set RUN=%LOCALAPPDATA%\AutoShopNativeBridge-f1-run
) else (
  set RUN=%PLCMCP_F1_RUN%
)
set VENDOR_INSTALL_DIR=D:\Inovance Control\AutoShop

if not exist "%RUN%" mkdir "%RUN%"

set VCVARS="C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat"
if not exist %VCVARS% (
  echo [build-f1] vcvarsall.bat not found at %VCVARS%
  exit /b 1
)
call %VCVARS% x86 >nul
if errorlevel 1 (
  echo [build-f1] vcvarsall x86 failed
  exit /b 1
)

echo [build-f1] toolchain: x86 v143

REM --- import library with the write-surface exports -------------------------
python "%ROOT%gen_write_def.py" "%VENDOR_INSTALL_DIR%\CommunicateEthernet.dll" "%RUN%\CommStackWrite.def"
if errorlevel 1 (
  echo [build-f1] export-table extraction failed
  exit /b 1
)
lib /nologo /def:"%RUN%\CommStackWrite.def" /out:"%RUN%\CommStackWrite.lib" /machine:x86
if errorlevel 1 (
  echo [build-f1] lib.exe failed
  exit /b 1
)

REM --- harness ----------------------------------------------------------------
cl /nologo /W3 /EHsc /MT ^
   "%ROOT%WriteProbe.cpp" ^
   /Fe:%RUN%\WriteProbe.exe ^
   /Fd:%RUN%\WriteProbe.pdb ^
   /link "%RUN%\CommStackWrite.lib" ws2_32.lib
if errorlevel 1 exit /b 1

REM --- runtime dependencies (read-only copies; the vendor DLL needs VC90 MFC) --
set VC90DIR=C:\Windows\WinSxS\Fusion\x86_microsoft.vc90.crt_1fc8b3b9a1e18e3b_none_ea33c8f0b247cd77\9.0\9.0.30729.4974
set MFC90DIR=C:\Windows\WinSxS\Fusion\x86_microsoft.vc90.mfc_1fc8b3b9a1e18e3b_none_eaa49612b1f5b2ea\9.0\9.0.30729.4974
if not exist "%RUN%\mfc90u.dll" (
  if exist "%MFC90DIR%\mfc90u.dll" copy /y "%MFC90DIR%\mfc90u.dll" "%RUN%\" >nul
)
if not exist "%RUN%\msvcp90.dll" copy /y "%VC90DIR%\msvcp90.dll" "%RUN%\" >nul
if not exist "%RUN%\msvcr90.dll" copy /y "%VC90DIR%\msvcr90.dll" "%RUN%\" >nul

del /q "%ROOT%*.obj" >nul 2>&1

echo [build-f1] OK - artifacts in %RUN%
dir /b "%RUN%"
endlocal
