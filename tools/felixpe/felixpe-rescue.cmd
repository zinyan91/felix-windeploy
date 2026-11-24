@echo off
setlocal EnableExtensions DisableDelayedExpansion
title Felix Boot Rescue
color 0B

:menu
cls
echo ========================================================================
echo                     FELIX BOOT RESCUE  (UEFI x64)
echo ========================================================================
echo.
echo   [1] Fix Windows Boot (UEFI / Repair BCD / 0xc000000e)
echo   [2] Resume Felix WinDeploy session
echo   [3] Unlock BitLocker volume (48-digit recovery key)
echo   [4] Load storage drivers from \FelixBootRescue\Drivers
echo   [5] Launch Felix WinDeploy
echo   [6] Open Command Prompt (CMD)
echo   [7] Restart computer
echo.
set "CHOICE="
set /p "CHOICE=Select option [1-7]: "
if "%CHOICE%"=="1" goto fix_boot
if "%CHOICE%"=="2" goto resume_setup
if "%CHOICE%"=="3" goto unlock_bitlocker
if "%CHOICE%"=="4" goto load_drivers
if "%CHOICE%"=="5" goto launch_setup
if "%CHOICE%"=="6" goto open_cmd
if "%CHOICE%"=="7" goto reboot_pc
goto menu

:fix_boot
cls
echo Scanning drives for Windows installation...
set "WIN_DRV="
for %%D in (C D E F G H I J K L M N O P Q R S T U V W Z) do (
    if exist "%%D:\Windows\System32\bcdboot.exe" if exist "%%D:\Windows\System32\winload.efi" (
        set "WIN_DRV=%%D:"
        echo   Found Windows: %%D:\Windows
        goto :found_win
    )
)
:found_win
if not defined WIN_DRV (
    echo [ERROR] No Windows installation found on any accessible drive.
    pause
    goto menu
)
set "ESP_DRV="
for %%S in (C D E F G H I J K L M N O P Q R S T U V W Z) do (
    if exist "%%S:\EFI\Microsoft\Boot\BCD" (
        set "ESP_DRV=%%S:"
        goto :found_esp
    )
)
echo Scanning for EFI partition to mount as S:...
if not exist "%TEMP%\mount_esp.txt" (
    > "%TEMP%\scan_esp.txt" echo list vol
    diskpart /s "%TEMP%\scan_esp.txt"
)
set /p "VOL_NUM=Enter EFI FAT32 Volume number to assign S: (or press Enter if already mounted): "
if defined VOL_NUM (
    > "%TEMP%\mount_esp.txt" (
        echo select volume %VOL_NUM%
        echo assign letter=S noerr
    )
    diskpart /s "%TEMP%\mount_esp.txt" >nul 2>&1
    if exist "S:\" set "ESP_DRV=S:"
)
:found_esp
if not defined ESP_DRV (
    set /p "ESP_DRV=Enter EFI drive letter (e.g. S:): "
)
if not defined ESP_DRV goto menu
echo Backing up existing BCD to \FelixBootRescue\Backup...
for %%U in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
    if exist "%%U:\FelixBootRescue\" (
        if exist "%ESP_DRV%\EFI\Microsoft\Boot\BCD" copy /y "%ESP_DRV%\EFI\Microsoft\Boot\BCD" "%%U:\FelixBootRescue\Backup\BCD.bak" >nul 2>&1
    )
)
echo Running %WIN_DRV%\Windows\System32\bcdboot.exe %WIN_DRV%\Windows /s %ESP_DRV% /f UEFI ...
"%WIN_DRV%\Windows\System32\bcdboot.exe" "%WIN_DRV%\Windows" /s "%ESP_DRV%" /f UEFI
echo Exit code: %errorlevel%
pause
goto menu

:resume_setup
cls
for %%D in (C D E F G H I J K L M N O P Q R S T U V W Z) do (
    if exist "%%D:\FelixWindowSetup\session.json" (
        echo Resuming %%D:\FelixWindowSetup\session.json...
        if exist "%%D:\FelixWindowSetup\app\WindowSetup.exe" (
            start "" /wait "%%D:\FelixWindowSetup\app\WindowSetup.exe" --resume "%%D:\FelixWindowSetup\session.json"
            goto menu
        )
    )
)
echo No pending FelixWindowSetup session found.
pause
goto menu

:unlock_bitlocker
cls
set /p "BL_DRV=Drive letter to unlock (e.g. C:): "
set /p "BL_KEY=Enter 48-digit recovery password: "
if defined BL_DRV if defined BL_KEY manage-bde -unlock %BL_DRV% -recoverypassword %BL_KEY%
pause
goto menu

:load_drivers
cls
for %%U in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
    if exist "%%U:\FelixBootRescue\Drivers\" (
        echo Scanning %%U:\FelixBootRescue\Drivers...
        for /r "%%U:\FelixBootRescue\Drivers" %%F in (*.inf) do (
            echo   Loading %%~nxF
            drvload "%%~fF"
        )
    )
)
pause
goto menu

:launch_setup
for %%U in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
    if exist "%%U:\FelixBootRescue\app\WindowSetup.exe" (
        start "" "%%U:\FelixBootRescue\app\WindowSetup.exe"
        goto menu
    )
)
if exist "Y:\Apps\WindowSetup\WindowSetup.exe" start "" "Y:\Apps\WindowSetup\WindowSetup.exe"
goto menu

:open_cmd
start "Command Prompt" cmd.exe
goto menu

:reboot_pc
wpeutil reboot
goto menu
