@echo off
setlocal
rem Rebuilds the Release publish output the desktop launcher shortcut runs. The launcher only
rem auto-builds this once, the first time it can't find it, so run this manually after code changes
rem to push a new version into the one-click shortcut. dotnet publish (not build) is required here:
rem only publish actually copies wwwroot into the output folder, which the app needs at runtime.
rem
rem Publishes into publish-staged, not publish: the running app keeps publish's files locked. The
rem launcher swaps publish-staged in on its next start (see ApplyStagedWebBuild), so the app can stay
rem open while this runs and only needs a restart to pick up the new version. The .complete marker
rem is written last, so a half-finished or failed publish is never swapped in.

cd /d "%~dp0"

set "WEB_OUT=src\StoryForge.Web\bin\Release\net8.0"

echo === Publishing StoryForge.Web (Release) ===
if exist "%WEB_OUT%\publish-staged" rmdir /s /q "%WEB_OUT%\publish-staged"
dotnet publish "src\StoryForge.Web\StoryForge.Web.csproj" -c Release -o "%WEB_OUT%\publish-staged"
if errorlevel 1 goto :error
echo.> "%WEB_OUT%\publish-staged\.complete"

echo.
tasklist /fi "imagename eq StoryForge.Launcher.exe" 2>NUL | "%SystemRoot%\System32\findstr.exe" /I "StoryForge.Launcher.exe" >NUL
if errorlevel 1 goto :build_launcher

echo === StoryForge is running: skipped rebuilding StoryForge.Launcher ===
echo     Its exe is locked while the app is open. Only matters if the Launcher's own code changed;
echo     if so, close StoryForge and run this again.
goto :done

:build_launcher
echo === Building StoryForge.Launcher (Release) ===
dotnet build "src\StoryForge.Launcher\StoryForge.Launcher.csproj" -c Release
if errorlevel 1 goto :error

:done
echo.
echo Done. Restart StoryForge to open the latest version.
pause
exit /b 0

:error
echo.
echo Failed - scroll up for the error.
pause
exit /b 1
