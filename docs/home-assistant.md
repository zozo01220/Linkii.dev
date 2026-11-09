# Linkivi et Home Assistant : vue d'ensemble

Dernière mise à jour : 9 novembre 2026. Le détail chronologique est dans `DEV-POC.md` (§191 à §208 et §221 à §224) ; ce document donne la vue d'ensemble, l'état de vérification et les pistes.

Deux intégrations, une dans chaque sens, qui ne sont **pas** à confondre :

| Sens | Ce que ça fait | Où |
|---|---|---|
| **Linkivi lit et commande Home Assistant** | Les écrans affichent et pilotent la maison : sept widgets, source de données, compositions, plan du bâtiment. | Back-office Linkivi (Applications › Home Assistant), relais, player |
| **Home Assistant lit et commande Linkivi** | Les écrans deviennent des appareils Home Assistant : état, veille, redémarrage, message affiché, carte de tableau de bord. | API publique `/api/v1` et dossier `homeassistant/` (intégration `linkivi`) |

## 1. Linkivi lit et commande Home Assistant (§191 à §208)

- **Accès** : direct (https public) ou par le **relais Linkivi** (réseau local, `http://homeassistant.local:8123`). Une seule session par mot de passe : le `refresh_token` est gardé chiffré et renouvelle le jeton.
- **Temps réel** : WebSocket (`get_states`, `state_changed`) quand un écran affiche un widget Home Assistant ; relevé toutes les 5 s par le relais ou si l'instance refuse le WebSocket. Santé : alerte « Home Assistant injoignable » après 5 minutes, entités introuvables signalées.
- **Sept widgets** : Capteurs, Interrupteurs, Lumières, Thermostats, Volets, Scènes et scripts, Serrures, avec l'option « Commande à l'écran » (second appui pour confirmer une serrure).
- **Source de données « Home Assistant »** : jeux « États actuels » et « Historique » (6 h à 30 jours), utilisables dans les listes, graphiques et compositions. Source intégrée aux compositions, pack de modèles « Maison connectée » (5 modèles).
- **Plan du bâtiment** (§193 à §203, §208) : pièces dessinées, murs en 3D, étages, vues enregistrées, plein écran pour borne et diffusion, plans partagés, éditeur en pleine page.
- Tests : `HomeAssistantTests` (domaines, commandes, relais, historique, client face à une fausse instance), tests JS de la vue du bâtiment. Essai de bout en bout avec une fausse instance et le vrai relais.
- **Jamais essayé avec une vraie instance** : WebSocket direct, connexion réelle par mot de passe, `/auth/revoke`, alerte à 5 minutes, actualisation quotidienne.

## 2. Home Assistant lit et commande Linkivi (§221 à §224)

### API publique (plan Pro, clé `lkv_…`)

Modèle de clé **« Home Assistant »** (Réglages › Développeurs) : accès `account:read`, `screens:read`, `screens:control`. Une clé agit au nom de son propriétaire : un administrateur commande tous les écrans, un membre seulement ce que sa fiche lui permet sur les écrans qui lui sont attribués.

| Appel | Rôle |
|---|---|
| `GET /api/v1/me` | Organisation et clé. |
| `GET /api/v1/screens` | État des écrans (en ligne, veille, aire, zone, liste, résolution, commandes que le matériel accepte). |
| `POST /api/v1/screens/{id}/commands` | `restart-player`, `reboot`, `poweroff`, `screen-off`, `wake` (202). Mêmes règles que le back-office (`DevicePower`) : écran en ligne, matériel compatible, un mur se commande en entier. |
| `POST /api/v1/screens/{id}/display` | **Afficher un message** : `title` (60) ou `message` (300), `look` information ou attention, `durationSeconds` 5 à 3600 (30), `wake`. |
| `DELETE /api/v1/screens/{id}/display` | Retire le message. |

Erreurs : 422 champ invalide, 409 `screen_unavailable` (hors ligne, en veille sans `wake`, non attribué, hors Pro, commande déjà en cours), 404, 403 `scope_missing`.

Code : `DeviceCommands.cs` (`DeviceCommandService`), `ScreenNotices.cs` (`NoticeService`, `Screen.Notice`), `Api/ScreensApi.cs`. Le message part en temps réel (SignalR `Notice`) avec un filet : le champ `notice` de `/api/player/version`. Le player l'affiche par-dessus la diffusion (`wwwroot/player/notice.js`) ; la diffusion reprend ensuite avec le contenu suivant. L'urgence passe devant ; l'historique de l'écran garde la trace (catégorie « Messages »).

### Intégration Home Assistant `linkivi` (version 1.3.0, dossier `homeassistant/`)

