@echo off
setlocal

set "CSPROJ=%~dp0..\src\DotNetDumpExtension\DotNetDumpExtension.csproj"
set "PS1=%~dp0update-csproj.ps1"

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" -CsprojPath "%CSPROJ%"

if %ERRORLEVEL% neq 0 (
    echo ERROR: update-csproj.ps1 failed.
    exit /b %ERRORLEVEL%
)

endlocal
