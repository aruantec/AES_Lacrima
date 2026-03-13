@echo off
setlocal

set SCRIPT_DIR=%~dp0
dotnet run --project "%SCRIPT_DIR%build\_build.csproj" -- %*
exit /b %ERRORLEVEL%
