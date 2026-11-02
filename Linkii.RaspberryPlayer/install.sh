#!/bin/bash
# Linkivi pour Raspberry Pi OS (64 bits) : à lancer depuis le dossier décompressé, avec l'utilisateur qui affichera l'écran (pas root).
#   ./install.sh [--server https://mon-serveur]
set -e

if [ "$(id -u)" = "0" ]; then echo "Lancez ce script avec l'utilisateur de la session, sans sudo."; exit 1; fi
HERE="$(cd "$(dirname "$0")" && pwd)"
DEST="$HOME/.local/share/linkivi-app"
SERVER=""
[ "$1" = "--server" ] && SERVER="$2"

# Chromium (inclus dans Raspberry Pi OS avec bureau ; installé sinon)
if ! command -v chromium-browser >/dev/null && ! command -v chromium >/dev/null; then
  sudo apt-get update && sudo apt-get install -y chromium-browser || sudo apt-get install -y chromium
fi

# Programme
mkdir -p "$DEST"
cp "$HERE/linkivi-pi" "$DEST/linkivi-pi"
chmod +x "$DEST/linkivi-pi"

# Démarrage avec la session du bureau (fonctionne sous labwc, Wayfire et X11)
mkdir -p "$HOME/.config/autostart"
EXEC="$DEST/linkivi-pi"
[ -n "$SERVER" ] && EXEC="$EXEC --server $SERVER"
cat > "$HOME/.config/autostart/linkivi.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Linkivi
Exec=$EXEC
X-GNOME-Autostart-enabled=true
EOF

# Session ouverte seule au démarrage, écran jamais mis en veille
if command -v raspi-config >/dev/null; then
  sudo raspi-config nonint do_boot_behaviour B4 || true   # bureau, ouverture de session automatique
  sudo raspi-config nonint do_blanking 1 || true          # pas de mise en veille de l'écran
fi

echo "Linkivi installé. Redémarrez (sudo reboot) : le code d'appairage s'affiche au démarrage."
