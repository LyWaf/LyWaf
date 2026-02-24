<#
.SYNOPSIS
    LyWaf 跨平台打包脚本 (PowerShell)
    编译为独立可执行程序，无需安装 .NET 运行时

.DESCRIPTION
    支持编译到 Windows、Linux、macOS 多个目标平台
    可在 Windows PowerShell 和 PowerShell Core (Linux/macOS) 上运行

.PARAMETER Target
    目标平台 RID，如: win-x64, linux-x64, osx-arm64, all
    留空则自动检测当前平台

.PARAMETER SkipFrontend
    跳过前端构建

.PARAMETER Trim
    启用裁剪（可减小体积，但可能导致兼容性问题）

.PARAMETER NoArchive
    跳过压缩打包

.EXAMPLE
    .\build.ps1                     # 编译当前平台
    .\build.ps1 -Target linux-x64   # 编译 Linux x64
    .\build.ps1 -Target all         # 编译所有平台
    .\build.ps1 -Target win-x64 -SkipFrontend  # 跳过前端，仅编译后端
    .\build.ps1 -Target win-x64 -Trim         # 启用裁剪，减小体积
#>

param(
    [string]$Target = "",
    [switch]$SkipFrontend,
    [switch]$Trim,
    [switch]$NoArchive
)

$ErrorActionPreference = "Stop"

# ---- 配置 ----
$ScriptDir     = $PSScriptRoot
$ProjectDir    = (Resolve-Path (Join-Path $ScriptDir "..")).Path
$ProjectName   = "LyWaf"
$OutputBase    = Join-Path $ProjectDir "publish"
$FrontendDir   = Join-Path $ProjectDir "Frontend"
$Configuration = "Release"

$AllRIDs = @(
    "linux-x64",
    "linux-arm64",
    "linux-musl-x64",
    "linux-musl-arm64",
    "osx-x64",
    "osx-arm64",
    "win-x64",
    "win-arm64"
)

# ---- 辅助函数 ----
function Write-Info    { param([string]$Msg) Write-Host "[INFO] $Msg" -ForegroundColor Cyan }
function Write-Ok      { param([string]$Msg) Write-Host "[OK] $Msg" -ForegroundColor Green }
function Write-Warn    { param([string]$Msg) Write-Host "[WARN] $Msg" -ForegroundColor Yellow }
function Write-Err     { param([string]$Msg) Write-Host "[ERROR] $Msg" -ForegroundColor Red; exit 1 }

# ---- 检查依赖 ----
function Test-Dependencies {
    $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCmd) {
        Write-Err "未找到 dotnet SDK，请先安装: https://dotnet.microsoft.com/download"
    }

    $dotnetVersion = & dotnet --version
    Write-Info "dotnet SDK 版本: $dotnetVersion"

    $majorVersion = [int]($dotnetVersion.Split('.')[0])
    if ($majorVersion -lt 9) {
        Write-Err "需要 .NET 9.0 SDK 或更高版本, 当前版本: $dotnetVersion"
    }
}

# ---- 构建前端 ----
function Build-Frontend {
    if ($SkipFrontend) {
        Write-Info "跳过前端构建 (-SkipFrontend)"
        return
    }

    $pkgJson = Join-Path $FrontendDir "package.json"
    if (-not (Test-Path $pkgJson)) {
        Write-Warn "未找到前端目录，跳过前端构建"
        return
    }

    $npmCmd = Get-Command npm -ErrorAction SilentlyContinue
    if (-not $npmCmd) {
        Write-Warn "未找到 npm，跳过前端构建 (请手动构建前端或安装 Node.js)"
        return
    }

    Write-Info "正在构建前端..."

    Push-Location $FrontendDir
    try {
        $nodeModules = Join-Path $FrontendDir "node_modules"
        if (-not (Test-Path $nodeModules)) {
            Write-Info "安装前端依赖..."
            & npm install
            if ($LASTEXITCODE -ne 0) {
                Write-Warn "前端依赖安装失败"
                return
            }
        }

        & npm run build
        if ($LASTEXITCODE -ne 0) {
            Write-Warn "前端构建失败"
            return
        }

        Write-Ok "前端构建完成"
    }
    finally {
        Pop-Location
    }
}

