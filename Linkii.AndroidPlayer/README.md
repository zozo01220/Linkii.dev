# Linkii Player — application Android

Coquille native autour du player web (`Linkii.Poc/wwwroot/player`). Le player garde l'appairage par code, la diffusion
et le cache hors ligne (service worker + Cache Storage) ; l'application ajoute le démarrage automatique, le plein écran
permanent, l'écran toujours allumé, la relance en cas de panne et un menu technicien. Aucune dépendance (pas d'AndroidX).

Visuel validé : `maquettes/player-android.html`.

## Compiler

JDK 17 ou plus (celui d'Android Studio convient), SDK Android 35.

```bash
cd Linkii.AndroidPlayer
./gradlew :app:assembleDebug          # app/build/outputs/apk/debug/linkii-player-debug.apk
./gradlew :app:bundleRelease          # app/build/outputs/bundle/release/linkii-player-release.aab (Google Play)
```

Adresse du serveur par défaut : `-PserverUrl=https://app.exemple.ch` (modifiable ensuite sur l'appareil).
Le serveur doit être en **HTTPS** : sans cela le service worker ne démarre pas et il n'y a pas de mode hors ligne
(seuls `localhost` et `10.0.2.2` sont admis en HTTP, pour le développement).

## Mettre à jour l'app sur un appareil (Android TV, ADB)

Le player (`wwwroot/player`) est servi par le serveur : un changement de `player.js` ne demande qu'un redéploiement. Un changement du code Kotlin
(menu technicien, téléchargements, anneau, veille…) demande de recompiler et de réinstaller l'APK.

1. Compiler avec un JDK 17 ou plus (le JDK par défaut de la machine de développement est le 11) :
   ```bash
   cd Linkii.AndroidPlayer
   JAVA_HOME="C:/Program Files/Android/Android Studio/jbr" ./gradlew :app:assembleDebug
   ```
2. Sur l'appareil : Paramètres › Système › À propos, 7 appuis sur « Version » ; puis Options pour les développeurs › débogage ADB / réseau (redémarrer l'appareil si besoin).
3. Installer depuis PowerShell (le `&` est obligatoire devant un chemin entre guillemets) :
   ```powershell
   $adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
   & $adb connect 192.168.x.x:5555        # accepter « Autoriser le débogage ? » sur l'appareil
   & $adb devices                         # doit afficher « device » (pas « unauthorized »)
   & $adb install -r app\build\outputs\apk\debug\linkii-player-debug.apk
   ```
   `-r` garde l'appairage, les réglages et les médias déjà téléchargés. Erreurs : `INSTALL_FAILED_UPDATE_INCOMPATIBLE` (autre signature : désinstaller d'abord, l'appairage est perdu), `INSTALL_FAILED_VERSION_DOWNGRADE` (ajouter `-d`), `no devices/emulators found` (`connect` pas fait ou ADB réseau inactif).
4. Contrôler : republier l'écran, puis menu technicien (« dont images… vidéos… »), puis couper le réseau.

## Publier sur Google Play

1. Créer une clé d'envoi (`keytool -genkeypair -v -keystore linkii-upload.jks -alias linkii -keyalg RSA -keysize 2048 -validity 10000`) et la garder hors du dépôt.
2. Dans `~/.gradle/gradle.properties` : `LINKII_KEYSTORE`, `LINKII_KEYSTORE_PASSWORD`, `LINKII_KEY_ALIAS`, `LINKII_KEY_PASSWORD`.
3. `./gradlew :app:bundleRelease`, puis envoyer le `.aab` dans la Play Console (compte développeur requis, 25 USD une fois).
4. Fiche : catégorie « Professionnel », politique de confidentialité obligatoire, formulaire « Sécurité des données ».
   Pour Android TV : ajouter des captures 1920×1080 et une bannière 1280×720 (la bannière du dépôt est provisoire).

## Menu technicien

Cinq appuis sur OK (télécommande) ou cinq touchers dans le coin supérieur droit, en moins de 3 secondes.
Code PIN par défaut : 9999 (modifiable depuis le menu ; vide = menu sans code).

Depuis la version 1.3.0, l'organisation peut imposer son code à tous ses écrans (Réglages › Contenus et écrans › Menu technicien). Le player le confie à l'app
(`LinkiiNative.techPin`), qui le garde pour fonctionner hors ligne. Ordre : code propre à l'appareil si l'organisation l'autorise et qu'il en a défini un, sinon code de
l'organisation, sinon code de l'appareil (9999). L'app renvoie une empreinte du code reçu (`pinSig`) : Réglages indique combien d'écrans l'ont appliqué.

## Médias hors ligne

Les fichiers de la liste de lecture (images, vidéos, fichiers des apps : tout ce que le player met dans `url` / `urls`) sont téléchargés
par l'app dans son stockage privé (`MediaStore.kt`), sans quota du WebView ni purge par le système. Téléchargement un fichier à la fois,
avec reprise après coupure, nouvel essai toutes les 30 s et retrait des fichiers qui ne sont plus utilisés ; il reprend dès le démarrage
de l'app. Le WebView lit ensuite ces fichiers depuis le disque (requêtes Range comprises), en ligne comme hors ligne.
Une petite icône de progression (anneau + flèche, puis coche) s'affiche en bas à droite : dès que le player reçoit une nouvelle publication
(`LinkiiNative.working(true)`, anneau qui tourne avant même le premier octet), puis pendant les téléchargements, et jusqu'à ce que la nouvelle liste soit prête.
Le player donne à l'app les fichiers de **toutes les zones** (première zone et zones 2 et 3 d'un écran découpé). Si plus aucun octet n'arrive pendant 90 s
(fichier en échec, espace plein), la nouvelle liste s'applique quand même et l'app réessaie toutes les 30 s.
Le menu technicien indique le nombre de médias en local, dont images et vidéos (nombre et poids), et le dernier échec de téléchargement
(code HTTP, espace insuffisant, erreur réseau). YouTube et les données en direct (agenda…) restent en ligne.
Le player passe par le pont `window.LinkiiNative` ; dans un navigateur il garde son cache habituel.

