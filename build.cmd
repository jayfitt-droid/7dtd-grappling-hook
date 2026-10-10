@echo off
rem Builds the mod DLL into build\ and writes build.log next to this script.
cd /d "%~dp0"
dotnet build src\GrapplingHook\GrapplingHook.csproj -c Release -nologo > build.log 2>&1
echo Exit code %ERRORLEVEL% >> build.log
