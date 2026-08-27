@echo off
setlocal enabledelayedexpansion

title DiskCleaner 编译脚本

REM ============================================================
REM  DiskCleaner 编译脚本
REM  框架: .NET 8.0 WPF (net8.0-windows)
REM  输出: 依赖框架单文件（Framework-Dependent Single File）
REM  说明: 需要目标机器已安装 .NET 8 Desktop Runtime
REM ============================================================

REM ---- 变量配置 ----
set "PROJECT_ROOT=%~dp0"
set "PROJECT_NAME=DiskCleaner"
set "CSPROJ=%PROJECT_ROOT%%PROJECT_NAME%.csproj"
set "CONFIG=Release"
set "RUNTIME=win-x64"
set "PUBLISH_DIR=%PROJECT_ROOT%bin"
set "OUTPUT_EXE=%PUBLISH_DIR%\%PROJECT_NAME%.exe"

cd /d "%PROJECT_ROOT%"

echo.
echo ============================================================
echo   %PROJECT_NAME% 编译脚本
echo   框架: .NET 8.0 WPF
echo   模式: 依赖框架单文件
echo ============================================================
echo.

REM ---- 检查 dotnet SDK ----
echo [1/6] 检查 .NET SDK 环境...
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo.
    echo [错误] 未找到 dotnet 命令！
    echo        请安装 .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)
for /f "tokens=*" %%v in ('dotnet --version') do set "DOTNET_VER=%%v"
echo       .NET SDK 版本: %DOTNET_VER%
echo       OK
echo.

REM ---- 编译前清理临时文件 ----
echo [2/6] 编译前清理临时文件...
set "CLEAN_COUNT=0"

if exist "%PROJECT_ROOT%bin" (
    echo       删除 bin\
    rmdir /s /q "%PROJECT_ROOT%bin" 2>nul
    set /a CLEAN_COUNT+=1
)
if exist "%PROJECT_ROOT%obj" (
    echo       删除 obj\
    rmdir /s /q "%PROJECT_ROOT%obj" 2>nul
    set /a CLEAN_COUNT+=1
)
if exist "%PROJECT_ROOT%.vs" (
    echo       删除 .vs\
    rmdir /s /q "%PROJECT_ROOT%.vs" 2>nul
    set /a CLEAN_COUNT+=1
)
REM 清理日志文件
if exist "%PROJECT_ROOT%%PROJECT_NAME%.log" (
    del /q "%PROJECT_ROOT%%PROJECT_NAME%.log" 2>nul
    echo       删除 %PROJECT_NAME%.log
    set /a CLEAN_COUNT+=1
)
REM 清理 VS 临时文件
if exist "%PROJECT_ROOT%\TestResults" (
    rmdir /s /q "%PROJECT_ROOT%\TestResults" 2>nul
    echo       删除 TestResults\
    set /a CLEAN_COUNT+=1
)
echo       已清理 !CLEAN_COUNT! 项临时目录/文件
echo       OK
echo.

REM ---- 还原 NuGet 包 ----
echo [3/6] 还原 NuGet 依赖包...
dotnet restore "%CSPROJ%"
if errorlevel 1 (
    echo.
    echo [错误] NuGet 包还原失败！
    pause
    exit /b 1
)
echo       OK
echo.

REM ---- 编译发布（依赖框架单文件）----
echo [4/6] 编译发布（依赖框架单文件）...
echo       配置: %CONFIG%
echo       运行时: %RUNTIME%
echo       单文件: 是
echo       自包含: 否（依赖框架）
echo.
dotnet publish "%CSPROJ%" ^
    -c %CONFIG% ^
    -r %RUNTIME% ^
    --self-contained false ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=false ^
    /p:DebugType=none ^
    /p:DebugSymbols=false ^
    -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo.
    echo [错误] 编译失败！请检查上方错误信息。
    pause
    exit /b 1
)
echo.
echo       OK
echo.

REM ---- 编译后清理临时文件 ----
echo [5/6] 编译后清理临时文件...
set "POST_CLEAN=0"

