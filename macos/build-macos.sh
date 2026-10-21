#!/usr/bin/env bash
set -euo pipefail

RID="${1:-osx-arm64}"
case "$RID" in
  osx-arm64|osx-x64) ;;
  *) echo "Usage: $0 [osx-arm64|osx-x64]" >&2; exit 2 ;;
esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/macos/CodexPond.Mac/CodexPond.Mac.csproj"
BUILD_STAMP="$(date +%Y%m%d-%H%M%S)-$$"
OUTPUT_DIR="$REPO_ROOT/artifacts/macos/$RID-$BUILD_STAMP"
PUBLISH_DIR="$OUTPUT_DIR/publish"
APP_DIR="$OUTPUT_DIR/Codex Pond.app"

mkdir -p "$PUBLISH_DIR" "$APP_DIR/Contents/MacOS"

dotnet publish "$PROJECT" \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  -p:PublishSingleFile=false \
  --output "$PUBLISH_DIR"

cp -R "$PUBLISH_DIR/." "$APP_DIR/Contents/MacOS/"
cp "$SCRIPT_DIR/Info.plist" "$APP_DIR/Contents/Info.plist"
chmod +x "$APP_DIR/Contents/MacOS/CodexPond.Mac"

echo "Created: $APP_DIR"
echo "This app bundle is unsigned. Sign and notarize it before distributing outside development."
