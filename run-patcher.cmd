@echo off
rem Wrapper to run AIOverhaulPatcher via dotnet and forward application args.
rem Place this file next to the repo root and point Synthesis to call it.

set PROJ_PATH=%~dp0AIOverhaulPatcher\AIOverhaulPatcher.csproj
set DOTNET_PATH=C:\Program Files\dotnet\dotnet.exe

"%DOTNET_PATH%" run --project "%PROJ_PATH%" --runtime win-x64 -c Release --no-build -- %*
exit /b %ERRORLEVEL%
