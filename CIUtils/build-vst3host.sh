#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: build-vst3host.sh --sdk-root PATH --output PATH [--configuration NAME] [--arch ARCH]

Builds the TuneLab native VST3 host against Steinberg VST3 SDK 3.8.1.
USAGE
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SDK_ROOT="${VST3_SDK_ROOT:-}"
OUTPUT=""
CONFIGURATION=Release
ARCH="$(uname -m)"
MIN_MACOS_VERSION="${TUNELAB_MACOS_MIN_VERSION:-26.0}"

[[ "$MIN_MACOS_VERSION" =~ ^[0-9]+\.[0-9]+$ ]] || die "invalid TUNELAB_MACOS_MIN_VERSION: $MIN_MACOS_VERSION"

while (($#)); do
  case "$1" in
    --sdk-root)
      (($# >= 2)) || die "--sdk-root needs a value"
      SDK_ROOT="$2"
      shift 2
      ;;
    --output)
      (($# >= 2)) || die "--output needs a value"
      OUTPUT="$2"
      shift 2
      ;;
    --configuration)
      (($# >= 2)) || die "--configuration needs a value"
      CONFIGURATION="$2"
      shift 2
      ;;
    --arch)
      (($# >= 2)) || die "--arch needs a value"
      ARCH="$2"
      shift 2
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      die "unknown option: $1"
      ;;
  esac
done

[[ "$(uname -s)" == Darwin ]] || die "this build script requires macOS"
[[ -n "$SDK_ROOT" && -f "$SDK_ROOT/CMakeLists.txt" ]] || die "provide a VST3 SDK source tree with --sdk-root"
[[ -n "$OUTPUT" ]] || die "provide an output path with --output"
command -v cmake >/dev/null 2>&1 || die "CMake is required"
command -v ninja >/dev/null 2>&1 || die "Ninja is required"

if [[ -z "${XCODE_VERSION:-}" ]]; then
  XCODE_VERSION="$(xcodebuild -version 2>/dev/null | awk '/^Xcode / { print $2; exit }' || true)"
fi
if [[ -z "${XCODE_VERSION:-}" && -f /Applications/Xcode.app/Contents/Info.plist ]]; then
  XCODE_VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' /Applications/Xcode.app/Contents/Info.plist)"
fi
[[ -n "${XCODE_VERSION:-}" ]] || die "could not determine the installed Xcode version"
export XCODE_VERSION

SDK_VERSION="$(git -C "$SDK_ROOT" describe --tags --exact-match 2>/dev/null || true)"
[[ "$SDK_VERSION" == v3.8.1_build_84 ]] || die "expected VST3 SDK tag v3.8.1_build_84 (found '${SDK_VERSION:-untagged}')"

BUILD_DIR="$(mktemp -d "${TMPDIR:-/private/tmp}/tunelab-vst3host.XXXXXX")"
trap 'rm -rf "$BUILD_DIR"' EXIT

cmake -S "$ROOT/native/macos/Vst3Host" -B "$BUILD_DIR" -G Ninja \
  -DCMAKE_BUILD_TYPE="$CONFIGURATION" \
  -DCMAKE_OSX_ARCHITECTURES="$ARCH" \
  -DCMAKE_OSX_DEPLOYMENT_TARGET="$MIN_MACOS_VERSION" \
  -DXCODE_VERSION="$XCODE_VERSION" \
  -DVST3_SDK_ROOT="$SDK_ROOT"
cmake --build "$BUILD_DIR" --config "$CONFIGURATION" --target TuneLabVst3Host

mkdir -p "$(dirname "$OUTPUT")"
cp "$BUILD_DIR/libTuneLabVst3Host.dylib" "$OUTPUT"
