@echo off
SET unigetuipath=%~dp0..\unigetui.exe
echo This script will remove scoop from your machine.
echo Removing Scoop implies removing all Scoop installed packages, buckets and preferences, and might also erase valuable user data related to the affected packages
echo|set/p="Press <ENTER> to continue or CLOSE this window to abort this process"&runas/u: "">NUL
cls
echo|set/p="ALL YOUR SCOOP PACKAGES WILL BE PERMANENTLY DELETED. Press <ENTER> to continue or CLOSE this window to abort this process."&runas/u: "">NUL
cls
echo.
echo Uninstalling scoop...
powershell -ExecutionPolicy ByPass -Command "scoop uninstall -p scoop"
if %errorlevel% equ 0 (
    echo UniGetUI will be restarted to continue.
    pause
    taskkill /im unigetui.exe /f
    start /b "%unigetuipath%" /i
) else (
    pause
    taskkill /im unigetui.exe /f
    start /b "%unigetuipath%" /i
)