# ---- 发布单个目标平台 ----
function Publish-Target {
    param([string]$RID)

    $OutputDir = Join-Path $OutputBase "$ProjectName-$RID"
    $CsprojPath = Join-Path $ProjectDir "$ProjectName.csproj"

    Write-Info "正在编译目标平台: $RID ..."

    # 清理旧输出
    if (Test-Path $OutputDir) {
        Remove-Item -Recurse -Force $OutputDir
    }

    # 构建参数
    $publishArgs = @(
        "publish", $CsprojPath,
        "-c", $Configuration,
        "-r", $RID,
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-o", $OutputDir
    )

    if ($Trim) {
        $publishArgs += "-p:PublishTrimmed=true"
    }

    & dotnet @publishArgs

    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] 编译目标平台 $RID 失败" -ForegroundColor Red
        return $false
    }

    # 清理不需要的文件
    Get-ChildItem -Path $OutputDir -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force

    # 清理测试框架残留 (Microsoft.NET.Test.Sdk / xunit 引入的文件)
    $testJunk = @(
        "CodeCoverage", "alpine", "arm64", "macos", "ubuntu", "x64", "x86",
        "cs", "de", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "tr", "zh-Hans", "zh-Hant",
        "tests", "Frontend", "wwwroot", "myLyWafbintest_build"
    )
    foreach ($dir in $testJunk) {
        $junkPath = Join-Path $OutputDir $dir
        if (Test-Path $junkPath) {
            Remove-Item -Recurse -Force $junkPath -ErrorAction SilentlyContinue
        }
    }

    $testFiles = @(
        "Microsoft.CodeCoverage.*", "Microsoft.DiaSymReader.*", "Microsoft.VisualStudio.*",
        "Microsoft.TestPlatform.*", "Mono.Cecil*", "testhost.*", "xunit.*",
        "ThirdPartyNotices.txt", "Microsoft.CodeCoverage.props", "Microsoft.CodeCoverage.targets",
        "System.Memory.dll", "System.Text.Json.dll", "System.IO.Hashing.dll",
        "web.config", "*.staticwebassets.endpoints.json", "appsettings.Development.json",
        "*.deps.json", "*.runtimeconfig.json", "*.staticwebassets.runtime.json",
        "appsettings.yaml"
    )
    foreach ($pattern in $testFiles) {
        Get-ChildItem -Path $OutputDir -Filter $pattern -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    }

    Write-Ok "编译完成: $RID -> $OutputDir"

    # 压缩打包
    if (-not $NoArchive) {
        $ArchiveName = "$ProjectName-$RID"
        if ($RID -like "win-*") {
            $ArchivePath = Join-Path $OutputBase "$ArchiveName.zip"
            if (Test-Path $ArchivePath) { Remove-Item -Force $ArchivePath }
            Compress-Archive -Path "$OutputDir\*" -DestinationPath $ArchivePath
            Write-Ok "已打包: $ArchiveName.zip"
        }
        else {
            # Linux/macOS 用 tar.gz
            $ArchivePath = Join-Path $OutputBase "$ArchiveName.tar.gz"
            if (Test-Path $ArchivePath) { Remove-Item -Force $ArchivePath }

            # 尝试使用 tar 命令
            $tarCmd = Get-Command tar -ErrorAction SilentlyContinue
            if ($tarCmd) {
                Push-Location $OutputBase
                & tar -czf "$ArchiveName.tar.gz" "$ArchiveName/"
                Pop-Location
                Write-Ok "已打包: $ArchiveName.tar.gz"
            }
            else {
                # 回退到 zip
                $ZipPath = Join-Path $OutputBase "$ArchiveName.zip"
                if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }
                Compress-Archive -Path "$OutputDir\*" -DestinationPath $ZipPath
                Write-Ok "已打包: $ArchiveName.zip (tar 不可用，使用 zip 代替)"
            }
        }
    }

    return $true
}

# ---- 主流程 ----
Write-Host ""
Write-Host "=====================================" -ForegroundColor White
Write-Host "  LyWaf 打包工具 (PowerShell)" -ForegroundColor White
Write-Host "=====================================" -ForegroundColor White
Write-Host ""

Test-Dependencies

# 自动检测平台
if ([string]::IsNullOrEmpty($Target)) {
    if ($IsLinux) {
        $arch = & uname -m
        if ($arch -eq "aarch64" -or $arch -eq "arm64") {
            $Target = "linux-arm64"
        } else {
            $Target = "linux-x64"
        }
    }
    elseif ($IsMacOS) {
        $arch = & uname -m
        if ($arch -eq "arm64") {
            $Target = "osx-arm64"
        } else {
            $Target = "osx-x64"
        }
    }
    else {
        # Windows
        if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") {
            $Target = "win-arm64"
        } else {
            $Target = "win-x64"
        }
    }
    Write-Info "自动检测平台: $Target"
}

# 构建前端
Build-Frontend

# 编译
if ($Target -eq "all") {
    Write-Info "编译所有平台..."
    $failedRIDs = @()

    foreach ($rid in $AllRIDs) {
        $result = Publish-Target -RID $rid
        if (-not $result) {
            $failedRIDs += $rid
        }
    }

    Write-Host ""
    Write-Host "=====================================" -ForegroundColor White

    if ($failedRIDs.Count -gt 0) {
        Write-Warn "部分平台编译失败: $($failedRIDs -join ', ')"
    }
    else {
        Write-Ok "所有平台编译完成!"
    }

    Write-Host "=====================================" -ForegroundColor White
    Write-Info "输出目录: $OutputBase"
    Write-Host ""

    # 列出产物
    Get-ChildItem -Path $OutputBase -Include "*.tar.gz", "*.zip" -Recurse | 
        ForEach-Object { Write-Host "  $($_.Name)  ($([math]::Round($_.Length / 1MB, 2)) MB)" }
}
else {
    $result = Publish-Target -RID $Target

    if ($result) {
        Write-Host ""
        Write-Host "=====================================" -ForegroundColor White
        Write-Ok "编译完成!"
        Write-Host "=====================================" -ForegroundColor White

        $outputDir = Join-Path $OutputBase "$ProjectName-$Target"
        Write-Info "输出目录: $outputDir"

        # 显示可执行文件信息
        if (Test-Path $outputDir) {
            if ($Target -like "win-*") {
                $exeFile = Get-ChildItem -Path $outputDir -Filter "*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
            }
            else {
                $exeFile = Get-ChildItem -Path $outputDir -Filter $ProjectName -ErrorAction SilentlyContinue | Select-Object -First 1
            }

            if ($exeFile) {
                $sizeMB = [math]::Round($exeFile.Length / 1MB, 2)
                Write-Info "可执行文件: $($exeFile.Name) ($sizeMB MB)"
            }
        }
    }
}

Write-Host ""
