@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "configuration=Debug"
if not "%~2"=="" goto usage
if "%~1"=="" goto build
if /i "%~1"=="debug" goto build
if /i "%~1"=="release" (
    set "configuration=Release"
    goto build
)
goto usage

:build
call "%~dp0build.bat" %configuration%
if errorlevel 1 exit /b %errorlevel%

set "output_dir=%~dp0mySQLPunk\bin\%configuration%"
if not exist "%output_dir%\mySQLPunk.exe" (
    echo ERROR: Executable not found: "%output_dir%\mySQLPunk.exe" 1>&2
    exit /b 1
)

echo Starting mySQLPunk ^(%configuration%^) ...
start "" /d "%output_dir%" "%output_dir%\mySQLPunk.exe"
exit /b %errorlevel%

:usage
echo Usage: %~nx0 [debug^|release] 1>&2
echo Default: debug 1>&2
exit /b 2
