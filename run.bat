@echo off
REM ============================================================
REM  potatoAgent - run
REM  1. build (calls build.bat)
REM  2. start dist\GUI.exe
REM  Double click this file.
REM ============================================================
setlocal
title potatoAgent - run
cd /d "%~dp0"

echo ============================================================
echo  potatoAgent  ^|  run
echo ============================================================
echo.

REM ---------- Step 1/2: build (build.bat skips its own pause) ----------
echo [1/2] Building first ...
echo.
set "POTATO_BUILD_CHILD=1"
call "%~dp0build.bat"
if errorlevel 1 goto :failed
echo.

REM ---------- Step 2/2: launch ----------
echo [2/2] Starting dist\GUI.exe ...
if not exist "dist\GUI.exe" goto :missing
start "" "%~dp0dist\GUI.exe"
echo       started.
echo.
echo RUN OK
echo.

pause
exit /b 0

:missing
echo [ERROR] dist\GUI.exe was NOT created, cannot start.
echo.

:failed
echo RUN FAILED
echo.
pause
exit /b 1
