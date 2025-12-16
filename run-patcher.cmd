@echo off
rem Wrapper to run AIOverhaulPatcher via dotnet and forward application args.
rem Place this file next to the repo root and point Synthesis to call it.








exit /b %ERRORLEVEL%"%DOTNET_PATH%" run --project "%PROJ_PATH%" --runtime win-x64 -c Release --no-build -- %*
:: Run dotnet with `--` to separate dotnet options from app args and forward all args (%*)
:: Keep `--no-build` to avoid rebuild in Synthesis runs.set PROJ_PATH=%~dp0AIOverhaulPatcher\AIOverhaulPatcher.csproj
:: Path to the project file, relative to this script location
:: (script assumed at repo root)set DOTNET_PATH=C:\Program Files\dotnet\dotnet.exe:: Path to dotnet.exe - adjust if needed