#!/bin/sh
# Puts KSF Companion (this folder - it runs from here, so keep it) in your app menu and on your PATH.
#   ./install.sh            add the menu entry and ~/.local/bin/ksf-companion
#   ./install.sh --remove   take them out again (first use the tray's "Remove from CS:S..." to take it out of the game)
set -eu

here=$(cd "$(dirname "$0")" && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}
entry=$data/applications/ksf-companion.desktop
icon=$data/icons/hicolor/256x256/apps/ksf-companion.png
link=$HOME/.local/bin/ksf-companion

if [ "${1:-}" = "--remove" ]; then
    rm -f "$entry" "$icon" "$link"
    echo "Removed the menu entry. Your settings and play-later list stay in ~/.config/ksf-companion."
    exit 0
fi

mkdir -p "$(dirname "$entry")" "$(dirname "$icon")" "$(dirname "$link")"
cp "$here/icon.png" "$icon"
ln -sf "$here/ksf-companion" "$link"
cat > "$entry" <<EOF
[Desktop Entry]
Type=Application
Name=KSF Companion
GenericName=Surf dashboard
Comment=KSF surf dashboard for Counter-Strike: Source
Exec="$here/ksf-companion"
Icon=ksf-companion
StartupWMClass=ksf-companion
Categories=Game;
Keywords=ksf;surf;counter-strike;css;
Terminal=false
EOF
echo "KSF Companion is in your app menu now (and in ~/.local/bin as ksf-companion)."
