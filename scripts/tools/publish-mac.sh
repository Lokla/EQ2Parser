#!/usr/bin/env bash
# Build EQ2Parser.app for this Mac: a self-contained osx-arm64 publish of
# src/EQ2Parser.Mac wrapped in an app bundle, signed ad-hoc (personal use;
# no Developer ID, no notarization).
#
#   scripts/tools/publish-mac.sh             # -> artifacts/mac/EQ2Parser.app
#   scripts/tools/publish-mac.sh --install   # ... and copy it to ~/Applications
#
# The version is the nearest git tag (v1.2.3 -> 1.2.3) plus the commit,
# as the Windows release takes it from the tag; untagged trees say 0.0.0.
# A bundle built here was never downloaded, so Gatekeeper does not
# quarantine it and it opens with a double-click.
#
# ASCII only, like the other scripts.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
install=0
[ "${1:-}" = "--install" ] && install=1

dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$dotnet" ] || dotnet="$(command -v dotnet)"
export DOTNET_ROOT="$(dirname "$dotnet")" DOTNET_CLI_TELEMETRY_OPTOUT=1

out="$repo/artifacts/mac"
app="$out/EQ2Parser.app"
publish="$out/publish"
rm -rf "$out"
mkdir -p "$publish"

tag="$(git -C "$repo" describe --tags --abbrev=0 2>/dev/null || echo v0.0.0)"
version="${tag#v}"
commit="$(git -C "$repo" rev-parse --short HEAD)"

echo "publishing $version ($commit) for osx-arm64..."
"$dotnet" publish "$repo/src/EQ2Parser.Mac" -c Release -r osx-arm64 --self-contained \
    -p:Version="$version" -p:InformationalVersion="$version+$commit" \
    -o "$publish" >"$out/publish.log" 2>&1 \
    || { echo "publish failed, see $out/publish.log"; exit 1; }

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish/." "$app/Contents/MacOS/"

# Icon: the Windows .ico (256 px) scaled into an iconset.
iconset="$out/EQ2Parser.iconset"
mkdir -p "$iconset"
sips -s format png "$repo/src/EQ2Parser.App/Assets/EQ2Parser.ico" --out "$out/icon.png" >/dev/null
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$out/icon.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$out/icon.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/EQ2Parser.icns"

cat >"$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>EQ2Parser</string>
  <key>CFBundleDisplayName</key><string>EQ2Parser</string>
  <key>CFBundleIdentifier</key><string>io.github.lokla.eq2parser</string>
  <key>CFBundleExecutable</key><string>EQ2Parser</string>
  <key>CFBundleIconFile</key><string>EQ2Parser</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version+$commit</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
EOF

codesign --force --deep --sign - "$app" >/dev/null 2>&1
codesign --verify --deep --strict "$app"
echo "built $app ($(du -sh "$app" | cut -f1))"

if [ "$install" -eq 1 ]; then
    mkdir -p "$HOME/Applications"
    if pgrep -x EQ2Parser >/dev/null; then
        echo "EQ2Parser is running; quit it and run again with --install"; exit 1
    fi
    rm -rf "$HOME/Applications/EQ2Parser.app"
    cp -R "$app" "$HOME/Applications/"
    echo "installed $HOME/Applications/EQ2Parser.app"
fi
