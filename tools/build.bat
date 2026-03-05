@echo off
chcp 65001 >nul 2>&1
setlocal enabledelayedexpansion

:: ============================================================================
:: LyWaf 打包脚本 (Windows)
:: 编译为独立可执行程序，无需安装 .NET 运行时
:: ============================================================================
:: 用法:
::   build.bat                     编译当前平台 (win-x64)
::   build.bat win-x64             编译 Windows x64
::   build.bat win-arm64           编译 Windows ARM64
::   build.bat linux-x64           交叉编译 Linux x64
::   build.bat linux-arm64         交叉编译 Linux ARM64
::   build.bat osx-x64             交叉编译 macOS x64
::   build.bat osx-arm64           交叉编译 macOS ARM64
::   build.bat all                 编译所有平台
::   build.bat --trim win-x64      启用裁剪 (可减小体积，可能有兼容性问题)
:: ============================================================================

set "SCRIPT_DIR=%~dp0"
set "PROJECT_DIR=%SCRIPT_DIR%.."
set "PROJECT_NAME=LyWaf"
set "OUTPUT_BASE=%PROJECT_DIR%\publish"
set "FRONTEND_DIR=%PROJECT_DIR%\Frontend"
set "CONFIGURATION=Release"
set "ENABLE_TRIM=0"

echo =====================================
echo   LyWaf 打包工具
echo =====================================

:: ---- 检查 dotnet SDK ----
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] 未找到 dotnet SDK，请先安装: https://dotnet.microsoft.com/download
    exit /b 1
)

for /f "tokens=*" %%v in ('dotnet --version') do set "DOTNET_VERSION=%%v"
echo [INFO] dotnet SDK 版本: %DOTNET_VERSION%

:: ---- 解析参数 ----
set "TARGET="
:parse_args
if "%~1"=="" goto :args_done
if "%~1"=="--trim" (
    set "ENABLE_TRIM=1"
    shift
    goto :parse_args
)
set "TARGET=%~1"
shift
goto :parse_args
:args_done

if "%TARGET%"=="" (
    :: 自动检测 Windows 架构
    if "%PROCESSOR_ARCHITECTURE%"=="ARM64" (
        set "TARGET=win-arm64"
    ) else (
        set "TARGET=win-x64"
    )
    echo [INFO] 自动检测平台: !TARGET!
)

:: ---- 构建前端 ----
call :build_frontend

:: ---- 编译 ----
if "%TARGET%"=="all" (
    echo [INFO] 编译所有平台...
    for %%r in (linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64 osx-x64 osx-arm64 win-x64 win-arm64) do (
        call :publish_rid %%r
        if errorlevel 1 (
            echo [ERROR] 编译 %%r 失败
        )
    )
    echo.
    echo =====================================
    echo [OK] 所有平台编译完成!
    echo =====================================
    echo [INFO] 输出目录: %OUTPUT_BASE%
) else (
    call :publish_rid %TARGET%
    if errorlevel 1 (
        echo [ERROR] 编译失败
        exit /b 1
    )
    echo.
    echo =====================================
    echo [OK] 编译完成!
    echo =====================================
    echo [INFO] 输出目录: %OUTPUT_BASE%\%PROJECT_NAME%-%TARGET%
)

exit /b 0

:: ============================================================================
:: 子函数: 构建前端
:: ============================================================================
:build_frontend
if not exist "%FRONTEND_DIR%\package.json" (
    echo [WARN] 未找到前端目录，跳过前端构建
    exit /b 0
)

where npm >nul 2>&1
if errorlevel 1 (
    echo [WARN] 未找到 npm，跳过前端构建 (请手动构建前端或安装 Node.js)
    exit /b 0
)

echo [INFO] 正在构建前端...

pushd "%FRONTEND_DIR%"

if not exist "node_modules" (
    echo [INFO] 安装前端依赖...
    call npm install
    if errorlevel 1 (
        echo [WARN] 前端依赖安装失败
        popd
        exit /b 0
    )
)

call npm run build
if errorlevel 1 (
    echo [WARN] 前端构建失败
    popd
    exit /b 0
)

popd
echo [OK] 前端构建完成
exit /b 0

:: ============================================================================
:: 子函数: 发布单个目标平台
:: ============================================================================
:publish_rid
set "RID=%~1"
set "OUTPUT_DIR=%OUTPUT_BASE%\%PROJECT_NAME%-%RID%"

echo [INFO] 正在编译目标平台: %RID% ...

:: 清理旧的输出
if exist "%OUTPUT_DIR%" rd /s /q "%OUTPUT_DIR%"

