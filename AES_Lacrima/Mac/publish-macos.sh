#!/usr/bin/env bash
# publish-macos.sh - Post-publish script to package AES_Lacrima as a .app bundle
#
# This script is called automatically by a target in AES_Lacrima.csproj
# after running `dotnet publish`. It will:
#   1. create a macOS application bundle at the publish location
#   2. move all published binaries/dependencies into the .app bundle
#   3. convert the PNG icon into an .icns file and embed it
#   4. generate a minimal Info.plist describing the bundle
#   5. clean up the raw published files to leave only the .app
#
# The result is a self-contained macOS application that doesn't open a terminal.

set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_NAME="AES - Lacrima"
EXECUTABLE_NAME="AES_Lacrima"
BUNDLE_ID="com.aruantec.aes-lacrima"
MIN_SYS_VER="12.3"
APP_VERSION="${APP_VERSION:-1.0.0}"
APP_VERSION="${APP_VERSION#v}"

PUBLISH_DIR="${1:-$(pwd)}"
PUBLISH_DIR="$(realpath "$PUBLISH_DIR")"
BUNDLE_DIR="$PUBLISH_DIR/$APP_NAME.app"

echo "Creating bundle at $BUNDLE_DIR"
rm -rf "$BUNDLE_DIR"
mkdir -p "$BUNDLE_DIR/Contents/MacOS" "$BUNDLE_DIR/Contents/Resources"

# 1. Move all published files into the bundle (excluding the bundle itself)
find "$PUBLISH_DIR" -mindepth 1 -maxdepth 1 ! -name "$APP_NAME.app" \
  -exec cp -R {} "$BUNDLE_DIR/Contents/MacOS/" \;

# Remove Linux-specific assets from the macOS bundle
rm -rf "$BUNDLE_DIR/Contents/MacOS/Linux"

if [[ ! -f "$BUNDLE_DIR/Contents/MacOS/$EXECUTABLE_NAME" ]]; then
    echo "error: published executable not found in $PUBLISH_DIR" >&2
    exit 1
fi
chmod +x "$BUNDLE_DIR/Contents/MacOS/$EXECUTABLE_NAME"

# 2. Write Info.plist
cat > "$BUNDLE_DIR/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0 //EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleExecutable</key>
    <string>$EXECUTABLE_NAME</string>
    <key>CFBundleVersion</key>
    <string>$APP_VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$APP_VERSION</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>LSMinimumSystemVersion</key>
    <string>$MIN_SYS_VER</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
</dict>
</plist>
EOF

# 3. Create icon
ICON_PNG="$BUNDLE_DIR/Contents/MacOS/Assets/AES.png"
if [[ ! -f "$ICON_PNG" && -f "$BUNDLE_DIR/Contents/MacOS/AES.png" ]]; then
    ICON_PNG="$BUNDLE_DIR/Contents/MacOS/AES.png"
fi
if [[ ! -f "$ICON_PNG" && -f "$PROJECT_DIR/Assets/AES.png" ]]; then
    ICON_PNG="$PROJECT_DIR/Assets/AES.png"
fi

if [[ -f "$ICON_PNG" ]]; then
    echo "Converting icon to AppIcon.icns from $ICON_PNG"
    ICONSET="$BUNDLE_DIR/Contents/Resources/AppIcon.iconset"
    rm -rf "$ICONSET"
    mkdir -p "$ICONSET"

    for size in 16 32 64 128 256 512; do
        sips -z $size $size "$ICON_PNG" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
        sips -z $((size*2)) $((size*2)) "$ICON_PNG" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
    done

    iconutil -c icns "$ICONSET" -o "$BUNDLE_DIR/Contents/Resources/AppIcon.icns"
    rm -rf "$ICONSET"
else
    echo "warning: icon image not found; bundle will not have custom icon" >&2
fi

# 4. Clean up the original publish directory so dependencies are not "lying around"
find "$PUBLISH_DIR" -mindepth 1 -maxdepth 1 ! -name "$APP_NAME.app" -exec rm -rf {} +

# 5. Ad-hoc sign the bundle (required for Apple Silicon gatekeeper to allow execution)
echo "Ad-hoc signing the application bundle for Apple Silicon..."
codesign --force --deep --sign - "$BUNDLE_DIR"

echo "=== macOS bundle ready: $BUNDLE_DIR ==="
echo "You can move it to /Applications or double-click it."
