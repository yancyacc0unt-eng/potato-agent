@echo off
REM ============================================================
REM  potatoAgent - build
REM  1. clean previous output (calls clean.bat)
REM  2. dotnet publish GUI as a single file framework dependent exe
REM  3. check dist\GUI.exe
REM  Double click this file, or call it from run.bat.
REM ============================================================
setlocal
title potatoAgent - build
cd /d "%~dp0"

echo ============================================================
echo  potatoAgent  ^|  build
echo ============================================================
echo.

REM ---------- Step 1/3: clean ----------
echo [1/3] Cleaning build\bin, build\obj and dist ...
echo.
set "POTATO_CLEAN_CHILD=1"
call "%~dp0clean.bat"
if errorlevel 1 goto :failed
echo.

REM ---------- Step 2/3: publish ----------
echo [2/3] Publishing GUI ...
echo       dotnet publish build\GUI.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
echo.
dotnet publish build\GUI.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
if errorlevel 1 goto :failed
echo.

REM ---------- Step 3/3: verify ----------
echo [3/3] Checking output ...
if not exist "dist\GUI.exe" goto :missing
for %%F in ("dist\GUI.exe") do echo       dist\GUI.exe  %%~zF bytes
echo.
echo BUILD OK
echo Output: %~dp0dist\GUI.exe
echo.
REM When called by run.bat we must not pause, the parent owns the pause.
if not defined POTATO_BUILD_CHILD pause
exit /b 0

:missing
echo [ERROR] dist\GUI.exe was NOT created.
echo.

:failed
echo BUILD FAILED
echo.
if not defined POTATO_BUILD_CHILD pause
exit /b 1