## Démarrage automatique

- Définir Linkii comme **écran d'accueil** de l'appareil (recommandé sur un boîtier) : lancement direct à l'allumage.
- Ou activer *Autoriser le démarrage automatique* dans le menu technicien (affichage par-dessus les autres applications, requis par Android 10+).
- Verrouillage kiosque (`startLockTask`) : automatique si l'appareil est géré (MDM / propriétaire de l'appareil).

## Version 1.1.0 : contrôle du direct

L'app remonte son modèle, la version Android et la version de l'application, et exécute les commandes du back-office (licence Growth) : aperçu du direct (`Capture.kt`, test `CaptureTest.kt`), redémarrage de l'application (`RestartActivity.kt`), pause, rechargement. À valider sur un appareil réel.
## Version 1.2.0 : mise en veille et luminosité

- **Mise en veille** (`SleepController.kt`) : le player confie les plages du back-office à l'app (`LinkiiNative.sleepSchedule`) ; l'appareil les applique seul, par alarme exacte, même si le WebView est suspendu ou le réseau coupé. Début de plage : écran verrouillé si l'app est administrateur de l'appareil (`SleepAdmin`), sinon repli écran noir et luminosité minimale. Fin : écran rallumé et app au premier plan.
  Activer le droit : menu technicien › *Activer la mise en veille de l'écran*, ou, si l'appareil n'affiche pas la confirmation, depuis un ordinateur relié en ADB :
  `adb shell dpm set-active-admin ch.linkii.player/.SleepAdmin`. Le menu technicien indique si des plages ont été reçues, si l'extinction est réelle ou en repli, et si les alarmes sont exactes ou approximatives.
  Android 12 et plus : le réveil à la minute près demande l'autorisation « alarmes exactes » (`SCHEDULE_EXACT_ALARM`, à accorder dans les réglages de l'appareil) ; sans elle, l'app programme une alarme approximative et le contrôle toutes les 15 s de l'activité prend le relais tant que le player tourne.
- **Réveil manuel** (menu ⋮ « Réveiller » du back-office) : l'écran reste éveillé 60 minutes malgré ses plages. Pendant la veille matérielle, l'app interroge le serveur chaque minute (`SleepController.pollWake`, `GET /api/player/version`, champ `wakeUntil`) ; elle se rendort à la fin du réveil.
- **Mise en veille manuelle** (menu ⋮ « Mettre en veille ») : le serveur envoie une plage couvrant toute la semaine ; l'écran dort jusqu'à « Réveiller », qui reprend le réveil manuel ci-dessus.
- **Luminosité** (`LinkiiNative.setBrightness`, `MainActivity.kt`) : le back-office règle la luminosité selon l'heure. Smartphone et tablette : luminosité de la fenêtre de l'app (10 à 100 %, 100 % = celle de l'appareil), l'appel renvoie `true`. Téléviseur ou boîtier (mode « télévision », interface Leanback ou sans écran tactile, `isTv()`) : rien n'est modifié et l'appel renvoie `false`, le player assombrit alors l'image par filtre. Le contraste est toujours un filtre dans le player.
- À valider sur un appareil réel (tablette et Android TV).

## Limites connues

- Le PIN n'est pas encore défini depuis le back-office.
- Icône et bannière provisoires (vectorielles).