set "TRIM_ARG="
if "%ENABLE_TRIM%"=="1" set "TRIM_ARG=-p:PublishTrimmed=true"

dotnet publish "%PROJECT_DIR%\%PROJECT_NAME%.csproj" ^
    -c %CONFIGURATION% ^
    -r %RID% ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    %TRIM_ARG% ^
    -o "%OUTPUT_DIR%"

if errorlevel 1 (
    echo [ERROR] 编译目标平台 %RID% 失败
    exit /b 1
)

:: 确保 datas 目录被拷贝（PublishSingleFile 模式下可能不会自动拷贝）
if exist "%PROJECT_DIR%\datas" if not exist "%OUTPUT_DIR%\datas" (
    mkdir "%OUTPUT_DIR%\datas"
    xcopy /s /e /y /q "%PROJECT_DIR%\datas\*" "%OUTPUT_DIR%\datas\" >nul 2>&1
    echo [INFO] 已拷贝 datas 目录
)

:: 清理 pdb 文件
del /q "%OUTPUT_DIR%\*.pdb" >nul 2>&1

:: 清理运行时状态文件
del /q "%OUTPUT_DIR%\.lywaf.*" >nul 2>&1
:: 清理 config.ly.* 但保留 config.ly 和 config.ly.example
for %%f in ("%OUTPUT_DIR%\config.ly.*") do (
    if /i not "%%~nxf"=="config.ly.example" if /i not "%%~nxf"=="config.ly" del /q "%%f" >nul 2>&1
)

:: 清理构建产物、临时目录
for %%d in (publish temp logs certs) do (
    if exist "%OUTPUT_DIR%\%%d" rd /s /q "%OUTPUT_DIR%\%%d"
)
:: 清理 bin* 开头的目录
for /d %%d in ("%OUTPUT_DIR%\bin*") do rd /s /q "%%d" >nul 2>&1

:: 清理测试框架残留
for %%d in (CodeCoverage alpine arm64 macos ubuntu x64 x86 cs de es fr it ja ko pl pt-BR ru tr zh-Hans zh-Hant tests Frontend wwwroot myLyWafbintest_build) do (
    if exist "%OUTPUT_DIR%\%%d" rd /s /q "%OUTPUT_DIR%\%%d"
)
del /q "%OUTPUT_DIR%\Microsoft.CodeCoverage.*" >nul 2>&1
del /q "%OUTPUT_DIR%\Microsoft.DiaSymReader.*" >nul 2>&1
del /q "%OUTPUT_DIR%\Microsoft.VisualStudio.*" >nul 2>&1
del /q "%OUTPUT_DIR%\Microsoft.TestPlatform.*" >nul 2>&1
del /q "%OUTPUT_DIR%\Mono.Cecil*" >nul 2>&1
del /q "%OUTPUT_DIR%\testhost.*" >nul 2>&1
del /q "%OUTPUT_DIR%\xunit.*" >nul 2>&1
del /q "%OUTPUT_DIR%\ThirdPartyNotices.txt" >nul 2>&1
del /q "%OUTPUT_DIR%\System.Memory.dll" >nul 2>&1
del /q "%OUTPUT_DIR%\System.Text.Json.dll" >nul 2>&1
del /q "%OUTPUT_DIR%\System.IO.Hashing.dll" >nul 2>&1
del /q "%OUTPUT_DIR%\web.config" >nul 2>&1
del /q "%OUTPUT_DIR%\*.staticwebassets.endpoints.json" >nul 2>&1
del /q "%OUTPUT_DIR%\appsettings.Development.json" >nul 2>&1
del /q "%OUTPUT_DIR%\*.deps.json" >nul 2>&1
del /q "%OUTPUT_DIR%\*.runtimeconfig.json" >nul 2>&1
del /q "%OUTPUT_DIR%\*.staticwebassets.runtime.json" >nul 2>&1
del /q "%OUTPUT_DIR%\appsettings.yaml" >nul 2>&1

echo [OK] 编译完成: %RID% -^> %OUTPUT_DIR%

:: 打包为 zip
where powershell >nul 2>&1
if not errorlevel 1 (
    set "ARCHIVE=%OUTPUT_BASE%\%PROJECT_NAME%-%RID%.zip"
    if exist "!ARCHIVE!" del /q "!ARCHIVE!"
    powershell -NoProfile -Command "Compress-Archive -Path '%OUTPUT_DIR%\*' -DestinationPath '!ARCHIVE!'"
    if not errorlevel 1 (
        echo [OK] 已打包: %PROJECT_NAME%-%RID%.zip
    )
)

exit /b 0
