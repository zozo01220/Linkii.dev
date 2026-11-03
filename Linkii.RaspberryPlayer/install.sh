#!/bin/bash
# Linkivi pour Raspberry Pi OS (64 bits) : à lancer depuis le dossier décompressé, avec l'utilisateur qui affichera l'écran (pas root).
#   ./install.sh [--server https://mon-serveur] [--keyboard ch]
set -e

if [ "$(id -u)" = "0" ]; then echo "Lancez ce script avec l'utilisateur de la session, sans sudo."; exit 1; fi
HERE="$(cd "$(dirname "$0")" && pwd)"
DEST="$HOME/.local/share/linkivi-app"
SERVER=""
KEYBOARD=""
while [ $# -gt 0 ]; do
  case "$1" in
    --server) SERVER="$2"; shift 2 ;;
    --keyboard) KEYBOARD="$2"; shift 2 ;;
    *) shift ;;
  esac
done

# Chromium (inclus dans Raspberry Pi OS avec bureau ; installé sinon)
if ! command -v chromium-browser >/dev/null && ! command -v chromium >/dev/null; then
  sudo apt-get update && sudo apt-get install -y chromium-browser || sudo apt-get install -y chromium
fi

# Programme
mkdir -p "$DEST"
cp "$HERE/linkivi-pi" "$DEST/linkivi-pi.new"      # copie puis remplacement : une mise à jour marche aussi quand le player tourne
chmod +x "$DEST/linkivi-pi.new"
mv -f "$DEST/linkivi-pi.new" "$DEST/linkivi-pi"

# Démarrage avec la session du bureau (fonctionne sous labwc, Wayfire et X11)
mkdir -p "$HOME/.config/autostart"
# Lanceur de la session : le démarrage automatique se règle dans le back-office (paramètres de l'écran › Appareil).
# Le player en garde le réglage dans settings.json ; coupé, la session n'ouvre pas le player (par défaut : il démarre).
cat > "$DEST/start.sh" <<'EOF'
#!/bin/sh
SETTINGS="${XDG_DATA_HOME:-$HOME/.local/share}/linkivi/settings.json"
if [ -f "$SETTINGS" ] && grep -Eq '"autoStart"[[:space:]]*:[[:space:]]*false' "$SETTINGS"; then exit 0; fi
exec "$(dirname "$0")/linkivi-pi" "$@"
EOF
chmod +x "$DEST/start.sh"

EXEC="$DEST/start.sh"
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

# Commandes du back-office : redémarrer et éteindre l'appareil sans mot de passe, ces deux commandes seulement
RULE="/etc/sudoers.d/linkivi"
echo "$USER ALL=(root) NOPASSWD: /usr/bin/systemctl reboot, /usr/bin/systemctl poweroff" | sudo tee "$RULE" >/dev/null
sudo chmod 440 "$RULE"
sudo visudo -cf "$RULE" >/dev/null || { sudo rm -f "$RULE"; echo "Règle sudo refusée : redémarrage et extinction à distance indisponibles."; }

# Poste dédié à l'affichage : ni curseur, ni assistant de bienvenue (pays, langue, clavier), ni barre des tâches (indicateur de clavier), ni notifications
sudo rm -f /etc/xdg/autostart/piwiz.desktop 2>/dev/null || true       # assistant de bienvenue : choix de la langue et du clavier
sudo apt-get install -y unclutter-xfixes 2>/dev/null || sudo apt-get install -y unclutter 2>/dev/null || true
cat > "$HOME/.config/autostart/linkivi-curseur.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Linkivi - curseur masque
Exec=sh -c 'command -v unclutter >/dev/null && unclutter -idle 0 -root'
X-GNOME-Autostart-enabled=true
EOF
# labwc (Raspberry Pi OS Bookworm) : la session ne lance que Linkivi (la version d'origine est gardée dans autostart.avant-linkivi)
if command -v labwc >/dev/null; then
  mkdir -p "$HOME/.config/labwc"
  [ -f "$HOME/.config/labwc/autostart" ] && [ ! -f "$HOME/.config/labwc/autostart.avant-linkivi" ] && cp "$HOME/.config/labwc/autostart" "$HOME/.config/labwc/autostart.avant-linkivi"
  echo "$EXEC &" > "$HOME/.config/labwc/autostart"
fi
# Wayfire : barre des tâches retirée
if [ -f "$HOME/.config/wayfire.ini" ] || command -v wayfire >/dev/null; then
  mkdir -p "$HOME/.config/autostart"
  printf '[Desktop Entry]\nType=Application\nName=Barre\nHidden=true\n' > "$HOME/.config/autostart/wf-panel-pi.desktop"
fi
# Clavier : choisir sa disposition une fois pour toutes (./install.sh --keyboard ch), au lieu de la demander sur l'écran
if [ -n "$KEYBOARD" ] && command -v raspi-config >/dev/null; then sudo raspi-config nonint do_configure_keyboard "$KEYBOARD" || true; fi

echo "Linkivi installé. Redémarrez (sudo reboot) : le code d'appairage s'affiche au démarrage."
