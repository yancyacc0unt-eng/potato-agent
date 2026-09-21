@echo off
REM ============================================================
REM  potatoAgent - smoke test  (double click this file)
REM
REM  Builds and runs every self test in the repo:
REM    1. build the projects and every self test project
REM    2. run CoreSelfTest       (brain: SSE / tool loop / errors / config)
REM    3. run Win32SelfTest      (real keyboard + mouse against its OWN notepad)
REM    4. run WorkspacesSelfTest (workspace store: paths, recent list, bad file)
REM    5. run SessionsSelfTest   (SQLite session store, temp database only)
REM    6. run FilesSelfTest      (file tools, temp folder only, delete -> recycle bin)
REM    7. run ShellSelfTest      (pc_shell: timeout / kill tree / output cap / UTF-8)
REM    8. run WebSelfTest        (web_search HTML parsers, offline fixtures)
REM    9. print a summary, then pause
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

REM ---------- Step 1/9: build ----------
echo [1/9] Building the projects and every self test project ...
echo.
call :build build\Core.csproj Core
if errorlevel 1 goto :failed
call :build build\Win32.csproj Win32
if errorlevel 1 goto :failed
call :build build\CoreSelfTest.csproj CoreSelfTest
if errorlevel 1 goto :failed
call :build build\WorkspacesSelfTest.csproj WorkspacesSelfTest
if errorlevel 1 goto :failed
call :build build\SessionsSelfTest.csproj SessionsSelfTest
if errorlevel 1 goto :failed
call :build build\FilesSelfTest.csproj FilesSelfTest
if errorlevel 1 goto :failed
call :build build\ShellSelfTest.csproj ShellSelfTest
if errorlevel 1 goto :failed
call :build build\WebSelfTest.csproj WebSelfTest
if errorlevel 1 goto :failed
echo.

REM ---------- Step 2/9: brain layer self test ----------
echo [2/9] Running CoreSelfTest (brain layer, no real network) ...
echo.
dotnet run --project build\CoreSelfTest.csproj -c Debug
set "CORE_RC=%ERRORLEVEL%"
echo.
if "%CORE_RC%"=="0" (echo       RESULT: CoreSelfTest  ....  PASS) else (echo       RESULT: CoreSelfTest  ....  FAIL  ^(exit code %CORE_RC%^))
echo.

REM ---------- Step 3/9: Win32 real machine self test ----------
echo [3/9] Running Win32SelfTest (real keyboard and mouse) ...
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

REM ---------- Step 4/9: workspace store self test ----------
echo [4/9] Running WorkspacesSelfTest (workspace store, no GUI, no network) ...
echo.
dotnet run --project build\WorkspacesSelfTest.csproj -c Debug
set "WS_RC=%ERRORLEVEL%"
echo.
if "%WS_RC%"=="0" (echo       RESULT: WorkspacesSelfTest  ....  PASS) else (echo       RESULT: WorkspacesSelfTest  ....  FAIL  ^(exit code %WS_RC%^))
echo.

REM ---------- Step 5/9: session store self test ----------
echo [5/9] Running SessionsSelfTest (SQLite session store, temp db) ...
echo.
dotnet run --project build\SessionsSelfTest.csproj -c Debug
set "SESS_RC=%ERRORLEVEL%"
echo.
if "%SESS_RC%"=="0" (echo       RESULT: SessionsSelfTest  ...  PASS) else (echo       RESULT: SessionsSelfTest  ...  FAIL  ^(exit code %SESS_RC%^))
echo.

REM ---------- Step 6/9: file tools self test ----------
echo [6/9] Running FilesSelfTest (file tools, temp folder only) ...
echo.
dotnet run --project build\FilesSelfTest.csproj -c Debug
set "FILES_RC=%ERRORLEVEL%"
echo.
if "%FILES_RC%"=="0" (echo       RESULT: FilesSelfTest  ....  PASS) else (echo       RESULT: FilesSelfTest  ....  FAIL  ^(exit code %FILES_RC%^))
echo.

REM ---------- Step 7/9: shell tool self test ----------
echo [7/9] Running ShellSelfTest (pc_shell: timeout, kill tree, output cap) ...
echo.
dotnet run --project build\ShellSelfTest.csproj -c Debug
set "SHELL_RC=%ERRORLEVEL%"
echo.
if "%SHELL_RC%"=="0" (echo       RESULT: ShellSelfTest  ....  PASS) else (echo       RESULT: ShellSelfTest  ....  FAIL  ^(exit code %SHELL_RC%^))
echo.

REM ---------- Step 8/9: web search self test ----------
echo [8/9] Running WebSelfTest (web_search parsers, offline fixtures) ...
echo.
dotnet run --project build\WebSelfTest.csproj -c Debug
set "WEB_RC=%ERRORLEVEL%"
echo.
if "%WEB_RC%"=="0" (echo       RESULT: WebSelfTest  ......  PASS) else (echo       RESULT: WebSelfTest  ......  FAIL  ^(exit code %WEB_RC%^))
echo.

REM ---------- Step 9/9: summary ----------
echo [9/9] Summary
echo.
echo       CoreSelfTest       (brain: SSE / tool loop / errors / config)
echo       Win32SelfTest      (real Notepad: focus / type / click / gate)
echo       WorkspacesSelfTest (workspace store: paths / recent / bad file)
echo       SessionsSelfTest   (SQLite session store)
echo       FilesSelfTest      (file tools: read / write / copy / move / recycle bin)
echo       ShellSelfTest      (pc_shell: timeout / kill tree / output cap)
echo       WebSelfTest        (web_search: DDG + Bing parsers, offline)
echo.

set "FAILED=0"
if not "%CORE_RC%"=="0" set "FAILED=1"
if not "%WIN32_RC%"=="0" set "FAILED=1"
if not "%WS_RC%"=="0" set "FAILED=1"
if not "%SESS_RC%"=="0" set "FAILED=1"
if not "%FILES_RC%"=="0" set "FAILED=1"
if not "%SHELL_RC%"=="0" set "FAILED=1"
if not "%WEB_RC%"=="0" set "FAILED=1"
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
if not "%WS_RC%"=="" if not "%WS_RC%"=="0" echo       - WorkspacesSelfTest FAILED ^(exit code %WS_RC%^)
if not "%SESS_RC%"=="" if not "%SESS_RC%"=="0" echo       - SessionsSelfTest   FAILED ^(exit code %SESS_RC%^)
if not "%FILES_RC%"=="" if not "%FILES_RC%"=="0" echo       - FilesSelfTest      FAILED ^(exit code %FILES_RC%^)
if not "%SHELL_RC%"=="" if not "%SHELL_RC%"=="0" echo       - ShellSelfTest      FAILED ^(exit code %SHELL_RC%^)
if not "%WEB_RC%"=="" if not "%WEB_RC%"=="0" echo       - WebSelfTest        FAILED ^(exit code %WEB_RC%^)
if "%CORE_RC%%WIN32_RC%%WS_RC%%SESS_RC%%FILES_RC%%SHELL_RC%%WEB_RC%"=="0000000" echo       - A build step failed, see the messages above.
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
