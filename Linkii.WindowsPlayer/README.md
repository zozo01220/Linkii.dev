# Linkii Player — Windows

Coquille .NET 8 (WPF) autour de WebView2 : elle charge le player web (`Linkii.Poc/wwwroot/player`). Le player garde l'appairage par code, la diffusion
et le cache hors ligne ; la coquille ajoute le plein écran, le démarrage automatique, l'anti-veille, la relance et les infos d'appareil.
Phase 1 : veille et luminosité réelles, capture, menu technicien et médiathèque native viendront ensuite.

## Instances (plusieurs players sur le même PC)

Une instance = un nom, un profil de navigateur (jeton d'appairage, playlist, cache), une fenêtre. Données : `%LOCALAPPDATA%\Linkii\Player\<instance>\`.
Chaque instance s'appaire comme un écran à part dans le back-office.

```
LinkiiPlayer.exe --instance salle-1 --server https://affichage.exemple.ch --window 0,0,960,540   # fenêtre de test
LinkiiPlayer.exe --instance mur-1 --monitor 2                                                    # plein écran sur le moniteur 2
LinkiiPlayer.exe --list-monitors                                                                 # numéros des moniteurs
```

Options : `--instance`, `--server` (gardée ensuite), `--monitor N` (1 = principal, puis de gauche à droite), `--window X,Y,L,H` (pixels), `--windowed`,
`--autostart on|off`, `--reset` (oublie l'appairage), `--devtools`, `--no-supervisor`, `--help`.
Mur d'écrans sur un PC : une instance par moniteur, chacune appairée comme un écran du mur.

Adresse du serveur, par ordre de priorité : `--server`, celle gardée par l'instance, celle posée par l'installeur (`HKCU\Software\Linkii\Player\ServerUrl`), sinon demandée au premier lancement.

## Fonctionnement

- Un lancement démarre un **superviseur** sans fenêtre qui lance le processus d'affichage (`--child`) et le relance : plantage (pause de 2 s, 30 s si cela se répète), fenêtre figée pendant une minute, redémarrage demandé par le back-office. Fermeture à la main (Alt+F4) : le superviseur s'arrête aussi.
- Pont `window.LinkiiNative` : `deviceInfo` (type `windows`, système, modèle · instance, version) et `restart`. `sync` n'est pas défini : le player reste en mode navigateur (cache du navigateur, indicateur de publication JS). S'il est défini un jour, le player passe en mode natif.
- Anti-veille : l'écran et la session restent éveillés tant que le player tourne.
- Serveur injoignable au démarrage : page d'attente et nouvelle tentative toutes les 10 s.
- Journal : `player.log` dans le dossier de l'instance.

## Installeur

```powershell
.\build-installer.ps1 -Version 0.1.0      # artifacts\LinkiiPlayer-0.1.0-x64.msi (autonome, environ 50 Mo)
```

Installation pour l'utilisateur courant, sans droits d'administrateur (`%LOCALAPPDATA%\Programs\Linkii Player`, raccourci dans le menu Démarrer).
Options silencieuses : `msiexec /i LinkiiPlayer-0.1.0-x64.msi /qn SERVERURL=https://… AUTOSTART=1` (démarrage avec la session pour l'instance `default`).
Prérequis : Windows 10 ou 11 avec Microsoft Edge WebView2 Runtime (présent sur Windows 11 et Windows 10 à jour).
Non signé pour l'instant : SmartScreen peut avertir au premier lancement.
