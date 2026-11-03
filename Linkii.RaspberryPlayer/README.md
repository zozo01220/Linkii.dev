# Linkivi — Raspberry Pi OS (64 bits)

Coquille .NET 8 autour de Chromium : elle charge le player web (`Linkii.Poc/wwwroot/player`), comme les coquilles Windows et Android.
Le player garde l'appairage par code, la diffusion et le cache hors ligne (service worker dans le profil de Chromium) ; la coquille ajoute le kiosque,
le démarrage automatique, la relance et le pont `window.LinkiiNative`.

Testé nulle part pour l'instant : à essayer sur un vrai Raspberry Pi (Pi 4 ou 5, Raspberry Pi OS 64 bits avec bureau).

## Installer sur le Pi

1. Raspberry Pi Imager › « Raspberry Pi OS (64-bit) » (avec bureau), réseau et SSH réglés dans l'Imager.
2. Copier `linkivi-pi-<version>.tar.gz` sur le Pi, puis, avec l'utilisateur de la session (sans `sudo`) :
   ```bash
   tar xzf linkivi-pi-0.1.0.tar.gz && cd linkivi-pi-0.1.0
   ./install.sh                    # ou : ./install.sh --server https://autre-serveur
   sudo reboot
   ```
   Le script installe Chromium s'il manque, pose le programme dans `~/.local/share/linkivi-app`, crée l'entrée de démarrage `~/.config/autostart/linkivi.desktop`,
   active la session automatique et désactive la mise en veille de l'écran (`raspi-config`).
3. Au démarrage, le player affiche son code d'appairage : le saisir dans le back-office (« Connecter un écran »).

Serveur par défaut : `https://linkivi.duckdns.org` (en HTTPS : sans cela, pas de service worker, donc pas de mode hors ligne).
Autre serveur : `--server`, ou la variable `LINKII_SERVER`.

## Compiler le paquet (sur le poste de développement)

```powershell
cd Linkii.RaspberryPlayer
.\build-package.ps1             # artifacts\linkivi-pi-0.1.0.tar.gz (autonome, arm64)
```

## Fonctionnement

- Un seul processus : il lance Chromium (`--kiosk`, port de débogage local 9222, profil `~/.local/share/linkivi/chromium`), s'y connecte, injecte `LinkiiNative` puis charge `/player/`.
- Relance : Chromium arrêté ou planté (pause de 2 s, 30 s si cela se répète), page qui ne répond plus pendant une minute, redémarrage demandé par le back-office.
- Serveur injoignable au démarrage : nouvelle tentative toutes les 10 s.
- Pont `window.LinkiiNative` : `deviceInfo` (type `raspberrypi`, système, modèle de carte, version, démarrage), `restart`, `config` (gardé dans `settings.json`), `capture`.
  La capture est celle de la page (Chromium), envoyée en `?plain=true`, avec le jeton de l'écran. Pas de `sync` : le player reste en mode navigateur.
- Journal : `~/.local/share/linkivi/player.log`.

## Pas encore fait

- Veille matérielle (écran éteint) : le player passe en écran noir sur ses plages, mais l'écran reste allumé (à faire avec `wlr-randr` ou `vcgencmd display_power`).
- Luminosité, menu technicien (PIN), médiathèque native, mode borne.
- Écran supprimé dans le back-office : le player revient au code d'appairage (pas de désinstallation).
- Démarrage automatique : réglé depuis le back-office (paramètres de l écran › Appareil), appliqué au prochain démarrage de la session (start.sh lit settings.json).
- Téléchargement depuis le back-office (`/download/...`) et choix « Raspberry Pi » dans « Connecter un écran ».
- Plusieurs écrans sur un même Pi.
- Vidéo : un Pi 5 n'a plus de décodeur H.264 matériel (le logiciel suffit en 1080p) ; éviter la 4K.
