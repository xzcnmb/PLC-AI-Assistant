@echo off
REM ===========================================================================
REM  AutoShopNativeBridge build script (VS2022 v143, x86)
REM
REM  Produces, under tools\AutoShopNativeBridge\run\ (NOT committed):
REM    PlcMcpBridgePlugin.dll      M1 plugin (CreatePlugin + legacy 7 exports)
REM    PlcMcpBridgeNoFactory.dll   M1 negative-test DLL (no CreatePlugin)
REM    CommStackShim.dll           M3 read-only comm-stack shim
REM    TestHost.exe                M1/M3/M4 verification harness
REM    Plugin.dll                  vendor import target (copied, read-only)
REM    mfc90u/msvcp90/msvcr90.dll  VC90 runtimes (copied from WinSxS)
REM
REM  Requirements: VS2022 Community with "Desktop development with C++"
REM  (v143 toolset + x86 support). Windows SDK for the x86 CRT.
REM ===========================================================================
setlocal enabledelayedexpansion

set ROOT=%~dp0
set SRC=%ROOT%src
REM Build outputs go OUTSIDE the repository (binaries are not committed).
REM Override with: set PLCMCP_NATIVE_RUN=<dir>  (or pass it as %%1)
if "%PLCMCP_NATIVE_RUN%"=="" (
  if not "%~1"=="" (
    set PLCMCP_NATIVE_RUN=%~1
  ) else (
    set PLCMCP_NATIVE_RUN=%LOCALAPPDATA%\AutoShopNativeBridge-run
  )
)
set RUN=%PLCMCP_NATIVE_RUN%
set VENDOR_PLUGIN_DIR=D:\Inovance Control\AutoShop\AutoVarShared
set VENDOR_INSTALL_DIR=D:\Inovance Control\AutoShop

if not exist "%RUN%" mkdir "%RUN%"

REM --- locate VS2022 v143 x86 tools -----------------------------------------
set VCVARS="C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat"
if not exist %VCVARS% (
  echo [build] vcvarsall.bat not found at %VCVARS%
  echo [build] adjust VCVARS in build.cmd to your VS2022 installation
  exit /b 1
)
call %VCVARS% x86 >nul
if errorlevel 1 (
  echo [build] vcvarsall x86 failed
  exit /b 1
)

echo [build] toolchain: x86 v143

REM --- vendor import library for Plugin.dll ---------------------------------
REM Plugin.lib ships with the product (VS2008 COFF import library). It is
REM copied into the run directory so the linker finds it without touching the
REM installation.
if not exist "%RUN%\Plugin.lib" (
  if exist "%VENDOR_PLUGIN_DIR%\Plugin.lib" (
    copy /y "%VENDOR_PLUGIN_DIR%\Plugin.lib" "%RUN%\Plugin.lib" >nul
  ) else (
    echo [build] vendor Plugin.lib not found under %VENDOR_PLUGIN_DIR%
    exit /b 1
  )
)

REM --- import library for CommunicateEthernet.dll ---------------------------
REM No import library ships for the communication stack, so generate one from
REM the export table (only the five read-only statics are imported).
python "%~dp0scripts\gen_import_lib.py" "%VENDOR_INSTALL_DIR%\CommunicateEthernet.dll" "%RUN%\CommStack.def"
if errorlevel 1 (
  echo [build] export-table extraction failed
  exit /b 1
)
lib /nologo /def:"%RUN%\CommStack.def" /out:"%RUN%\CommStack.lib" /machine:x86
if errorlevel 1 (
  echo [build] lib.exe failed
  exit /b 1
)

REM --- M1 plugin DLL ----------------------------------------------------------
cl /nologo /W3 /EHsc /LD /MT ^
   /I"%SRC%\plugin" /I"%SRC%\pipe" ^
   "%SRC%\plugin\PlcMcpBridgePlugin.cpp" "%SRC%\pipe\BridgePipeServer.cpp" ^
   /Fe:%RUN%\PlcMcpBridgePlugin.dll ^
   /Fd:%RUN%\PlcMcpBridgePlugin.pdb ^
   /link "%RUN%\Plugin.lib"
if errorlevel 1 exit /b 1

REM --- M1 negative-test DLL ---------------------------------------------------
cl /nologo /W3 /EHsc /LD /MT "%SRC%\plugin\PlcMcpBridgeNoFactory.cpp" ^
   /Fe:%RUN%\PlcMcpBridgeNoFactory.dll
if errorlevel 1 exit /b 1

REM --- M3 comm-stack shim -----------------------------------------------------
cl /nologo /W3 /EHsc /LD /MT ^
   /I"%SRC%\shim" ^
   "%SRC%\shim\CommStackShim.cpp" ^
   /Fe:%RUN%\CommStackShim.dll ^
   /link "%RUN%\CommStack.lib"
if errorlevel 1 exit /b 1

REM --- M1/M3/M4 harness -------------------------------------------------------
cl /nologo /W3 /EHsc /MT ^
   /I"%SRC%\plugin" ^
   "%SRC%\host\TestHost.cpp" ^
   /Fe:%RUN%\TestHost.exe ^
   /link "%RUN%\Plugin.lib"
if errorlevel 1 exit /b 1

REM --- runtime dependencies (read-only copies) --------------------------------
if not exist "%RUN%\Plugin.dll" copy /y "%VENDOR_PLUGIN_DIR%\Plugin.dll" "%RUN%\" >nul

set VC90DIR=C:\Windows\WinSxS\Fusion\x86_microsoft.vc90.crt_1fc8b3b9a1e18e3b_none_ea33c8f0b247cd77\9.0\9.0.30729.4974
set MFC90DIR=C:\Windows\WinSxS\Fusion\x86_microsoft.vc90.mfc_1fc8b3b9a1e18e3b_none_eaa49612b1f5b2ea\9.0\9.0.30729.4974
if not exist "%RUN%\mfc90u.dll" (
  if exist "%MFC90DIR%\mfc90u.dll" copy /y "%MFC90DIR%\mfc90u.dll" "%RUN%\" >nul
)
if not exist "%RUN%\msvcp90.dll" copy /y "%VC90DIR%\msvcp90.dll" "%RUN%\" >nul
if not exist "%RUN%\msvcr90.dll" copy /y "%VC90DIR%\msvcr90.dll" "%RUN%\" >nul
if not exist "%RUN%\mfc90u.dll" (
  echo [build] WARNING: mfc90u.dll not found in WinSxS; harness will fail to load Plugin.dll
)

REM Intermediate objects are dropped next to the sources; remove them so the
REM repository only ever contains sources, scripts and the README.
del /q "%ROOT%*.obj" >nul 2>&1

echo [build] OK - artifacts in %RUN%
dir /b "%RUN%"
endlocal
