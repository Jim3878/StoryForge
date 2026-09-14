@echo off
setlocal
rem Rebuilds the Release publish output the desktop launcher shortcut runs. The launcher only
rem auto-builds this once, the first time it can't find it, so run this manually after code changes
rem to push a new version into the one-click shortcut. dotnet publish (not build) is required here:
rem only publish actually copies wwwroot into the output folder, which the app needs at runtime.

cd /d "%~dp0"

echo === Publishing StoryForge.Web (Release) ===
dotnet publish "src\StoryForge.Web\StoryForge.Web.csproj" -c Release -o "src\StoryForge.Web\bin\Release\net8.0\publish"
if errorlevel 1 goto :error

echo.
echo === Building StoryForge.Launcher (Release) ===
dotnet build "src\StoryForge.Launcher\StoryForge.Launcher.csproj" -c Release
if errorlevel 1 goto :error

echo.
echo Done. The desktop shortcut will now open the latest version.
pause
exit /b 0

:error
echo.
echo Failed - scroll up for the error.
pause
exit /b 1
