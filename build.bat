@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "configuration=Debug"
if not "%~2"=="" goto usage
if "%~1"=="" goto resolve_msbuild
if /i "%~1"=="debug" goto resolve_msbuild
if /i "%~1"=="release" (
    set "configuration=Release"
    goto resolve_msbuild
)
goto usage

:resolve_msbuild
set "msbuild_path="
if defined MSBUILD_EXE (
    if not exist "%MSBUILD_EXE%" (
        echo ERROR: MSBUILD_EXE does not point to an existing file. 1>&2
        exit /b 1
    )
    set "msbuild_path=%MSBUILD_EXE%"
    goto build
)

for /f "delims=" %%I in ('where MSBuild.exe 2^>nul') do if not defined msbuild_path set "msbuild_path=%%I"
if defined msbuild_path goto build

set "vswhere_path=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%vswhere_path%" (
    for /f "usebackq delims=" %%I in (`"%vswhere_path%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do if not defined msbuild_path set "msbuild_path=%%I"
)
if defined msbuild_path goto build

if exist "C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe" (
    set "msbuild_path=C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    goto build
)

echo ERROR: MSBuild was not found. Install Visual Studio or Build Tools 2019 or newer 1>&2
echo with .NET desktop build tools and the .NET Framework 4.7.2 Developer Pack. 1>&2
echo You can also set MSBUILD_EXE to the full path of MSBuild.exe. 1>&2
exit /b 1

:build
pushd "%~dp0" || exit /b 1
echo Building mySQLPunk ^(%configuration%^) with "%msbuild_path%"
"%msbuild_path%" "mySQLPunk.sln" /restore /p:RestorePackagesConfig=true /p:Configuration=%configuration% /p:Platform="Any CPU" /verbosity:minimal /nologo
set "build_exit_code=%errorlevel%"
popd
if not "%build_exit_code%"=="0" exit /b %build_exit_code%

echo Build succeeded: "%~dp0mySQLPunk\bin\%configuration%\mySQLPunk.exe"
exit /b 0

:usage
echo Usage: %~nx0 [debug^|release] 1>&2
echo Default: debug 1>&2
exit /b 2
