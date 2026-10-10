# Linkii Player — application Android

Coquille native autour du player web (`Linkii.Poc/wwwroot/player`). Le player garde l'appairage par code, la diffusion
et le cache hors ligne (service worker + Cache Storage) ; l'application ajoute le démarrage automatique, le plein écran
permanent, l'écran toujours allumé, la relance en cas de panne et un menu technicien. Aucune dépendance (pas d'AndroidX).

Visuel validé : `maquettes/player-android.html`.

## Compiler

JDK 17 ou plus (celui d'Android Studio convient), SDK Android 35.

```bash
cd Linkii.Android
./gradlew :app:assembleDebug          # app/build/outputs/apk/debug/app-debug.apk
./gradlew :app:bundleRelease          # app/build/outputs/bundle/release/app-release.aab (Google Play)
```

Adresse du serveur par défaut : `-PserverUrl=https://app.exemple.ch` (modifiable ensuite sur l'appareil).
Le serveur doit être en **HTTPS** : sans cela le service worker ne démarre pas et il n'y a pas de mode hors ligne
(seuls `localhost` et `10.0.2.2` sont admis en HTTP, pour le développement).

## Publier sur Google Play

1. Créer une clé d'envoi (`keytool -genkeypair -v -keystore linkii-upload.jks -alias linkii -keyalg RSA -keysize 2048 -validity 10000`) et la garder hors du dépôt.
2. Dans `~/.gradle/gradle.properties` : `LINKII_KEYSTORE`, `LINKII_KEYSTORE_PASSWORD`, `LINKII_KEY_ALIAS`, `LINKII_KEY_PASSWORD`.
3. `./gradlew :app:bundleRelease`, puis envoyer le `.aab` dans la Play Console (compte développeur requis, 25 USD une fois).
4. Fiche : catégorie « Professionnel », politique de confidentialité obligatoire, formulaire « Sécurité des données ».
   Pour Android TV : ajouter des captures 1920×1080 et une bannière 1280×720 (la bannière du dépôt est provisoire).

## Menu technicien

Cinq appuis sur OK (télécommande) ou cinq touchers dans le coin supérieur droit, en moins de 3 secondes.
Code PIN par défaut : 9999 (modifiable depuis le menu ; vide = menu sans code).

## Médias hors ligne

Les fichiers de la liste de lecture (images, vidéos, fichiers des apps : tout ce que le player met dans `url` / `urls`) sont téléchargés
par l'app dans son stockage privé (`MediaStore.kt`), sans quota du WebView ni purge par le système. Téléchargement un fichier à la fois,
avec reprise après coupure, nouvel essai toutes les 30 s et retrait des fichiers qui ne sont plus utilisés ; il reprend dès le démarrage
de l'app. Le WebView lit ensuite ces fichiers depuis le disque (requêtes Range comprises), en ligne comme hors ligne.
Une petite icône de progression (anneau + flèche, puis coche) s'affiche en bas à droite pendant les téléchargements.
Le menu technicien indique le nombre de médias en local. YouTube et les données en direct (agenda…) restent en ligne.
Le player passe par le pont `window.LinkiiNative` ; dans un navigateur il garde son cache habituel.

## Démarrage automatique

- Définir Linkii comme **écran d'accueil** de l'appareil (recommandé sur un boîtier) : lancement direct à l'allumage.
- Ou activer *Autoriser le démarrage automatique* dans le menu technicien (affichage par-dessus les autres applications, requis par Android 10+).
- Verrouillage kiosque (`startLockTask`) : automatique si l'appareil est géré (MDM / propriétaire de l'appareil).

## Version 1.1.0 : contrôle du direct

L'app remonte son modèle, la version Android et la version de l'application, et exécute les commandes du back-office (licence Growth) : aperçu du direct (`Capture.kt`, test `CaptureTest.kt`), redémarrage de l'application (`RestartActivity.kt`), pause, rechargement. À valider sur un appareil réel.
## Limites connues

- Le PIN n'est pas encore défini depuis le back-office.
- Icône et bannière provisoires (vectorielles).
