# Linkii Back-office — application Android

Coquille native autour du back-office web responsive (`Linkii.Poc`). Le serveur fournit toute l'interface ; l'app ajoute
ce que le navigateur de l'appareil ne donne pas : session conservée (cookies), import de fichiers dans la médiathèque,
téléchargements, bouton Retour qui suit l'historique, barre de progression et écran de secours si le serveur est injoignable
(boutons « Réessayer » et « Changer de serveur »), notifications push et, plus tard, verrouillage biométrique. Seule dépendance : Firebase Messaging.

## Compiler

JDK 17 ou plus (celui d'Android Studio convient), SDK Android 35.

```bash
cd Linkii.AndroidBackOffice
JAVA_HOME="C:/Program Files/Android/Android Studio/jbr" ./gradlew :app:assembleDebug   # app/build/outputs/apk/debug/linkii-backoffice-debug.apk
./gradlew :app:bundleRelease                                                            # app/build/outputs/bundle/release/linkii-backoffice-release.aab
```

Adresse par défaut : `-PserverUrl=https://app.exemple.ch` (modifiable ensuite sur l'appareil, écran de secours › « Changer de serveur »).
HTTPS obligatoire (HTTP admis seulement pour `localhost` et `10.0.2.2`). Signature et publication Play Store : voir `Linkii.AndroidPlayer/README.md`
(mêmes variables `LINKII_KEYSTORE*`).

## Connexion

Les pages `login.microsoftonline.com`, `login.live.com` et `accounts.google.com` s'ouvrent dans l'app pour que la session revienne au back-office ;
tout autre domaine externe s'ouvre dans le navigateur. L'app retire la mention « wv » de son identifiant de navigateur, car Google refuse la
connexion depuis un WebView reconnaissable. **À valider sur appareil** : si Google la bloque quand même, la connexion e-mail / Microsoft reste possible.

## Pont JavaScript

`window.LinkiiAdmin` : `version()`, `serverUrl()`, `retry()`, `changeServer()`, `pushToken()`, `deviceName()`, `biometricAvailable()`, `biometricEnabled()`, `setBiometric(on)`.
Le back-office peut tester sa présence pour savoir qu'il tourne dans l'app.

## Notifications (Firebase Cloud Messaging)

Quatre catégories, que chaque administrateur coupe ou garde dans **Réglages › Compte et confidentialité › Notifications** : écrans hors ligne ou de retour (suit l'alerte réglée dans Réglages),
synchronisation de dossier en échec (première erreur d'une série seulement), abonnement et essai (rappel, fin d'essai, espace suspendu), nouvelle personne dans l'organisation.

**Mise en place (une fois)**
1. Créer un projet sur console.firebase.google.com, y ajouter une app Android `ch.linkii.backoffice`.
2. Compiler avec les valeurs du projet (ou les mettre dans `~/.gradle/gradle.properties`) :
   `./gradlew :app:assembleDebug -PfcmProjectId=… -PfcmAppId=1:…:android:… -PfcmApiKey=… -PfcmSenderId=…` (pas de `google-services.json`).
3. Serveur : Paramètres du projet › Comptes de service › générer une clé privée, puis `Linkii:Fcm:ServiceAccountFile` = chemin du fichier JSON
   (variable d'environnement `Linkii__Fcm__ServiceAccountFile`). Sans ce fichier le serveur n'envoie rien ; sans les `-Pfcm…` l'app n'en reçoit pas.

**Fonctionnement** : au démarrage l'app obtient son jeton Firebase ; la page du back-office (`push-register.js`, via `LinkiiAdmin.pushToken()`) l'enregistre avec la session
de la personne (`POST /api/push/register`). Le serveur (`Push.cs`) envoie des messages de données ; `PushReceiver.kt` affiche la notification, un toucher ouvre la page concernée.
Un jeton refusé par Firebase est retiré. Android 13 et plus : l'autorisation est demandée au premier lancement.

## Verrouillage biométrique (prévu pour plus tard)

`BiometricGate.kt` (empreinte, visage ou code de l'appareil, Android 11+, sans bibliothèque) est prêt mais **désactivé** : aucun écran ne le propose encore.
Pont : `LinkiiAdmin.biometricAvailable()`, `biometricEnabled()`, `setBiometric(true|false)`. Activé, le contenu reste masqué à chaque retour dans l'app après 30 s d'absence.
L'interrupteur est dans **Réglages › Compte et confidentialité › Verrouillage de l'application** (visible seulement dans l'app, sur un appareil compatible).

## Limites connues

- Notifications jamais essayées avec un vrai projet Firebase ; pas de caméra (import depuis les fichiers de l'appareil seulement).
- Icône provisoire (vectorielle, identique au player).
- Jamais essayée sur un appareil réel.

## Look « app »

L'app ajoute `LinkiiBackOffice/<version>` à son identifiant de navigateur ; le serveur pose alors `body.is-app` (`App.razor`) et le CSS « app » s'applique : barre d'onglets en bas, menu « Plus » en feuille, cartes avec liseré d'état, Réglages en liste de rubriques. Le site PC et le navigateur mobile ne reçoivent rien de cela. Détails : `DEV-POC.md` §63, maquette `maquettes/app-mobile.html`.
