#!/bin/bash
# ============================================================================
# LyWaf 跨平台打包脚本 (Linux / macOS)
# 编译为独立可执行程序，无需安装 .NET 运行时
# ============================================================================
# 用法:
#   ./build.sh                    # 编译当前平台
#   ./build.sh linux-x64          # 编译 Linux x64
#   ./build.sh linux-arm64        # 编译 Linux ARM64
#   ./build.sh osx-x64            # 编译 macOS x64 (Intel)
#   ./build.sh osx-arm64          # 编译 macOS ARM64 (Apple Silicon)
#   ./build.sh win-x64            # 交叉编译 Windows x64
#   ./build.sh all                # 编译所有平台
#   ./build.sh --trim linux-x64   # 启用裁剪 (可减小体积，可能有兼容性问题)
# ============================================================================

set -e

# 颜色输出
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_NAME="LyWaf"
OUTPUT_BASE="$PROJECT_DIR/publish"
FRONTEND_DIR="$PROJECT_DIR/Frontend"
CONFIGURATION="Release"

# 支持的目标平台
ALL_RIDS=("linux-x64" "linux-arm64" "linux-musl-x64" "linux-musl-arm64" "osx-x64" "osx-arm64" "win-x64" "win-arm64")

info()    { echo -e "${CYAN}[INFO]${NC} $1"; }
success() { echo -e "${GREEN}[OK]${NC} $1"; }
warn()    { echo -e "${YELLOW}[WARN]${NC} $1"; }
error()   { echo -e "${RED}[ERROR]${NC} $1"; exit 1; }

# ---- 检查依赖 ----
check_deps() {
    if ! command -v dotnet &> /dev/null; then
        error "未找到 dotnet SDK，请先安装: https://dotnet.microsoft.com/download"
    fi

    DOTNET_VERSION=$(dotnet --version)
    info "dotnet SDK 版本: $DOTNET_VERSION"

    # 检查是否为 .NET 9+
    MAJOR_VERSION=$(echo "$DOTNET_VERSION" | cut -d. -f1)
    if [ "$MAJOR_VERSION" -lt 9 ]; then
        error "需要 .NET 9.0 SDK 或更高版本, 当前版本: $DOTNET_VERSION"
    fi
}

# ---- 构建前端 ----
build_frontend() {
    if [ -d "$FRONTEND_DIR" ] && [ -f "$FRONTEND_DIR/package.json" ]; then
        info "正在构建前端..."

        if ! command -v npm &> /dev/null; then
            warn "未找到 npm，跳过前端构建 (请手动构建前端或安装 Node.js)"
            return 0
        fi

        cd "$FRONTEND_DIR"

        if [ ! -d "node_modules" ]; then
            info "安装前端依赖..."
            npm install
        fi

        npm run build
        cd "$PROJECT_DIR"
        success "前端构建完成"
    else
        warn "未找到前端目录，跳过前端构建"
    fi
}