if exist "%PROJECT_ROOT%obj" (
    rmdir /s /q "%PROJECT_ROOT%obj" 2>nul
    echo       删除 obj\
    set /a POST_CLEAN+=1
)
REM 删除 publish 中间目录 bin\Release（仅保留 -o bin 下的单文件 exe）
if exist "%PROJECT_ROOT%bin\Release" (
    rmdir /s /q "%PROJECT_ROOT%bin\Release" 2>nul
    echo       删除 bin\Release 中间目录
    set /a POST_CLEAN+=1
)
REM 清理 publish 目录中多余的文件（仅保留 exe 和 dll）
if exist "%PUBLISH_DIR%\*.pdb" (
    del /q "%PUBLISH_DIR%\*.pdb" 2>nul
    echo       删除 publish\*.pdb
    set /a POST_CLEAN+=1
)
if exist "%PUBLISH_DIR%\*.xml" (
    del /q "%PUBLISH_DIR%\*.xml" 2>nul
    echo       删除 publish\*.xml
    set /a POST_CLEAN+=1
)
if exist "%PUBLISH_DIR%\*.json" (
    del /q "%PUBLISH_DIR%\*.json" 2>nul
    echo       删除 publish\*.json
    set /a POST_CLEAN+=1
)
echo       已清理 !POST_CLEAN! 项临时文件
echo       OK
echo.

REM ---- 可选数字签名（消除安全软件对未知签名 EXE 的安装拦截/误报）----
echo [5.5/6] 可选数字签名（消除安全软件误报）...
if not defined SIGN_CERT_FILE (
    echo   未配置签名证书，跳过签名。
    echo   说明: 设置环境变量 SIGN_CERT_FILE（指向 .pfx）与 SIGN_CERT_PASSWORD 后重新发布，
    echo         可获取受信任代码签名，消除安全软件对“未知签名”程序的安装拦截与误报。
    echo   OK（跳过）
) else (
    set "SIGNTOOL="
    if exist "%ProgramFiles(x86)%\Windows Kits\10\bin\x64\signtool.exe" set "SIGNTOOL=%ProgramFiles(x86)%\Windows Kits\10\bin\x64\signtool.exe"
    if exist "%ProgramFiles%\Windows Kits\10\bin\x64\signtool.exe" set "SIGNTOOL=%ProgramFiles%\Windows Kits\10\bin\x64\signtool.exe"
    if not defined SIGNTOOL (
        echo   [警告] 未找到 signtool.exe（需安装 Windows SDK），跳过签名。
        echo   OK（跳过）
    ) else (
        "%SIGNTOOL%" sign /f "%SIGN_CERT_FILE%" /p "%SIGN_CERT_PASSWORD%" /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "%OUTPUT_EXE%"
        if errorlevel 1 (
            echo   [警告] 签名失败！请检查证书路径与密码（未签名仍可运行，但可能触发安全软件拦截）。
        ) else (
            echo   已用代码签名证书签名: %OUTPUT_EXE%
        )
    )
)
echo.

REM ---- 编译结果报告 ----
echo [6/6] 编译结果
echo ============================================================
if exist "%OUTPUT_EXE%" (
    for %%A in ("%OUTPUT_EXE%") do set "FILE_SIZE=%%~zA"
    set /a "FILE_MB=!FILE_SIZE! / 1048576"
    set /a "FILE_KB=!FILE_SIZE! / 1024"
    echo   编译成功！
    echo.
    echo   输出文件: %OUTPUT_EXE%
    echo   文件大小: !FILE_MB! MB ^(!FILE_KB! KB^)
    echo.
    echo   运行要求: 目标机器需安装 .NET 8 Desktop Runtime
    echo   下载地址: https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    echo   提示: 可直接复制 .exe 到目标机器运行
) else (
    echo   [警告] 未找到输出文件，请检查编译是否成功。
)
echo ============================================================
echo.

pause
endlocal
