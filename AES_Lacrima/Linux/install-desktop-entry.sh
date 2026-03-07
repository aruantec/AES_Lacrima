#!/usr/bin/env bash
set -euo pipefail

PUBLISH_DIR="${1:-$(pwd)}"
PUBLISH_DIR="$(realpath "$PUBLISH_DIR")"

APP_BIN="$PUBLISH_DIR/AES_Lacrima"
ICON_FILE="$PUBLISH_DIR/Assets/AES.png"
TEMPLATE_FILE="$PUBLISH_DIR/Linux/aes-lacrima.desktop"

DESKTOP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
DESKTOP_FILE="$DESKTOP_DIR/aes-lacrima.desktop"
ICON_BASE_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor"
ICON_256_DIR="$ICON_BASE_DIR/256x256/apps"
ICON_128_DIR="$ICON_BASE_DIR/128x128/apps"
ICON_64_DIR="$ICON_BASE_DIR/64x64/apps"
ICON_48_DIR="$ICON_BASE_DIR/48x48/apps"
ICON_32_DIR="$ICON_BASE_DIR/32x32/apps"
ICON_NAME="aes-lacrima"

if [[ ! -f "$APP_BIN" ]]; then
  echo "Binary not found: $APP_BIN" >&2
  exit 1
fi

if [[ ! -f "$ICON_FILE" ]]; then
  echo "Icon not found: $ICON_FILE" >&2
  exit 1
fi

if [[ ! -f "$TEMPLATE_FILE" ]]; then
  echo "Desktop template not found: $TEMPLATE_FILE" >&2
  exit 1
fi

mkdir -p "$DESKTOP_DIR"
mkdir -p "$ICON_256_DIR" "$ICON_128_DIR" "$ICON_64_DIR" "$ICON_48_DIR" "$ICON_32_DIR"

ESCAPED_EXEC="$(printf '%q' "$APP_BIN")"

sed -e "s|__EXEC__|$ESCAPED_EXEC|g" "$TEMPLATE_FILE" > "$DESKTOP_FILE"
chmod 644 "$DESKTOP_FILE"

# Install icon into the user icon theme under a stable icon name.
cp -f "$ICON_FILE" "$ICON_256_DIR/$ICON_NAME.png"
if command -v ffmpeg >/dev/null 2>&1; then
  ffmpeg -y -i "$ICON_FILE" -vf scale=128:128 -frames:v 1 -update 1 "$ICON_128_DIR/$ICON_NAME.png" >/dev/null 2>&1 || true
  ffmpeg -y -i "$ICON_FILE" -vf scale=64:64 -frames:v 1 -update 1 "$ICON_64_DIR/$ICON_NAME.png" >/dev/null 2>&1 || true
  ffmpeg -y -i "$ICON_FILE" -vf scale=48:48 -frames:v 1 -update 1 "$ICON_48_DIR/$ICON_NAME.png" >/dev/null 2>&1 || true
  ffmpeg -y -i "$ICON_FILE" -vf scale=32:32 -frames:v 1 -update 1 "$ICON_32_DIR/$ICON_NAME.png" >/dev/null 2>&1 || true
fi

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$DESKTOP_DIR" >/dev/null 2>&1 || true
fi

# Refresh icon cache where available.
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f -t "$ICON_BASE_DIR" >/dev/null 2>&1 || true
fi

# Optional: Set a custom icon on the raw ELF file for Nautilus/GNOME.
if command -v gio >/dev/null 2>&1; then
  gio set "$APP_BIN" metadata::custom-icon "file://$ICON_FILE" >/dev/null 2>&1 || true
fi

echo "Installed desktop entry: $DESKTOP_FILE"
echo "App binary: $APP_BIN"
echo "Icon name: $ICON_NAME"
