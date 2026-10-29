@echo off
REM ===========================================================================
REM G2 download-closure harness build (x86 v143) - NOT part of CI.
REM Produces DownloadProbe.exe under %LOCALAPPDATA%\AutoShopNativeBridge-g2-run
REM and copies the vendor DLLs it loads at runtime (never modified).
REM Usage: g2build.cmd [run-dir]
REM ===========================================================================
setlocal
set ROOT=%~dp0..
if "%PLCMCP_G2_RUN%"=="" (
  if not "%~1"=="" (
    set PLCMCP_G2_RUN=%~1
  ) else (
    set PLCMCP_G2_RUN=%LOCALAPPDATA%\AutoShopNativeBridge-g2-run
  )
)
set RUN=%PLCMCP_G2_RUN%
if not exist "%RUN%" mkdir "%RUN%"

set VCVARS="C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat"
call %VCVARS% x86 >nul
if errorlevel 1 (
  echo [g2build] vcvarsall x86 failed
  exit /b 1
)

cl /nologo /W3 /EHsc /MT "%~dp0DownloadProbe.cpp" /Fe:%RUN%\DownloadProbe.exe /link ws2_32.lib
if errorlevel 1 exit /b 1

REM vendor DLL copies (read-only sources; the install dir is never written)
set INSTALL=D:\Inovance Control\AutoShop
copy /y "%INSTALL%\CommunicateEthernet.dll" "%RUN%\" >nul
copy /y "%INSTALL%\InterfaceMgr.dll" "%RUN%\" >nul
if not "%INSTALL%"=="%~dp0.." copy /y "%INSTALL%\mfc90u.dll" "%RUN%\" >nul 2>&1

set VC90DIR=C:\Windows\WinSxS\Fusion\x86_microsoft.vc90.crt_1fc8b3b9a1e18e3b_none_ea33c8f0b247cd77\9.0\9.0.30729.4974
set MFC90DIR=C:\Windows\WinSxS\Fusion\x86_microsoft.vc90.mfc_1fc8b3b9a1e18e3b_none_eaa49612b1f5b2ea\9.0\9.0.30729.4974
if not exist "%RUN%\mfc90u.dll" if exist "%MFC90DIR%\mfc90u.dll" copy /y "%MFC90DIR%\mfc90u.dll" "%RUN%\" >nul
if not exist "%RUN%\msvcp90.dll" if exist "%VC90DIR%\msvcp90.dll" copy /y "%VC90DIR%\msvcp90.dll" "%RUN%\" >nul
if not exist "%RUN%\msvcr90.dll" if exist "%VC90DIR%\msvcr90.dll" copy /y "%VC90DIR%\msvcr90.dll" "%RUN%\" >nul

del /q "%~dp0*.obj" >nul 2>&1
echo [g2build] OK - %RUN%\DownloadProbe.exe
endlocal