- **Appareil par écran**, relevé toutes les 30 s (`DataUpdateCoordinator`), nouveaux écrans détectés seuls. Flux de configuration (adresse + clé, ré-authentification).
- **Entités** : *En ligne* (capteur binaire), *Écran* (interrupteur : éteindre = veille manuelle, allumer = réveil d'une heure), boutons *Redémarrer le player*, *Redémarrer l'appareil*, *Éteindre l'appareil* (seulement si le matériel sait).
- **Actions** : `linkivi.show_message` (écrans, appareils ou pièces ; titre, message, aspect, durée, réveil) et `linkivi.clear_display`. Une erreur d'un écran n'empêche pas les autres.
- **Carte « Écrans Linkivi »** (`www/linkivi-screens-card.js` 1.2.0) : deux styles, `cards` (petites télés) et `list` (verre), anneau de synthèse, couleur d'accent, confirmations, éditeur visuel. Chargée par l'intégration (`add_extra_js_url`) ; ressource manuelle `/linkivi/linkivi-screens-card.js` (module JavaScript) en secours.
- Mode d'emploi, exemples d'automatisations et dépannage : `homeassistant/README.md`.

## 3. État de vérification

| Élément | Vérifié | Pas encore vérifié |
|---|---|---|
| API commandes et messages | Tests `ScreenCommandsApiTests` (9) et `ScreenDisplayApiTests` (7) | Appareils réels (Windows, Raspberry Pi, Android propriétaire), mur d'écrans |
| Player (message) | Page de test du navigateur : aspects, barre, retrait, refus en veille, réveil | Vrai écran, conflit avec l'urgence en réel |
| Entités Home Assistant | Visibles sur une vraie instance (six écrans) | Boutons et interrupteur en réel ; **code Python jamais exécuté** (pas de Python sur le poste) |
| Carte | Faux Home Assistant ; version 1.1.0 confirmée sur la vraie instance avec la ressource manuelle | Version 1.2.0 sur la vraie instance, chargement automatique (n'a pas marché chez l'utilisateur), éditeur visuel |
| Action `show_message` | Syntaxe de l'API et du service relue | Appel depuis un vrai Home Assistant, cibles par pièce, erreurs affichées |

Suite complète des tests : une trentaine d'échecs sans rapport (facturation, OAuth, PDF, salles, PWA et Apps lancés hors du dossier du projet), les mêmes qu'avant ce travail.

## 4. Maquettes

| Fichier | Sujet | État |
|---|---|---|
| `maquettes/ha-carte-ecrans.html` | Carte des écrans | Validée, codée |
| `maquettes/ha-carte-ecrans-style.html` | Trois styles de carte | A (cartes) et B (liste) retenus, codés |
| `maquettes/ha-declencheurs.html` | Règles dans Linkivi (« quand… alors… ») | Non validée, gardée pour plus tard |
| `maquettes/ha-actions-contenu.html` | Actions Home Assistant (message, liste, urgence, arrêt) | Message codé, reste non codé |

## 5. Pistes et reste à faire

- **Actions de contenu** : `show_list` (avec lecture des listes dans l'API), `start_emergency` / `end_emergency` (l'API `POST/DELETE /emergency` existe déjà ; il faut l'accès `emergency:write` dans le modèle de clé « Home Assistant » et l'intégration « Alerte d'urgence » installée), aspect « Image », capteur « Urgence en cours », modèles d'automatisation (blueprints), pastille « Message depuis Home Assistant · Arrêter » sur la carte d'écran, bouton « Envoyer un message » dans la carte.
- **Événements Linkivi vers Home Assistant** : bouton de borne, scan de QR, écran hors ligne, via les webhooks existants.
- **Entités de plus** : luminosité, volume, liste en cours, batterie des Android, version du player, réservations de salle comme calendrier.
- **HACS** : dépôt GitHub public à part (`custom_components/linkivi/` à la racine), `hacs.json`, vraies adresses et `codeowners` dans le manifeste (actuellement fictifs), versions publiées. À régler avant : chargement automatique de la carte, exécution réelle du code Python, nom `linkivi` à figer (voir le renommage Linkii vers Linkivi, reporté).
- **Caméras sur mouvement** (Netatmo Presence, Home Assistant chez l'utilisateur) : photo de l'événement `netatmo_event` affichée 30 s, puis image rafraîchie, puis flux (WebRTC, le HLS a 6 à 13 s de retard). Même socle que le message temporaire. Deux essais à faire : écouter `netatmo_event`, mesurer le retard d'une caméra.
- **Particuliers** : l'API est réservée au plan Pro ; un plan « Maison » serait à décider. Usages en tête : notifications sur l'écran familial, sonnette avec photo, présence et veille automatique, alerte sur tous les écrans.
