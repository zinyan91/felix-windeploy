@echo off
rem FelixPE - lop .cmd mong nhat co the.
rem
rem Ly do ton tai: winpeshl chay rat som. Neu lop tren loi thi khong con gi bat
rem loi, va chu may nhan mot man hinh den. Luoi an toan roi ve cmd nam o day.
rem
rem KHONG dung PowerShell. Do that 12/10/2026: boot.wim cua ISO khong co
rem PowerShell, goi OC cua ADK khong ap duoc vi lech muc va (0x800f081e), va
rem Anhdv Boot cung khong co - ho dung PECMD.
rem
rem KHONG dat `exit /b` o bat cu dau truoc dong cmd.exe cuoi: mot `exit /b` sai
rem cho se nuot mat luoi an toan ma moi test khac van xanh.

title FelixPE: wpeinit
wpeinit
if exist "%SystemRoot%\System32\felixpe-profile.cmd" call "%SystemRoot%\System32\felixpe-profile.cmd"

rem Cua so nay AN tu dau (winpeshl -> felixpe-an cho). Giu Shift luc boot thi felixpe-an
rem hien cua so va dat FELIXPE_CMD=1: vao thang dau nhac lenh.
if "%FELIXPE_CMD%"=="1" goto :fallback

:shell
title FelixPE: dang goi explorer
echo [FelixPE] Khoi dong desktop...

rem LUON kiem file ton tai truoc khi `start`. Thieu file thi `start` bat HOP
rem THOAI LOI va chan ca chuoi boot; may dung im, nhin qua tuong treo. Do that
rem 12/10/2026 voi sihost.exe - thu ma Anhdv cung khong co, toi tu them theo
rem suy doan.
if not exist "%SYSTEMDRIVE%\Windows\explorer.exe" (
    echo [FelixPE] KHONG co explorer.exe.
    goto :fallback
)

rem Ung dung nam o \Apps tren USB, ky tu o thi khong doan truoc duoc. Shortcut
rem tao luc dung tro vao Y:, nen o day gan o nao co \Apps\apps.json thanh Y:.
if not exist Y:\Apps\apps.json (
    for %%d in (C D E F G H I J K L M N O P Q R S T U V W Z) do (
        if exist %%d:\Apps\apps.json if not exist Y:\Apps\apps.json subst Y: %%d:\
    )
)
rem Menu khoi dong (Toolkit) dat ung dung o <o>:\FelixPE\Apps.
if not exist Y:\Apps\apps.json (
    for %%d in (C D E F G H I J K L M N O P Q R S T U V W Z) do (
        if exist %%d:\FelixPE\Apps\apps.json if not exist Y:\Apps\apps.json subst Y: %%d:\FelixPE
    )
)
rem Phan vung Boot tren o cung mang thuoc tinh GPT "khong gan ky tu" nen PE cung
rem khong gan: gan thu tung volume vao Y:, khong phai thi go ra.
if not exist Y:\Apps\apps.json (
    for /f %%v in ('mountvol ^| find "Volume{"') do (
        if not exist Y:\Apps\apps.json (
            mountvol Y: %%v >nul 2>&1
            if not exist Y:\Apps\apps.json mountvol Y: /d >nul 2>&1
        )
    )
)
if exist Y:\Apps\apps.json (echo [FelixPE] Ung dung: Y:\Apps) else (echo [FelixPE] Khong thay o \Apps - shortcut ung dung se khong mo duoc.)

rem Tu dong tiep tuc phien cai Windows hoac sao luu duoc chuan bi tu Desktop (Felix WinDeploy).
if not defined FELIX_WS_LAUNCHED (
    for %%d in (C D E F G H I J K L M N O P Q R S T U V W Z) do (
        if not defined FELIX_WS_LAUNCHED if exist "%%d:\FelixWindowSetup\backup-session.json" (
            if exist "%%d:\FelixWindowSetup\app\WindowSetup.exe" (
                set "FELIX_WS_LAUNCHED=1"
                start "" "%%d:\FelixWindowSetup\app\WindowSetup.exe" --resume-backup "%%d:\FelixWindowSetup\backup-session.json"
            ) else if exist "Y:\Apps\WindowSetup\WindowSetup.exe" (
                set "FELIX_WS_LAUNCHED=1"
                start "" "Y:\Apps\WindowSetup\WindowSetup.exe" --resume-backup "%%d:\FelixWindowSetup\backup-session.json"
            )
        )
        if not defined FELIX_WS_LAUNCHED if exist "%%d:\FelixWindowSetup\session.json" (
            if exist "%%d:\FelixWindowSetup\app\WindowSetup.exe" (
                set "FELIX_WS_LAUNCHED=1"
                start "" "%%d:\FelixWindowSetup\app\WindowSetup.exe" --resume "%%d:\FelixWindowSetup\session.json"
            ) else if exist "Y:\Apps\WindowSetup\WindowSetup.exe" (
                set "FELIX_WS_LAUNCHED=1"
                start "" "Y:\Apps\WindowSetup\WindowSetup.exe" --resume "%%d:\FelixWindowSetup\session.json"
            )
        )
    )
)

