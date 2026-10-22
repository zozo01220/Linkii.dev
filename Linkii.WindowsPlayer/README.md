# Linkii Player — Windows

Coquille .NET 8 (WPF) autour de WebView2 : elle charge le player web (`Linkii.Poc/wwwroot/player`). Le player garde l'appairage par code, la diffusion
et le cache hors ligne ; la coquille ajoute le plein écran, le démarrage automatique, l'anti-veille, la relance et les infos d'appareil.
Veille et luminosité réelles, capture, menu technicien et médiathèque native viendront ensuite.

## Rien à régler sur le PC

On installe, le player démarre et affiche son code d'appairage. Tout le reste se règle dans le back-office, **Paramètres de l'écran › Appareil**,
et arrive sur le PC à chaque connexion, sans publication (gardé localement pour démarrer hors ligne) :

- **Démarrage automatique** (oui par défaut ; valeur des nouveaux écrans dans Réglages › Contenus et écrans) : entrée `LinkiiPlayer-<instance>` de la clé Run de l'utilisateur.
- **Afficher dans une fenêtre** (non par défaut) : fenêtre à la taille de la résolution de l'écran, réduite si le moniteur est plus petit ; sinon plein écran.
- **Moniteur** : proposé quand le PC en a plusieurs (1 = principal, puis de gauche à droite).

Adresse du serveur : intégrée (`http://localhost:5080` pour l'instant, constante `InstanceStore.DefaultServer` et propriété `SERVERURL` de l'installeur),
à changer au passage sur le serveur de production.

## Plusieurs écrans sur un même PC

Dans le back-office, menu ⋮ d'un écran Windows › **Ajouter un écran sur ce PC** : le nouvel écran est créé déjà appairé (nom, moniteur, fenêtre),
il hérite de la zone et de la liste de lecture. Le PC le reçoit avec sa configuration et l'ouvre seul : une instance de plus
(`%LOCALAPPDATA%\Linkii\Player\e-xxxxxxxx\`, profil de navigateur à part, jeton posé avant le premier chargement). Supprimer l'écran ferme sa fenêtre
et efface l'instance. Les écrans d'un même PC portent la pastille « PC partagé · N écrans ».

## Fonctionnement

- Un lancement démarre un **superviseur** sans fenêtre qui lance le processus d'affichage (`--child`) et le relance : plantage (pause de 2 s, 30 s si cela se répète), fenêtre figée pendant une minute, redémarrage demandé par le back-office. Fermeture à la main (Alt+F4) : le superviseur s'arrête aussi.
- Pont `window.LinkiiNative` : `deviceInfo` (type `windows`, système, modèle, version, identifiant du PC, instance, moniteurs), `restart`, `config` (paramètres de l'appareil) et `unpaired` (écran supprimé). `sync` n'est pas défini : le player reste en mode navigateur (cache du navigateur, indicateur de publication JS).
- Anti-veille : l'écran et la session restent éveillés tant que le player tourne.
- Serveur injoignable au démarrage : page d'attente et nouvelle tentative toutes les 10 s.
- Journal : `player.log` dans le dossier de l'instance ; paramètres reçus : `settings.json`.
- Arguments internes seulement (posés par le player lui-même) : `--instance`, `--child`, `--uninstall`, et `--devtools` pour le débogage.

## Installeur

```powershell
.\build-installer.ps1 -Version 0.2.0      # artifacts\LinkiiPlayer-0.2.0-x64.msi (autonome, environ 50 Mo)
```

Installation pour l'utilisateur courant, sans droits d'administrateur (`%LOCALAPPDATA%\Programs\Linkii Player`, raccourci dans le menu Démarrer) ;
le player démarre à la fin de l'installation. La désinstallation arrête les instances et retire leurs démarrages automatiques.
Prérequis : Windows 10 ou 11 avec Microsoft Edge WebView2 Runtime (présent sur Windows 11 et Windows 10 à jour).
Non signé pour l'instant : SmartScreen peut avertir au premier lancement.
