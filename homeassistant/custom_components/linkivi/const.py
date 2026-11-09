"""Constantes de l'intégration Linkivi."""

from datetime import timedelta

DOMAIN = "linkivi"

# Carte personnalisée « Écrans Linkivi » (www/linkivi-screens-card.js), publiée sous /linkivi/ et chargée par l'interface.
CARD_FILE = "linkivi-screens-card.js"
CARD_URL = f"/{DOMAIN}/{CARD_FILE}"
CARD_VERSION = "1.2.0"

# L'API publique de Linkivi (§154) : lecture des écrans toutes les 30 s (limite de 60 requêtes par minute et par clé).
SCAN_INTERVAL = timedelta(seconds=30)
API_PATH = "/api/v1"

# Commandes acceptées par POST /screens/{id}/commands (module « Écrans », accès screens:control).
CMD_RESTART_PLAYER = "restart-player"
CMD_REBOOT = "reboot"
CMD_POWER_OFF = "poweroff"
CMD_SCREEN_OFF = "screen-off"
CMD_WAKE = "wake"

DEVICE_LABELS = {
    "windows": "Player Windows",
    "raspberrypi": "Player Raspberry Pi",
    "android": "Player Android",
    "androidtv": "Player Android TV",
    "web": "Player navigateur",
}