rem Nap driver LAN/Wi-Fi (SDI) CHAY NEN: quet driver mat hang chuc giay, khong duoc chan desktop.
rem Console rieng qua felixpe-an: SDI tu ShowWindow console cua no, chung console thi lo cua so boot.
if exist "Y:\Apps\Drivers\NET\nap-mang.cmd" if exist "%SYSTEMDRIVE%\Windows\System32\felixpe-an.exe" "%SYSTEMDRIVE%\Windows\System32\felixpe-an.exe" nen cmd.exe /c "Y:\Apps\Drivers\NET\nap-mang.cmd"
rem Muon driver touchpad/am thanh tu Windows cua may (chay nen, mot lan moi lan boot): may khong co chuot
rem van dung touchpad duoc. Chay lai bang tay qua shortcut sau khi mo khoa BitLocker o Windows.
if not defined FELIXPE_DA_MUON if exist "%SYSTEMDRIVE%\Windows\System32\felixpe-muon-driver.cmd" if exist "%SYSTEMDRIVE%\Windows\System32\felixpe-an.exe" (
    set "FELIXPE_DA_MUON=1"
    "%SYSTEMDRIVE%\Windows\System32\felixpe-an.exe" nen cmd.exe /c "%SYSTEMDRIVE%\Windows\System32\felixpe-muon-driver.cmd" nen
)

rem Phep do khong can cong cu ngoai: start /wait chan den khi Explorer thoat.
rem Doc TIEU DE cua so tren anh chup la biet ngay:
rem   con "cho explorer thoat"      -> Explorer DANG SONG, chi la khong ve gi
rem   doi thanh "explorer DA THOAT" -> Explorer CHET NGAY
rem Cua so nay an khi Explorer chay va hien lai khi Explorer thoat: luoi
rem an toan van o day, chi khong che desktop.
title FelixPE: cho explorer thoat
rem PE khong co Settings (ms-settings): an Display settings va Personalize thay vi de chung mo vao khoang trong.
rem Do phan giai do bootmgr dat luc boot (highestmode); khong co driver card man hinh thi PE khong doi duoc nua.
reg add "HKCR\DesktopBackground\Shell\Display" /v "ProgrammaticAccessOnly" /d "" /f >nul 2>&1
reg add "HKCR\DesktopBackground\Shell\Personalize" /v "ProgrammaticAccessOnly" /d "" /f >nul 2>&1
rem Explorer trong PE khong ve hinh nen; felixpe-hinhnen tu ve sau lop icon (§17).
if exist "%SYSTEMDRIVE%\Windows\System32\felixpe-hinhnen.exe" start "" "%SYSTEMDRIVE%\Windows\System32\felixpe-hinhnen.exe"
rem Explorer trong PE KHONG chay muc Run (do 13/10/2026). Voi -autorun, Open-Shell
rem tu cho cua so Progman cua Explorer toi 1 phut nen goi truoc Explorer duoc.
if exist "%SYSTEMDRIVE%\Program Files\Open-Shell\StartMenu.exe" start "" "%SYSTEMDRIVE%\Program Files\Open-Shell\StartMenu.exe" -autorun
rem Bang thong tin tu cho Progman cua Explorer; mutex giu dung mot ban qua moi vong lap.
if exist "%SYSTEMDRIVE%\Windows\System32\felixpe-thongtin.exe" start "" "%SYSTEMDRIVE%\Windows\System32\felixpe-thongtin.exe"
rem Explorer cua PE khong ve icon mang: felixpe-wifi ve icon trang thai o khay va mo cua so Ket noi Wi-Fi.
rem EnableAutoTray=0: hien moi icon khay, khong giau icon moi vao mui ten.
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer" /v EnableAutoTray /t REG_DWORD /d 0 /f >nul 2>&1
if exist "%SYSTEMDRIVE%\Windows\System32\felixpe-wifi.exe" start "" "%SYSTEMDRIVE%\Windows\System32\felixpe-wifi.exe" khay
rem `an` chay song song (/b: cung console) va tu cho thanh tac vu roi moi an cua so.
if exist "%SYSTEMDRIVE%\Windows\System32\felixpe-desktop.exe" start "" /b felixpe-desktop.exe an
start /wait "" "%SYSTEMDRIVE%\Windows\explorer.exe"
if exist "%SYSTEMDRIVE%\Windows\System32\felixpe-desktop.exe" felixpe-desktop.exe hien
title FelixPE: explorer DA THOAT
echo [FelixPE] Explorer da thoat.

:fallback
rem Vong lap nay la bat buoc. Khi file nay chay het, winpeshl het viec va WinPE
rem TU KHOI DONG LAI, mat het thu dang lam do. Go "exit" trong cmd phai quay ve
rem khoi dong lai desktop, khong duoc lam may reboot.
title FelixPE: dau nhac lenh
echo.
echo [FelixPE] Dau nhac lenh. Go "exit" de quay lai desktop.
cmd.exe
goto :shell
