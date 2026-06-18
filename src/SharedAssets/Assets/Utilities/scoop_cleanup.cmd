@echo off
echo This script will clean the Scoop cache and run the cleanup command.
echo|set/p="Press <ENTER> to continue or CLOSE this window to abort this process"&runas/u: "">NUL
cls
echo Cleaning Scoop cache...
call scoop cleanup --all
call scoop cleanup --all --global
call scoop cache rm --all
call scoop cache rm --all --global
echo Done!
pause
exit
