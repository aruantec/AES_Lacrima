#!/usr/bin/env bash
set -euo pipefail

PUBLISH_DIR="${1:-}"
OUTPUT_DIR="${2:-}"
TARGET_ARCH="${3:-}"

if [[ -z "$PUBLISH_DIR" || -z "$OUTPUT_DIR" ]]; then
  echo "usage: $0 <publish-dir> <output-dir> [x86_64|aarch64]" >&2
  exit 1
fi

PUBLISH_DIR="$(realpath "$PUBLISH_DIR")"
OUTPUT_DIR="$(realpath -m "$OUTPUT_DIR")"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_NAME="AES-Lacrima"
EXECUTABLE_NAME="AES_Lacrima"
DESKTOP_TEMPLATE="$PROJECT_DIR/Linux/aes-lacrima.desktop"
ICON_FILE="$PROJECT_DIR/Assets/AES.png"

if [[ ! -d "$PUBLISH_DIR" ]]; then
  echo "publish directory not found: $PUBLISH_DIR" >&2
  exit 1
fi

if [[ ! -f "$PUBLISH_DIR/$EXECUTABLE_NAME" ]]; then
  echo "published executable not found: $PUBLISH_DIR/$EXECUTABLE_NAME" >&2
  exit 1
fi

if [[ ! -f "$DESKTOP_TEMPLATE" ]]; then
  echo "desktop template not found: $DESKTOP_TEMPLATE" >&2
  exit 1
fi

if [[ ! -f "$ICON_FILE" ]]; then
  echo "icon not found: $ICON_FILE" >&2
  exit 1
fi

if [[ -z "$TARGET_ARCH" ]]; then
  case "$(uname -m)" in
    x86_64|amd64)
      TARGET_ARCH="x86_64"
      ;;
    aarch64|arm64)
      TARGET_ARCH="aarch64"
      ;;
    *)
      echo "unsupported architecture: $(uname -m)" >&2
      exit 1
      ;;
  esac
fi

case "$TARGET_ARCH" in
  x86_64|aarch64)
    ;;
  *)
    echo "unsupported AppImage architecture: $TARGET_ARCH" >&2
    exit 1
    ;;
esac

if [[ -n "${APPIMAGETOOL:-}" ]]; then
  APPIMAGETOOL_BIN="$APPIMAGETOOL"
elif command -v appimagetool >/dev/null 2>&1; then
  APPIMAGETOOL_BIN="$(command -v appimagetool)"
else
  echo "appimagetool not found. Set APPIMAGETOOL or add it to PATH." >&2
  exit 1
fi

APPIMAGETOOL_BIN="$(realpath "$APPIMAGETOOL_BIN")"
mkdir -p "$OUTPUT_DIR"

WORK_DIR="$(mktemp -d)"
APPDIR="$WORK_DIR/${APP_NAME}.AppDir"
cleanup() {
  rm -rf "$WORK_DIR"
}
trap cleanup EXIT

mkdir -p \
  "$APPDIR/usr/bin" \
  "$APPDIR/usr/share/applications" \
  "$APPDIR/usr/share/icons/hicolor/256x256/apps"

cp -a "$PUBLISH_DIR/." "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/$EXECUTABLE_NAME"
rm -f "$APPDIR/usr/bin/Linux/install-desktop-entry.sh"

DESKTOP_FILE="$WORK_DIR/aes-lacrima.desktop"
sed \
  -e 's|__EXEC__|AES_Lacrima|g' \
  -e 's|__ICON__|aes-lacrima|g' \
  "$DESKTOP_TEMPLATE" > "$DESKTOP_FILE"

cp "$DESKTOP_FILE" "$APPDIR/aes-lacrima.desktop"
cp "$DESKTOP_FILE" "$APPDIR/usr/share/applications/aes-lacrima.desktop"
cp "$ICON_FILE" "$APPDIR/aes-lacrima.png"
cp "$ICON_FILE" "$APPDIR/usr/share/icons/hicolor/256x256/apps/aes-lacrima.png"

cat > "$APPDIR/AppRun" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
APPDIR="$(cd "$(dirname "$0")" && pwd)"
exec "$APPDIR/usr/bin/AES_Lacrima" "$@"
EOF
chmod +x "$APPDIR/AppRun"

OUTPUT_FILE="$OUTPUT_DIR/${APP_NAME}-${TARGET_ARCH}.AppImage"
export ARCH="$TARGET_ARCH"
export VERSION="${VERSION:-${GITHUB_REF_NAME:-dev}}"
export APPIMAGE_EXTRACT_AND_RUN=1

"$APPIMAGETOOL_BIN" --no-appstream "$APPDIR" "$OUTPUT_FILE"
chmod +x "$OUTPUT_FILE"

echo "Created AppImage: $OUTPUT_FILE"
