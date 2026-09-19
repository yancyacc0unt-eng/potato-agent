@echo off
REM ============================================================
REM  potatoAgent - smoke test  (double click this file)
REM
REM  Builds and runs every self test in the repo:
REM    1. build Core.csproj + Win32.csproj + CoreSelfTest.csproj
REM    2. run CoreSelfTest    (brain: SSE / tool loop / errors / config)
REM    3. run Win32SelfTest   (real keyboard + mouse against its OWN notepad)
REM    4. print a summary, then pause
REM
REM  Exit code: 0 = everything passed, 1 = something failed.
REM  NOTE: comments and messages are ASCII English on purpose -
REM        a .bat carrying Chinese text garbles on a default console.
REM ============================================================
setlocal
title potatoAgent - smoke test
cd /d "%~dp0"

REM The self tests print Chinese, so the console needs to be UTF-8.
chcp 65001 >nul 2>&1

echo ============================================================
echo  potatoAgent  ^|  smoke test
echo ============================================================
echo.

where dotnet >nul 2>&1
if errorlevel 1 goto :nodotnet

REM ---------- Step 1/4: build ----------
echo [1/4] Building Core, Win32 and CoreSelfTest ...
echo.
call :build build\CoreSelfTest.csproj CoreSelfTest
if errorlevel 1 goto :failed
call :build build\Win32.csproj Win32
if errorlevel 1 goto :failed
call :build build\CoreSelfTest.csproj CoreSelfTest
if errorlevel 1 goto :failed
echo.

REM ---------- Step 2/4: brain layer self test ----------
echo [2/4] Running CoreSelfTest (brain layer, no real network) ...
echo.
dotnet run --project build\CoreSelfTest.csproj -c Debug
set "CORE_RC=%ERRORLEVEL%"
echo.
if "%CORE_RC%"=="0" (echo       RESULT: CoreSelfTest  ....  PASS) else (echo       RESULT: CoreSelfTest  ....  FAIL  ^(exit code %CORE_RC%^))
echo.

REM ---------- Step 3/4: Win32 real machine self test ----------
echo [3/4] Running Win32SelfTest (real keyboard and mouse) ...
echo.
echo       NOTE: a few Notepad windows will flash on screen.
echo             That is NORMAL. The test types into its OWN Notepad
echo             and closes it again when it is done.
echo.
dotnet run --project build\Win32SelfTest.csproj -c Debug
set "WIN32_RC=%ERRORLEVEL%"
echo.
if "%WIN32_RC%"=="0" (echo       RESULT: Win32SelfTest  ...  PASS) else (echo       RESULT: Win32SelfTest  ...  FAIL  ^(exit code %WIN32_RC%^))
echo.

REM ---------- Step 4/4: summary ----------
echo [4/4] Summary
echo.
echo       CoreSelfTest   (brain: SSE / tool loop / errors / config)
echo       Win32SelfTest  (real Notepad: focus / type / click / gate)
echo.

set "FAILED=0"
if not "%CORE_RC%"=="0" set "FAILED=1"
if not "%WIN32_RC%"=="0" set "FAILED=1"
if "%FAILED%"=="1" goto :failed

echo  ############################################################
echo  ##                                                        ##
echo  ##              ALL   SELF   TESTS   PASSED               ##
echo  ##                                                        ##
echo  ############################################################
echo.
pause
exit /b 0

:nodotnet
echo  ############################################################
echo  ##                                                        ##
echo  ##                SMOKE   TEST   FAILED                   ##
echo  ##                                                        ##
echo  ############################################################
echo.
echo       [ERROR] "dotnet" was not found on PATH.
echo               Install the .NET SDK 10 and try again.
echo.
pause
exit /b 2

:failed
echo  ############################################################
echo  ##                                                        ##
echo  ##                SMOKE   TEST   FAILED                   ##
echo  ##                                                        ##
echo  ############################################################
echo.
if not "%CORE_RC%"=="" if not "%CORE_RC%"=="0" echo       - CoreSelfTest  FAILED ^(exit code %CORE_RC%^)
if not "%WIN32_RC%"=="" if not "%WIN32_RC%"=="0" echo       - Win32SelfTest FAILED ^(exit code %WIN32_RC%^)
if "%CORE_RC%%WIN32_RC%"=="00" echo       - A build step failed, see the messages above.
echo.
pause
exit /b 1

REM ---------- helper: build one project, report warnings / errors ----------
:build
echo       dotnet build %~1 -c Debug
set "OUT=%TEMP%\potato-smoke-build.txt"
dotnet build %~1 -c Debug > "%OUT%" 2>&1
set "RC=%ERRORLEVEL%"
REM dotnet prints its own warning / error counts at the end of the log, so just
REM echo the whole log - that keeps the real numbers visible without parsing
REM them out of a UTF-16 file. The pass / fail judgement is the exit code only.
type "%OUT%"
del "%OUT%" >nul 2>&1
if not "%RC%"=="0" (
    echo       %~2  ....  BUILD FAILED
    exit /b 1
)
echo       %~2  ....  build ok
exit /b 0
