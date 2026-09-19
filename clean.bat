@echo off
REM ============================================================
REM  potatoAgent - clean
REM  Removes build\bin, build\obj and dist.
REM  Double click this file, or call it from build.bat.
REM ============================================================
setlocal
title potatoAgent - clean
cd /d "%~dp0"

echo ============================================================
echo  potatoAgent  ^|  clean
echo ============================================================
echo.

echo [1/3] Removing build\bin ...
if exist "build\bin" (
    rd /s /q "build\bin"
    echo       done.
) else (
    echo       not found, skipped.
)

echo [2/3] Removing build\obj ...
if exist "build\obj" (
    rd /s /q "build\obj"
    echo       done.
) else (
    echo       not found, skipped.
)

echo [3/3] Removing dist ...
if exist "dist" (
    rd /s /q "dist"
    echo       done.
) else (
    echo       not found, skipped.
)

echo.
echo CLEAN OK
echo.

REM When called by build.bat we must not pause, the parent owns the pause.
if not defined POTATO_CLEAN_CHILD pause
exit /b 0
