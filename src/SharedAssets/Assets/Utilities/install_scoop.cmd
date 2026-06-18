@echo off
SET unigetuipath=%~dp0..\wingetui.exe
set pwsh=C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe
echo Scoop Installer Assistant - UniGetUI
echo This script will install Scoop and its dependencies, since it appears that they are not installed on your machine.
echo|set/p="Press <ENTER> to continue or CLOSE this window to abort this process"&runas/u: "">NUL
cls
%pwsh% Set-ExecutionPolicy RemoteSigned -Scope CurrentUser
%pwsh% -ExecutionPolicy ByPass -File "%~dp0\install_scoop.ps1"
if %errorlevel% equ 0 (
    echo UniGetUI will be restarted to continue.
    pause
    taskkill /im wingetui.exe /f
    taskkill /im unigetui.exe /f
    start /b "%unigetuipath%" /i
) else (
    pause
    taskkill /im wingetui.exe /f
    taskkill /im unigetui.exe /f
    start /b "%unigetuipath%" /i
)
