@echo off
setlocal

call "%~dp0update-csproj.bat"
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%

dotnet build "%~dp0..\src\DotNetDumpExtension.sln" -c Release
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%

endlocal
