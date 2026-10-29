@echo off
REM ===========================================================================
REM  AutoShopInjectBridge build script (VS2022 v143, x86)  -- E2, phase three
REM
REM  Produces, under %LOCALAPPDATA%\AutoShopInjectBridge-run\ (NOT committed):
REM    AutoShopInjectBridge.dll         bridge DLL (pipe server + vendor calls)
REM    AutoShopInjectBridgeInjector.exe x86 injector (launch/stop/eject/status)
REM    BridgeTestHost.exe               in-process verification harness
REM
REM  Requirements: VS2022 Community "Desktop development with C++" (v143 + x86).
REM  No third-party dependency; no import library needed (the bridge resolves
REM  the vendor exports at runtime, exactly like the D2 crypto shim).
REM ===========================================================================
setlocal

set ROOT=%~dp0
set SRC=%ROOT%src
REM Build outputs go OUTSIDE the repository (binaries are not committed).
REM Override with: set PLCMCP_INJECT_RUN=<dir>  (or pass it as %%1)
if "%PLCMCP_INJECT_RUN%"=="" (
  if not "%~1"=="" (
    set PLCMCP_INJECT_RUN=%~1
  ) else (
    set PLCMCP_INJECT_RUN=%LOCALAPPDATA%\AutoShopInjectBridge-run
  )
)
set RUN=%PLCMCP_INJECT_RUN%

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

REM --- bridge DLL (T2) --------------------------------------------------------
cl /nologo /W3 /EHsc /LD /MT ^
   /I"%SRC%\bridge" ^
   "%SRC%\bridge\AutoShopInjectBridge.cpp" "%SRC%\bridge\BridgePipeServer.cpp" "%SRC%\bridge\VendorComm.cpp" ^
   /Fo:%RUN%\ ^
   /Fe:%RUN%\AutoShopInjectBridge.dll ^
   /Fd:%RUN%\AutoShopInjectBridge.pdb ^
   /link user32.lib
if errorlevel 1 exit /b 1

REM --- injector (T1) ----------------------------------------------------------
cl /nologo /W3 /EHsc /MT ^
   "%SRC%\injector\AutoShopInjectBridgeInjector.cpp" ^
   /Fo:%RUN%\ ^
   /Fe:%RUN%\AutoShopInjectBridgeInjector.exe ^
   /Fd:%RUN%\AutoShopInjectBridgeInjector.pdb ^
   /link advapi32.lib
if errorlevel 1 exit /b 1

REM --- in-process verification harness ----------------------------------------
cl /nologo /W3 /EHsc /MT ^
   "%SRC%\host\BridgeTestHost.cpp" ^
   /Fo:%RUN%\ ^
   /Fe:%RUN%\BridgeTestHost.exe ^
   /Fd:%RUN%\BridgeTestHost.pdb
if errorlevel 1 exit /b 1

echo [build] OK - artifacts in %RUN%
dir /b "%RUN%"
endlocal
