@echo off
setlocal
pushd "%~dp0"

rem NativeAOT publish invokes vswhere.exe unqualified to locate the MSVC linker.
rem Ensure the standard vswhere install folder is on PATH for this process.
set "VSWHERE_DIR=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer"
if exist "%VSWHERE_DIR%\vswhere.exe" set "PATH=%VSWHERE_DIR%;%PATH%"

dotnet publish LcdHub.csproj /p:PublishProfile=AOT
set EXITCODE=%ERRORLEVEL%

if %EXITCODE% EQU 0 (
    echo.
    echo Published to: %~dp0bin\Publish\AOT\LcdHub.exe
)

popd
endlocal & exit /b %EXITCODE%
