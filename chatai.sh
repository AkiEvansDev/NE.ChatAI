#!/usr/bin/env bash
set -euo pipefail

# Default port (change here, export CHATAI_PORT, or pass as argument: ./chatai.sh 8080 / ./chatai.sh --port 8080)
DEFAULT_PORT=5090
REPO="${CHATAI_REPO:-AkiEvansDev/NE.ChatAI}"
ASSET_NAME="NE.ChatAI-linux-x64.tar.gz"

PORT="${CHATAI_PORT:-$DEFAULT_PORT}"
OPEN_BROWSER=1
CHECK_UPDATE=1

while [ $# -gt 0 ]; do
    case "$1" in
        --port|-p)
            if [ $# -lt 2 ]; then
                echo "Missing port value after $1" >&2
                exit 1
            fi
            PORT="$2"
            shift 2
            ;;
        --no-browser)
            OPEN_BROWSER=0
            shift
            ;;
        --no-update)
            CHECK_UPDATE=0
            shift
            ;;
        *)
            PORT="$1"
            shift
            ;;
    esac
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$ROOT/app"
DATA_DIR="$ROOT/data"
VERSION_FILE="$ROOT/.version"
BIN_PATH="$APP_DIR/NE.ChatAI.Web"
API_URL="${CHATAI_RELEASE_API:-https://api.github.com/repos/$REPO/releases/latest}"

if [ "$CHECK_UPDATE" = "1" ]; then
    INSTALLED=""
    if [ -f "$VERSION_FILE" ]; then
        INSTALLED="$(tr -d '\r\n ' < "$VERSION_FILE")"
    fi

    if RELEASE_JSON="$(curl -fsSL --connect-timeout 6 -H "User-Agent: NE.ChatAI-Launcher" -H "Accept: application/vnd.github+json" "$API_URL" 2>/dev/null)"; then
        LATEST="$(printf '%s\n' "$RELEASE_JSON" | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)"
        DOWNLOAD_URL="$(printf '%s\n' "$RELEASE_JSON" | grep -oE '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*'"$ASSET_NAME"'"' | sed -n 's/.*"browser_download_url"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1 || true)"

        if [ -n "$LATEST" ] && [ -n "$DOWNLOAD_URL" ] && { [ ! -x "$BIN_PATH" ] || [ "$INSTALLED" != "$LATEST" ]; }; then
            if [ -x "$BIN_PATH" ]; then
                echo "Updating NE.ChatAI (${INSTALLED:-local} -> $LATEST)..."
            else
                echo "Installing NE.ChatAI $LATEST..."
            fi

            TMP_ARCHIVE="$(mktemp "${TMPDIR:-/tmp}/chatai-XXXXXX.tar.gz")"
            STAGING="$ROOT/.update-staging"
            rm -rf "$STAGING"
            mkdir -p "$STAGING"

            cleanup() {
                rm -f "$TMP_ARCHIVE"
                rm -rf "$STAGING"
            }
            trap cleanup EXIT

            curl -fsSL -H "User-Agent: NE.ChatAI-Launcher" "$DOWNLOAD_URL" -o "$TMP_ARCHIVE"
            tar -xzf "$TMP_ARCHIVE" -C "$STAGING"

            rm -rf "$APP_DIR"
            if [ -d "$STAGING/app" ]; then
                mv "$STAGING/app" "$APP_DIR"
            else
                mv "$STAGING" "$APP_DIR"
            fi
            chmod +x "$BIN_PATH"
            printf '%s\n' "$LATEST" > "$VERSION_FILE"

            trap - EXIT
            cleanup
            echo "Ready: $LATEST"
        fi
    else
        if [ -x "$BIN_PATH" ]; then
            echo "Update check skipped (unable to reach $API_URL)."
        else
            echo "Failed to download release from $API_URL and no local build found at $BIN_PATH." >&2
            exit 1
        fi
    fi
fi

if [ ! -x "$BIN_PATH" ]; then
    echo "NE.ChatAI binary not found at $BIN_PATH." >&2
    exit 1
fi

mkdir -p "$DATA_DIR"

export ASPNETCORE_ENVIRONMENT="Production"
export ASPNETCORE_URLS="http://0.0.0.0:$PORT"
export ChatAI__DataPath="$DATA_DIR"

echo ""
echo "NE.ChatAI is starting on port $PORT"
echo "  Local: http://localhost:$PORT"
if command -v hostname >/dev/null 2>&1; then
    for ip in $(hostname -I 2>/dev/null || true); do
        echo "  LAN:   http://$ip:$PORT"
    done
fi
echo "  Data:  $DATA_DIR"
echo ""

if [ "$OPEN_BROWSER" = "1" ] && { [ -n "${DISPLAY:-}" ] || [ -n "${WAYLAND_DISPLAY:-}" ]; } && command -v xdg-open >/dev/null 2>&1; then
    xdg-open "http://localhost:$PORT" >/dev/null 2>&1 || true
fi

cd "$APP_DIR"
exec "$BIN_PATH"
