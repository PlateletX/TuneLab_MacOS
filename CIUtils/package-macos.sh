#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: package-macos.sh [options]

Publishes TuneLab and its CLI for macOS, assembles TuneLab.app in a temporary
directory, and creates a ZIP containing the validated app bundle. An optional Developer ID signing
identity and notarization keychain profile can be supplied through environment
variables.

Options:
  --configuration NAME    Build configuration (default: Release)
  --runtime RID           Runtime identifier (default: osx-arm64)
  --output-dir PATH       Output directory (default: artifacts/macos)
  --version VERSION       Bundle and archive version (default: tag or project version)
  --self-contained BOOL   Include the .NET runtime (default: true)
  --skip-publish          Package existing publish directories without dotnet
  --dmg                   Also create a DMG with an Applications shortcut
  --help                  Show this help

Optional environment:
  MACOS_SIGN_IDENTITY     Developer ID Application identity shown by codesign
  MACOS_NOTARY_PROFILE    Keychain profile for `xcrun notarytool`; requires --dmg

Publish directories used by --skip-publish:
  TuneLab/bin/<configuration>/net8.0/<runtime>/publish
  TuneLab.Cli/bin/<configuration>/net8.0/<runtime>/publish
  ExtensionInstaller/bin/<configuration>/net8.0/<runtime>/publish

SDL2 is bundled from the published NuGet runtime asset. LAME defaults to the
Homebrew library; release builds should set MACOS_LAME_DYLIB to a library built
for macOS 26. The VST3 host is built from the pinned Steinberg SDK when
VST3_SDK_ROOT is set, or supplied through MACOS_VST3_HOST_DYLIB. Non-system
dependencies of bundled native libraries are bundled automatically. Override
SDL2 with MACOS_SDL2_DYLIB when needed.
USAGE
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION=Release
RUNTIME=osx-arm64
OUTPUT_DIR="$ROOT/artifacts/macos"
VERSION=""
SELF_CONTAINED=true
SKIP_PUBLISH=false
MAKE_DMG=false
MIN_MACOS_VERSION="${TUNELAB_MACOS_MIN_VERSION:-26.0}"
MIN_MACOS_MAJOR="${MIN_MACOS_VERSION%%.*}"
MIN_MACOS_MINOR="${MIN_MACOS_VERSION#*.}"

[[ "$MIN_MACOS_VERSION" =~ ^[0-9]+\.[0-9]+$ ]] || die "invalid TUNELAB_MACOS_MIN_VERSION: $MIN_MACOS_VERSION"

# Keep managed apphosts and native dependencies on the same minimum system
# version, even when this script is invoked outside CI.
export TUNELAB_MACOS_MIN_VERSION="$MIN_MACOS_VERSION"
export MACOSX_DEPLOYMENT_TARGET="$MIN_MACOS_VERSION"