# ---- 发布单个目标平台 ----
publish_rid() {
    local RID="$1"
    local OUTPUT_DIR="$OUTPUT_BASE/$PROJECT_NAME-$RID"

    info "正在编译目标平台: $RID ..."

    # 清理旧的输出
    rm -rf "$OUTPUT_DIR"

    local PUBLISH_ARGS=(
        publish "$PROJECT_DIR/$PROJECT_NAME.csproj"
        -c "$CONFIGURATION"
        -r "$RID"
        --self-contained true
        -p:PublishSingleFile=true
        -p:IncludeNativeLibrariesForSelfExtract=true
        -p:EnableCompressionInSingleFile=true
        -o "$OUTPUT_DIR"
    )

    # 仅在指定 --trim 参数时启用裁剪
    if [ "${ENABLE_TRIM:-0}" = "1" ]; then
        PUBLISH_ARGS+=("-p:PublishTrimmed=true")
    fi

    dotnet "${PUBLISH_ARGS[@]}"

    # 确保 datas 目录被拷贝（PublishSingleFile 模式下可能不会自动拷贝）
    if [ -d "$PROJECT_DIR/datas" ] && [ ! -d "$OUTPUT_DIR/datas" ]; then
        mkdir -p "$OUTPUT_DIR/datas"
        cp -r "$PROJECT_DIR/datas/"* "$OUTPUT_DIR/datas/"
        info "已拷贝 datas 目录"
    fi

    # 清理不需要的文件
    rm -f "$OUTPUT_DIR"/*.pdb

    # 清理运行时状态文件
    rm -f "$OUTPUT_DIR"/.lywaf.*
    # 清理 config.ly.* 但保留 config.ly 和 config.ly.example
    find "$OUTPUT_DIR" -maxdepth 1 -name "config.ly.*" ! -name "config.ly.example" ! -name "config.ly" -delete 2>/dev/null || true

    # 清理测试框架残留 (Microsoft.NET.Test.Sdk / xunit 引入的文件)
    local JUNK_DIRS=(CodeCoverage alpine arm64 macos ubuntu x64 x86
        cs de es fr it ja ko pl pt-BR ru tr zh-Hans zh-Hant tests Frontend wwwroot myLyWafbintest_build)
    for dir in "${JUNK_DIRS[@]}"; do
        rm -rf "$OUTPUT_DIR/$dir"
    done

    # 清理构建产物、临时目录、bin* 开头的目录
    rm -rf "$OUTPUT_DIR"/publish "$OUTPUT_DIR"/temp "$OUTPUT_DIR"/logs "$OUTPUT_DIR"/certs
    find "$OUTPUT_DIR" -maxdepth 1 -type d -name "bin*" -exec rm -rf {} + 2>/dev/null || true

    rm -f "$OUTPUT_DIR"/Microsoft.CodeCoverage.* \
          "$OUTPUT_DIR"/Microsoft.DiaSymReader.* \
          "$OUTPUT_DIR"/Microsoft.VisualStudio.* \
          "$OUTPUT_DIR"/Microsoft.TestPlatform.* \
          "$OUTPUT_DIR"/Mono.Cecil* \
          "$OUTPUT_DIR"/testhost.* \
          "$OUTPUT_DIR"/xunit.* \
          "$OUTPUT_DIR"/ThirdPartyNotices.txt \
          "$OUTPUT_DIR"/System.Memory.dll \
          "$OUTPUT_DIR"/System.Text.Json.dll \
          "$OUTPUT_DIR"/System.IO.Hashing.dll \
          "$OUTPUT_DIR"/web.config \
          "$OUTPUT_DIR"/*.staticwebassets.endpoints.json \
          "$OUTPUT_DIR"/appsettings.Development.json \
          "$OUTPUT_DIR"/*.deps.json \
          "$OUTPUT_DIR"/*.runtimeconfig.json \
          "$OUTPUT_DIR"/*.staticwebassets.runtime.json \
          "$OUTPUT_DIR"/appsettings.yaml

    success "编译完成: $RID -> $OUTPUT_DIR"

    # 打包为 tar.gz (Linux/macOS) 或 zip (Windows)
    cd "$OUTPUT_BASE"
    local ARCHIVE_NAME="$PROJECT_NAME-$RID"

    if [[ "$RID" == win-* ]]; then
        if command -v zip &> /dev/null; then
            zip -r -q "$ARCHIVE_NAME.zip" "$ARCHIVE_NAME/"
            success "已打包: $ARCHIVE_NAME.zip"
        else
            warn "未安装 zip，跳过压缩打包"
        fi
    else
        tar -czf "$ARCHIVE_NAME.tar.gz" "$ARCHIVE_NAME/"
        success "已打包: $ARCHIVE_NAME.tar.gz"
    fi

    cd "$PROJECT_DIR"
}

# ---- 主函数 ----
main() {
    info "====================================="
    info "  LyWaf 打包工具"
    info "====================================="

    check_deps

    # 解析参数
    ENABLE_TRIM=0
    local TARGET=""
    for arg in "$@"; do
        case "$arg" in
            --trim) ENABLE_TRIM=1 ;;
            *)      TARGET="$arg" ;;
        esac
    done

    if [ -z "$TARGET" ]; then
        # 自动检测当前平台
        local OS_TYPE ARCH_TYPE
        OS_TYPE="$(uname -s)"
        ARCH_TYPE="$(uname -m)"

        case "$OS_TYPE" in
            Linux*)
                # 检查是否为 musl (Alpine)
                if ldd --version 2>&1 | grep -qi musl; then
                    OS_TYPE="linux-musl"
                else
                    OS_TYPE="linux"
                fi
                ;;
            Darwin*)  OS_TYPE="osx" ;;
            MINGW*|MSYS*|CYGWIN*) OS_TYPE="win" ;;
            *)        error "不支持的操作系统: $OS_TYPE" ;;
        esac

        case "$ARCH_TYPE" in
            x86_64|amd64)  ARCH_TYPE="x64" ;;
            aarch64|arm64) ARCH_TYPE="arm64" ;;
            *)             error "不支持的架构: $ARCH_TYPE" ;;
        esac

        TARGET="$OS_TYPE-$ARCH_TYPE"
        info "自动检测平台: $TARGET"
    fi

    # 构建前端
    build_frontend

    # 编译
    if [ "$TARGET" = "all" ]; then
        info "编译所有平台..."
        for RID in "${ALL_RIDS[@]}"; do
            publish_rid "$RID"
        done

        echo ""
        info "====================================="
        success "所有平台编译完成!"
        info "====================================="
        info "输出目录: $OUTPUT_BASE"
        echo ""
        ls -lh "$OUTPUT_BASE"/*.{tar.gz,zip} 2>/dev/null || true
    else
        publish_rid "$TARGET"

        echo ""
        info "====================================="
        success "编译完成!"
        info "====================================="
        info "输出目录: $OUTPUT_BASE/$PROJECT_NAME-$TARGET"
    fi
}

main "$@"