while (($#)); do
  case "$1" in
    --configuration)
      (($# >= 2)) || die "--configuration needs a value"
      CONFIGURATION="$2"
      shift 2
      ;;
    --runtime)
      (($# >= 2)) || die "--runtime needs a value"
      RUNTIME="$2"
      shift 2
      ;;
    --output-dir)
      (($# >= 2)) || die "--output-dir needs a value"
      OUTPUT_DIR="$2"
      shift 2
      ;;
    --version)
      (($# >= 2)) || die "--version needs a value"
      VERSION="$2"
      shift 2
      ;;
    --self-contained)
      (($# >= 2)) || die "--self-contained needs a value"
      SELF_CONTAINED="$2"
      shift 2
      ;;
    --skip-publish)
      SKIP_PUBLISH=true
      shift
      ;;
    --dmg)
      MAKE_DMG=true
      shift
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

[[ "$(uname -s)" == Darwin ]] || die "this packaging script must run on macOS"
[[ "$RUNTIME" == osx-arm64 || "$RUNTIME" == osx-x64 ]] || die "unsupported macOS runtime: $RUNTIME"
[[ "$SELF_CONTAINED" == true || "$SELF_CONTAINED" == false ]] || die "--self-contained must be true or false"

find_brew_library() {
  local formula="$1"
  local pattern="$2"
  command -v brew >/dev/null 2>&1 || return 1
  local prefix
  prefix="$(brew --prefix "$formula" 2>/dev/null)" || return 1
  local candidate
  for candidate in "$prefix"/lib/$pattern; do
    if [[ -e "$candidate" ]]; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done
  return 1
}

LAME_DYLIB="${MACOS_LAME_DYLIB:-}"
if [[ -z "$LAME_DYLIB" ]]; then
  LAME_DYLIB="$(find_brew_library lame 'libmp3lame*.dylib')" || die "LAME dylib not found; install Homebrew lame or set MACOS_LAME_DYLIB"
fi
[[ -f "$LAME_DYLIB" ]] || die "LAME dylib not found: $LAME_DYLIB"

if [[ -n "${MACOS_NOTARY_PROFILE:-}" && "$MAKE_DMG" != true ]]; then
  die "MACOS_NOTARY_PROFILE requires --dmg so the notarized image can be stapled"
fi
if [[ -n "${MACOS_NOTARY_PROFILE:-}" && -z "${MACOS_SIGN_IDENTITY:-}" ]]; then
  die "MACOS_NOTARY_PROFILE requires MACOS_SIGN_IDENTITY"
fi

if [[ -z "$VERSION" ]]; then
  VERSION="${GITHUB_REF_NAME:-}"
  VERSION="${VERSION#v}"
fi
if [[ -z "$VERSION" || "$VERSION" == master || ! "$VERSION" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]]; then
  VERSION="$(sed -n 's/^[[:space:]]*<Version>\([^<]*\)<\/Version>[[:space:]]*$/\1/p' "$ROOT/TuneLab/TuneLab.csproj" | head -n 1)"
fi
[[ "$VERSION" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]] || die "could not determine a numeric bundle version (got '$VERSION')"

APP_PUBLISH="$ROOT/TuneLab/bin/$CONFIGURATION/net8.0/$RUNTIME/publish"
CLI_PUBLISH="$ROOT/TuneLab.Cli/bin/$CONFIGURATION/net8.0/$RUNTIME/publish"
INSTALLER_PUBLISH="$ROOT/ExtensionInstaller/bin/$CONFIGURATION/net8.0/$RUNTIME/publish"
LEGACY_COMPAT_OUTPUT="$ROOT/legacy/compat/TuneLab.Hosting.Compat.Legacy/bin/$CONFIGURATION/net8.0"

if [[ "$SKIP_PUBLISH" != true ]]; then
  command -v dotnet >/dev/null 2>&1 || die ".NET SDK is required; install .NET 8 or run this script on the macOS CI runner"
  # Publish directories are reused by dotnet and can retain files from an
  # earlier project or a previous packaging attempt.  Remove the generated
  # RID roots, not only the nested publish folders: the main project and the
  # installer share part of their BaseOutputPath, so stale files at the RID
  # root would otherwise be copied into the next publish manifest.
  rm -rf "$(dirname "$APP_PUBLISH")" "$(dirname "$CLI_PUBLISH")" "$(dirname "$INSTALLER_PUBLISH")" "$LEGACY_COMPAT_OUTPUT"
  dotnet publish "$ROOT/TuneLab/TuneLab.csproj" \
    --configuration "$CONFIGURATION" \
    --runtime "$RUNTIME" \
    --self-contained "$SELF_CONTAINED"
  dotnet publish "$ROOT/TuneLab.Cli/TuneLab.Cli.csproj" \
    --configuration "$CONFIGURATION" \
    --runtime "$RUNTIME" \
    --self-contained "$SELF_CONTAINED"
  dotnet publish "$ROOT/ExtensionInstaller/ExtensionInstaller.csproj" \
    --configuration "$CONFIGURATION" \
    --runtime "$RUNTIME" \
    --self-contained "$SELF_CONTAINED" \
    --output "$INSTALLER_PUBLISH"
fi

[[ -d "$APP_PUBLISH" ]] || die "TuneLab publish output not found: $APP_PUBLISH"
[[ -d "$CLI_PUBLISH" ]] || die "TuneLab CLI publish output not found: $CLI_PUBLISH"
[[ -d "$INSTALLER_PUBLISH" ]] || die "Extension Installer publish output not found: $INSTALLER_PUBLISH"
[[ -f "$APP_PUBLISH/TuneLab" ]] || die "TuneLab apphost not found in $APP_PUBLISH"
[[ -f "$CLI_PUBLISH/TuneLab.Cli" ]] || die "TuneLab CLI apphost not found in $CLI_PUBLISH"
[[ -f "$INSTALLER_PUBLISH/ExtensionInstaller" ]] || die "Extension Installer apphost not found in $INSTALLER_PUBLISH"

SDL2_DYLIB="${MACOS_SDL2_DYLIB:-$APP_PUBLISH/libSDL2.dylib}"
[[ -f "$SDL2_DYLIB" ]] || die "SDL2 dylib not found in the app publish output: $SDL2_DYLIB"

EXPECTED_ARCH=arm64
[[ "$RUNTIME" == osx-x64 ]] && EXPECTED_ARCH=x86_64
mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"
BUILD_DIR="$(mktemp -d "${TMPDIR:-/private/tmp}/tunelab-package.XXXXXX")"
NATIVE_STAGE="$BUILD_DIR/native"
mkdir -p "$NATIVE_STAGE"
trap 'rm -rf "$BUILD_DIR"' EXIT

VST3_HOST_DYLIB="${MACOS_VST3_HOST_DYLIB:-}"
if [[ -z "$VST3_HOST_DYLIB" && -n "${VST3_SDK_ROOT:-}" ]]; then
  VST3_HOST_DYLIB="$BUILD_DIR/libTuneLabVst3Host.dylib"
  bash "$ROOT/CIUtils/build-vst3host.sh" --sdk-root "$VST3_SDK_ROOT" \
    --output "$VST3_HOST_DYLIB" --configuration "$CONFIGURATION" --arch "$EXPECTED_ARCH"
fi
[[ -f "$VST3_HOST_DYLIB" ]] || die "VST3 host dylib not found; set VST3_SDK_ROOT or MACOS_VST3_HOST_DYLIB"

NATIVE_LIBRARY_NAMES=(libSDL2.dylib libmp3lame.dylib libTuneLabVst3Host.dylib)
NATIVE_LIBRARY_SOURCES=("$SDL2_DYLIB" "$LAME_DYLIB" "$VST3_HOST_DYLIB")
for ((index = 0; index < ${#NATIVE_LIBRARY_NAMES[@]}; index++)); do
  cp -L "${NATIVE_LIBRARY_SOURCES[$index]}" "$NATIVE_STAGE/${NATIVE_LIBRARY_NAMES[$index]}"
done

resolve_native_dependency() {
  local image="$1"
  local dependency="$2"
  local candidate
  local rpath

  case "$dependency" in
    /*)
      if [[ -f "$dependency" ]]; then
        printf '%s\n' "$dependency"
        return 0
      fi
      ;;
    @loader_path/*)
      candidate="$(dirname "$image")/${dependency#@loader_path/}"
      if [[ -f "$candidate" ]]; then
        printf '%s\n' "$candidate"
        return 0
      fi
      ;;
    @rpath/*)
      while IFS= read -r rpath; do
        case "$rpath" in
          @loader_path/*) rpath="$(dirname "$image")/${rpath#@loader_path/}" ;;
          /*) ;;
          *) continue ;;
        esac
        candidate="$rpath/${dependency#@rpath/}"
        if [[ -f "$candidate" ]]; then
          printf '%s\n' "$candidate"
          return 0
        fi
      done < <(otool -l "$image" | awk '$1 == "cmd" && $2 == "LC_RPATH" { next_path = 1; next } next_path && $1 == "path" { print $2; next_path = 0 }')
      ;;
  esac
  return 1
}

for ((index = 0; index < ${#NATIVE_LIBRARY_NAMES[@]}; index++)); do
  native_library="$NATIVE_STAGE/${NATIVE_LIBRARY_NAMES[$index]}"
  source_library="${NATIVE_LIBRARY_SOURCES[$index]}"
  architectures="$(lipo -archs "$native_library" 2>/dev/null)" || die "not a macOS dynamic library: $native_library"
  [[ " $architectures " == *" $EXPECTED_ARCH "* ]] || die "$native_library does not contain $EXPECTED_ARCH (found: $architectures)"

  if command -v vtool >/dev/null 2>&1; then
    build_info="$(vtool -show-build "$native_library" 2>/dev/null)" || die "could not inspect minimum macOS version: $native_library"
    printf '%s\n' "$build_info" | awk -v min_major="$MIN_MACOS_MAJOR" -v min_minor="$MIN_MACOS_MINOR" '$1 == "minos" { split($2, version, "."); if (version[1] > min_major || (version[1] == min_major && version[2] > min_minor)) exit 1 }' \
      || die "$native_library requires a macOS version later than $MIN_MACOS_VERSION"
  fi

  install_name_tool -id "@loader_path/${NATIVE_LIBRARY_NAMES[$index]}" "$native_library"

  install_id="$(otool -D "$native_library" 2>/dev/null | tail -n +2 | head -n 1)"
  dependencies="$(otool -L "$native_library" 2>/dev/null)" || die "could not inspect native dependencies: $native_library"
  while IFS= read -r dependency; do
    dependency="${dependency#"${dependency%%[![:space:]]*}"}"
    dependency="${dependency%% (compatibility version*}"
    [[ -n "$dependency" && "$dependency" != "$install_id" ]] || continue

    case "$dependency" in
      /System/Library/*|/usr/lib/*)
        continue
        ;;
      @loader_path/*)
        if [[ -f "$NATIVE_STAGE/${dependency#@loader_path/}" ]]; then
          continue
        fi
        ;;
    esac

    dependency_source="$(resolve_native_dependency "$source_library" "$dependency")" \
      || die "$native_library has an unresolved dependency: $dependency"
    dependency_name="$(basename "$dependency_source")"
    dependency_index=-1
    for ((candidate_index = 0; candidate_index < ${#NATIVE_LIBRARY_NAMES[@]}; candidate_index++)); do
      if [[ "${NATIVE_LIBRARY_NAMES[$candidate_index]}" == "$dependency_name" ]]; then
        cmp -s "$dependency_source" "${NATIVE_LIBRARY_SOURCES[$candidate_index]}" \
          || die "native dependency name collision: $dependency_name"
        dependency_index="$candidate_index"
        break
      fi
    done
    if ((dependency_index < 0)); then
      cp -L "$dependency_source" "$NATIVE_STAGE/$dependency_name"
      NATIVE_LIBRARY_NAMES+=("$dependency_name")
      NATIVE_LIBRARY_SOURCES+=("$dependency_source")
    fi
    install_name_tool -change "$dependency" "@loader_path/$dependency_name" "$native_library"
  done < <(printf '%s\n' "$dependencies" | tail -n +2)
done

if [[ "$SKIP_PUBLISH" != true ]]; then
  for apphost in "$APP_PUBLISH/TuneLab" "$CLI_PUBLISH/TuneLab.Cli" "$INSTALLER_PUBLISH/ExtensionInstaller"; do
    architectures="$(lipo -archs "$apphost" 2>/dev/null)" || die "not a macOS executable: $apphost"
    [[ " $architectures " == *" $EXPECTED_ARCH "* ]] || die "$apphost does not contain $EXPECTED_ARCH (found: $architectures)"
  done
fi

APP="$BUILD_DIR/TuneLab.app"
CONTENTS="$APP/Contents"
MACOS="$CONTENTS/MacOS"
HELPERS="$CONTENTS/Helpers"
CLI_HELPER="$HELPERS/TuneLab.Cli"
INSTALLER_HELPER="$HELPERS/ExtensionInstaller"
RESOURCES="$CONTENTS/Resources"
FRAMEWORKS="$CONTENTS/Frameworks"
mkdir -p "$MACOS" "$HELPERS" "$RESOURCES" "$FRAMEWORKS"

ditto --norsrc --noextattr --noqtn "$APP_PUBLISH" "$MACOS"
# Each self-contained helper has its own .NET runtime, so keep their files apart.
mkdir -p "$CLI_HELPER" "$INSTALLER_HELPER"
ditto --norsrc --noextattr --noqtn "$CLI_PUBLISH" "$CLI_HELPER"
ditto --norsrc --noextattr --noqtn "$INSTALLER_PUBLISH" "$INSTALLER_HELPER"
[[ -f "$CLI_HELPER/TuneLab.Cli" ]] || die "TuneLab CLI apphost was not bundled"
[[ -f "$INSTALLER_HELPER/ExtensionInstaller" ]] || die "Extension Installer apphost was not bundled"
chmod +x "$MACOS/TuneLab" "$CLI_HELPER/TuneLab.Cli" "$INSTALLER_HELPER/ExtensionInstaller"
cp "$ROOT/LICENSE.txt" "$MACOS/LICENSE.txt"
cp "$ROOT/THIRD-PARTY-NOTICES.txt" "$MACOS/THIRD-PARTY-NOTICES.txt"

ditto --norsrc --noextattr --noqtn "$NATIVE_STAGE/." "$FRAMEWORKS"

cat > "$CONTENTS/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleDevelopmentRegion</key>
	<string>en</string>
	<key>CFBundleDisplayName</key>
	<string>TuneLab</string>
	<key>CFBundleExecutable</key>
	<string>TuneLab</string>
	<key>CFBundleIdentifier</key>
	<string>app.tunelab.editor</string>
	<key>CFBundleInfoDictionaryVersion</key>
	<string>6.0</string>
	<key>CFBundleName</key>
	<string>TuneLab</string>
	<key>CFBundlePackageType</key>
	<string>APPL</string>
	<key>CFBundleShortVersionString</key>
	<string>$VERSION</string>
	<key>CFBundleVersion</key>
	<string>$VERSION</string>
	<key>LSApplicationCategoryType</key>
	<string>public.app-category.music</string>
	<key>LSMinimumSystemVersion</key>
	<string>$MIN_MACOS_VERSION</string>
	<key>NSHighResolutionCapable</key>
	<true/>
	<key>NSPrincipalClass</key>
	<string>NSApplication</string>
	<key>CFBundleDocumentTypes</key>
	<array>
		<dict>
			<key>LSItemContentTypes</key>
			<array><string>app.tunelab.project</string></array>
			<key>CFBundleTypeName</key>
			<string>TuneLab Project</string>
			<key>CFBundleTypeRole</key>
			<string>Editor</string>
			<key>LSHandlerRank</key>
			<string>Owner</string>
		</dict>
		<dict>
			<key>LSItemContentTypes</key>
			<array><string>app.tunelab.extension-package</string></array>
			<key>CFBundleTypeName</key>
			<string>TuneLab Extension Package</string>
			<key>CFBundleTypeRole</key>
			<string>Editor</string>
			<key>LSHandlerRank</key>
			<string>Owner</string>
		</dict>
	</array>
	<key>UTExportedTypeDeclarations</key>
	<array>
		<dict>
			<key>UTTypeConformsTo</key>
			<array><string>public.data</string></array>
			<key>UTTypeDescription</key>
			<string>TuneLab Project</string>
			<key>UTTypeIdentifier</key>
			<string>app.tunelab.project</string>
			<key>UTTypeTagSpecification</key>
			<dict>
				<key>public.filename-extension</key>
			<array><string>tlpx</string><string>tlp</string></array>
			</dict>
		</dict>
		<dict>
			<key>UTTypeConformsTo</key>
			<array><string>public.data</string></array>
			<key>UTTypeDescription</key>
			<string>TuneLab Extension Package</string>
			<key>UTTypeIdentifier</key>
			<string>app.tunelab.extension-package</string>
			<key>UTTypeTagSpecification</key>
			<dict>
				<key>public.filename-extension</key>
				<array><string>tlx</string></array>
			</dict>
		</dict>
	</array>
</dict>
</plist>
PLIST

if command -v sips >/dev/null 2>&1; then
  sips -s format icns "$ROOT/TuneLab/Assets/TuneLab.ico" --out "$RESOURCES/TuneLab.icns" >/dev/null
  /usr/libexec/PlistBuddy -c 'Add :CFBundleIconFile string TuneLab' "$CONTENTS/Info.plist"
fi

plutil -lint "$CONTENTS/Info.plist" >/dev/null

# Downloaded build inputs can carry provenance attributes that prevent signing.
xattr -cr "$APP"

if [[ -n "${MACOS_SIGN_IDENTITY:-}" ]]; then
  codesign --force --deep --options runtime --timestamp \
    --entitlements "$ROOT/CIUtils/macos-entitlements.plist" \
    --sign "$MACOS_SIGN_IDENTITY" "$APP"
  codesign --verify --deep --strict "$APP"
else
  for native_library in "$FRAMEWORKS"/*.dylib; do
    codesign --force --sign - "$native_library"
    codesign --verify --strict "$native_library"
  done
fi

ZIP="$OUTPUT_DIR/TuneLab-$RUNTIME-v$VERSION.zip"
rm -f "$ZIP"
if [[ -n "${MACOS_SIGN_IDENTITY:-}" ]]; then
  # Managed assemblies are nested code under Contents/MacOS. Their signatures
  # can live in extended attributes, which must survive a signed archive.
  ditto -c -k --keepParent "$APP" "$ZIP"
else
  ditto -c -k --norsrc --noextattr --noqtn --keepParent "$APP" "$ZIP"
fi

if [[ "$MAKE_DMG" == true ]]; then
  command -v hdiutil >/dev/null 2>&1 || die "hdiutil is required to create a DMG"
  DMG="$OUTPUT_DIR/TuneLab-$RUNTIME-v$VERSION.dmg"
  STAGING="$BUILD_DIR/dmg-staging"
  rm -rf "$STAGING" "$DMG"
  mkdir -p "$STAGING"
  ditto "$APP" "$STAGING/TuneLab.app"
  ln -s /Applications "$STAGING/Applications"
  hdiutil create -volname TuneLab -srcfolder "$STAGING" -ov -format UDZO "$DMG" >/dev/null
  rm -rf "$STAGING"

  if [[ -n "${MACOS_NOTARY_PROFILE:-}" ]]; then
    xcrun notarytool submit "$DMG" --keychain-profile "$MACOS_NOTARY_PROFILE" --wait
    xcrun stapler staple "$DMG"
    xcrun stapler validate "$DMG"
  fi
fi

printf 'Created app bundle archive: %s\n' "$ZIP"
if [[ "$MAKE_DMG" == true ]]; then
  printf 'Created disk image: %s\n' "$DMG"
fi
