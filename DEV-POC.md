# Linkii — Dev POC (jetable)

> **Statut : test / prototype voué à mourir.** But : prouver la chaîne *back-office → serveur → SignalR → player* et la promesse produit **« écran en service en moins de 2 minutes »**. Le code n'est pas destiné à être réutilisé : on privilégie la vitesse.
> Source : *Linkii — Spécification produit (v1)*. Dernière mise à jour : 11 octobre 2026.

> **Apps retirées le 7 octobre 2026** (on y reviendra plus tard) : **LinkedIn**, **Menu restaurant (API)**, **Menu restaurant (CSV)**, **Météo : prévisions**, **Occupation de salle**. Manifestes, fournisseurs serveur, rendus du player, connexion OAuth LinkedIn, import CSV des menus, bibliothèque QR et tests correspondants sont supprimés ; leurs contenus existants sont purgés au démarrage (`Seed.PurgeRetiredApps`, sauvegarde `data.json.pre-apps-retirees.bak`). **Le code complet d’avant le retrait est conservé dans `_archive/avant-retrait-apps-2026-10-07/`.** Les sections de ce document qui décrivent ces apps (porte de salle, menus, LinkedIn) sont historiques. Le connecteur Microsoft 365 (page Données) reste : il sert à l’app Agenda.

## 1. Lancer le POC

```bash
cd C:\DEV\En-cours\Linkii\Linkii.Poc
dotnet run --urls http://localhost:5080
```

| Quoi | URL |
|---|---|
| Back-office | `http://localhost:5080/` (redirige vers `/login`) |
| Player (navigateur, plein écran F11) | `http://localhost:5080/player/` |

- Premier lancement : `/login` propose de **créer l'espace** (organisation, nom, e-mail, mot de passe ≥ 8 caractères).
- Repartir de zéro : arrêter l'app, supprimer `data.json` et `media/`.
- Arrêter l'app avant de recompiler (le `.exe` est verrouillé tant qu'elle tourne). Si `data.json` est modifié à la main pendant qu'elle tourne, l'app l'écrase au prochain enregistrement.

## 2. Ce qui est réalisé

### Chaîne de bout en bout
1. Un écran affiche un **code à 6 chiffres** (cases animées) au premier lancement.
2. Le code est saisi dans le back-office → écran **appairé** (jeton stocké sur l'écran).
3. L'écran diffuse la playlist en boucle (images, vidéos H.264, widgets), sans écran vide entre deux contenus (double buffer).
4. **Publier** → notification **SignalR** → l'écran se met à jour en quelques secondes ; le back-office passe l'écran de *Mise à jour* à *En ligne*.
5. Coupure réseau → l'écran continue depuis le cache, affiche une **fine bordure rouge + pastille « Hors ligne » en bas à droite**, et le back-office l'affiche *Hors ligne*.

### Fonctions back-office (Blazor Server)
| Zone | Contenu |
|---|---|
| **Connexion** | Compte administrateur (cookie, mot de passe haché PBKDF2), création d'espace au 1er lancement, menu utilisateur en haut à droite : *Réglages de l'organisation* / **Se déconnecter** |
| **Assistant de démarrage** | Plein écran, affiché quand la base est vide (ou via `/start`) : 1 **choisir l'usage** (contenus, porte de salle, horaires, agenda, menu, départs) → 2 importer des médias **ou** brancher la source de données → 3 connecter l'écran (code + options de matériel / format). Chronomètre « mis en service en mm:ss », écran de succès avec confettis |
| ~~**Données**~~ | Page retirée le 8 octobre 2026 : le connecteur Microsoft 365 se configure dans **Intégrations › Calendriers partagés › Microsoft 365 › Gérer** |
| **Écrans** | Une barre de listes déroulantes (9 octobre 2026) : *Tout sélectionner*, *N sélectionnés · Actions* (publier, changer de zone, assembler en mur, déplacer, supprimer), *Zone*, *État*, *Tri*, recherche, cartes / liste ; zone choisie : une ligne d'information et *Actions du mur* / *Actions de la zone*. **Publier** (orange, avec le nombre) n'apparaît que s'il y a des modifications à publier. Carte par écran (état, playlist, format, dernière synchro, résolution détectée), connexion par code, **modification du format** à chaud |
| **Médiathèque** | Import d'images (jpg, png, gif, webp, svg) et de **vidéos de presque tous les formats** (MP4, MKV, AVI, MOV, WMV, FLV, MPEG, WebM, 3GP, TS… — convertis automatiquement en MP4 H.264, voir ci-dessous), widgets **Horloge** et **Météo**, **vidéos YouTube** (lien collé), **widgets de données** (salle, horaires, agenda, menu, départs — voir §9), **Pack d'exemples**, **filtre Tous / Images / Vidéos / Widgets** avec compteurs (les vidéos YouTube sont classées dans « Vidéos ») (7 diapos : affiche, plan, menu, alerte, horaires, sécurité, remerciement) |
| **Playlists** | Grille de cartes (mosaïque des contenus, durée de la boucle, nombre d’écrans, carte « + »), menu ⋮ : Éditer, Renommer, Dupliquer, Supprimer ; éditeur sur `/playlists/{id}` : ordre, durée, position, renommer, panneau Apps. **Aucune publication ici** |
| **Réglages** (paramètres du tenant, onglets Organisation · Paramètres · Compte) | Organisation, fuseau horaire, couleur principale, durée par défaut, heure de redémarrage nocturne, météo par défaut, préréglage des nouveaux écrans, changement de mot de passe |
| **Intégrations** (`/integrations`, menu latéral en bas) et **Widgets** (`/widgets`) | Catalogue en cartes ou en liste (`CatalogView`, affichage mémorisé par page via `ViewToggle`). Intégrations regroupées par `category` du manifeste (Médiathèque · Service en ligne · Web), onglet **Comptes connectés** (voir §16) : interrupteur de disponibilité, nombre de contenus, Canva › **Gérer** (compte et intégration de l’organisation). Widgets : interrupteur (proposé ou non dans l’édition des écrans), nombre d’écrans qui l’utilisent. « Playlist » s’affiche « Liste de lecture » dans l’interface (le code garde `Playlist`). **Calendriers partagés** : famille de sources (Microsoft 365, Google Calendar, CalDAV, liens ICS, Exchange) dans Intégrations, une connexion par source (`Tenant.CalendarAccounts`, secrets chiffrés ; Microsoft 365 garde `Ms*`) et ses calendriers (`Tenant.SharedCalendars`, fenêtre `CalendarSourceDialog`). L’app `agenda` (« Calendrier partagé ») n’a plus qu’un champ `calendar` (Id du calendrier), résolu côté serveur à chaque lecture (`SharedCalendarSources.Resolve`) ; elle est proposée dès qu’une source est active. Les anciens contenus Agenda (connexion dans le contenu) sont supprimés au démarrage (`Seed.PurgeOldCalendars`, sauvegarde `data.json.pre-calendriers.bak`). **Drives** : Google Drive, OneDrive / SharePoint, Nextcloud, SFTP et Dropbox (`Apps/Drives.cs`, `Apps/DriveRemotes.cs` pour les trois dernières : §26 ; fenêtre `DriveSourceDialog`), Google Drive : compte Google connecté par l’organisation (voir Compte Google) ; OneDrive : connexion Microsoft 365 partagée avec les calendriers (autorisation `Files.Read.All` ou `Sites.Selected`). Dossiers choisis en parcourant le compte connecté ou par lien de partage (`Tenant.DriveFolders`, sous-dossiers inclus sur option : voir §17), images et vidéos copiées dans la médiathèque (`Info["drive"]`, affichées avec un badge Drive, ni renommées ni supprimées depuis la Médiathèque), synchronisées toutes les 15 min (`DriveSyncWorker`) ou à la demande. L’app `drive` (« Dossier Drive ») résout ses fichiers à la publication ; après chaque synchronisation, `Notifier.RefreshDrive` met à jour les éléments publiés et prévient les écrans, sans republier. |

### Widgets et positions
- Widgets : **Horloge** (fuseau du tenant) et **Météo** (Open-Meteo, sans clé API, cache serveur 10 min, dernière valeur gardée hors ligne).
- **YouTube** : on colle le lien de la vidéo (`watch?v=`, `youtu.be/`, `/shorts/`, `/embed/`) ; le player utilise l'**API IFrame de YouTube** (`youtube-nocookie.com`) : **sans son**, sans commandes, démarrage forcé en muet ; en diaporama, la diapo **dure jusqu'à la fin de la vidéo** (plafond 30 min) puis enchaîne ; seule ou en incrustation, elle boucle. Si la vidéo refuse l'intégration, l'écran affiche le motif (code d'erreur YouTube) et passe à la suite. Pleine page ou incrustation dans un angle.
- Positions : **pleine page** (diapo avec durée) · **4 angles** · **bandeau haut / bas** (widgets permanents, sans durée).
- Photos et vidéos : pleine page uniquement. Les listes (horaires, agenda, menu, départs) : pleine page ou angle ; horloge, météo et porte de salle : toutes les positions.
- **Rien ne se superpose** : les bandeaux et les rangées d'angles réservent leur place, la zone de contenu se réduit en conséquence. Plusieurs widgets à la même position s'empilent.

### Format d'écran
- Choisi à la connexion (assistant ou formulaire) et modifiable ensuite : **matériel** (téléviseur, moniteur, totem / borne, tablette Android), **format** (paysage / portrait), **résolution** (auto, 720p, 768p, Full HD, QHD, 4K).
- Le player travaille sur une scène à la résolution cible, mise à l'échelle et **pivotée** si l'écran est physiquement dans l'autre sens. Résolution réelle de l'appareil remontée au back-office.
- Le *matériel* n'est qu'un libellé + une suggestion d'orientation (un totem → portrait) : il ne change pas le comportement. L'**usage** (contenus, porte de salle…) vient du widget placé dans la playlist.

### Zones et lecture synchronisée (9 octobre 2026)
- **Zone par défaut** : chaque aire a une zone « Défaut » (`Zone.IsDefault`, renommable, non supprimable) ; **tout écran est dans une zone**. Migration au démarrage (`Seed.EnsureAreas`, sauvegarde `data.json.pre-zone-defaut.bak`) : les écrans sans zone passent dans le Défaut de leur aire. Un écran déplacé vers une autre aire arrive dans son Défaut ; supprimer une zone renvoie ses écrans dans Défaut. La barre des zones n'apparaît que s'il y a une zone en plus de Défaut (ou une synchro active).
- **Type de zone** : `Zone.Kind` : « free » (zone libre) ou « wall » (mur d'écrans, voir ci-dessous).
- **Mur d'écrans** (9 octobre 2026) : une seule image répartie sur une grille d'écrans.
  - *Créer* : cocher au moins 2 écrans dans la page Écrans → **Assembler en mur…** → une fenêtre (`WallDialog`) : grille suggérée d'après le nombre d'écrans (`Helpers.WallShapes`, colonnes × lignes = nombre d'écrans), ordre = ordre de sélection (de gauche à droite, puis de haut en bas), deux clics sur des cases pour les échanger, **Identifier sur les écrans** (message SignalR `Identify` : chaque écran affiche son numéro 5 s), liste préremplie avec celle du premier écran, résolution conseillée affichée. **Créer le mur** publie aussitôt (6 clics pour 4 écrans).
  - *Modèle* : zone `Kind = "wall"` avec `WallCols`, `WallRows`, `PlaylistId` ; synchro toujours active ; chaque écran a sa place `Screen.WallPos` (0 = en haut à gauche), joue la liste du mur en écran plein (`AreaOps.CreateWall / ArrangeWall / SetWallPlaylist / DissolveWall`).
  - *Player* : l'API envoie `wall: { cols, rows, col, row }` ; la scène devient l'image de tout le mur (colonnes × largeur, lignes × hauteur) et est décalée pour que seule la portion de l'écran soit visible (`layout()` dans `player.js`). La place fait partie de la révision (`Helpers.SyncStamp`) : réorganiser recadre les écrans aussitôt.
  - *Widgets* : un écran qui entre dans un mur perd ses widgets (ils seraient coupés ou répétés) ; l'onglet Widgets disparaît de son édition, et les widgets déjà publiés ne sont plus envoyés à un écran de mur.
  - *Au quotidien* : bandeau du mur (liste du mur, **Publier le mur**, **Réorganiser…**, **Dissoudre…**, aperçu de la grille : un clic ouvre l'écran). Les cartes affichent la place (« 2 · haut droite ») au lieu du choix de liste. Un écran du mur ne change pas de zone ni d'aire : on dissout d'abord le mur ; ses écrans retournent dans Défaut et gardent la liste.
  - Non fait : compensation des bordures, placement sur une grille avec des cases vides, avertissement de format calculé au-delà de « formats différents ».
- **Lecture synchronisée** (`Zone.Sync`, désactivée par défaut, interrupteur dans *Gérer les zones* et dans le bandeau de la zone) : les écrans de la zone **qui jouent la même liste publiée** affichent le même contenu au même moment (±0,5 s visé). Aucun objet « groupe » : `Helpers.SyncedScreens` repère les écrans alignés (même liste, même boucle publiée) pour le badge *Synchronisé* / *Indépendant*.
- **Principe** : la position dans la boucle se déduit de l'heure du serveur : `(heure serveur − Zone.SyncEpochUtc) mod durée de la boucle`. L'API player ajoute `serverNow` à `/api/player/playlist` et `/api/player/version`, et `sync: { epoch }` à la playlist ; le player estime son écart d'horloge (milieu de l'aller-retour, mesure la plus courte, dernière valeur gardée hors ligne dans `lk_clock`) et recalcule le contenu courant à chaque changement de diapo (`syncPick` / `syncLeft` dans `player.js`, aussi pour les zones 2 et 3 d'un écran découpé). Un écran qui démarre tard ou revient d'une coupure rejoint la bonne diapo.
- **Durées** : en zone synchronisée, une app « jusqu'à la fin » reçoit une durée fixe, calculée à l'envoi (`Helpers.FixedForSync`, aucune republication) : diaporama d'images (diaporama, Canva en pages, Drive sans vidéo) = nombre d'images × durée de chaque image ; YouTube = durée constatée ; sinon 60 s.
- **Déroulé des contenus à l'heure commune** : le player passe à chaque contenu l'origine de son créneau (ou celle de la zone si la liste n'a qu'un contenu) dans `host.opts.sync`. Les apps de la médiathèque (vidéo, diaporamas, Canva, Drive) en déduisent le fichier affiché et la position dans la vidéo (`syncedList` dans `apps.js` : durée d'une vidéo lue dans ses métadonnées, recalage doux par la vitesse de lecture, saut au-delà de 0,5 s) ; « Ordre aléatoire » donne le même ordre sur tous les écrans. Une vidéo hors app (ancien format) est recalée de la même façon. Activer ou couper la synchro fait partie de la révision de l'écran (`Helpers.SyncStamp`) : ses écrans se rechargent aussitôt.
- Non fait : mesure de l'écart réel remonté au back-office (la maquette montrait « +0,2 s »), avertissement dans l'éditeur de liste quand elle contient une durée variable.

## 3. Hors périmètre (volontairement)

| Exclu | Pourquoi |
|---|---|
| Montants d'abonnement, facturation, quantités incluses | Phase ultérieure (seuls les écrans sont comptés) |
| Envoi d'e-mails (adresse d'envoi du revendeur) | Champ stocké, aucun envoi dans le POC |
| Google Calendar (hors lien ICS), réservation depuis la tablette | Voir §9 |
| Planning de diffusion | Fait le 11 octobre 2026 : voir §37 (urgence et copie entre aires à revoir) |
| Zones d'écran multiples, transitions, aperçu | Une zone de contenu + widgets, coupure en fondu |
| Publicité, preuve de diffusion, bornes | Voir §7 |
| App Android (WebView/APK), démarrage auto | Player testé dans un navigateur (Chromium kiosque) ; APK = étape ultérieure |
| Limitation des tentatives de connexion, mot de passe oublié | POC |
| Tests automatisés, CI, déploiement | Lancement local |

## 4. Stack et structure

- **.NET 8**, un seul projet `Linkii.Poc` : Blazor Server (back-office) + Minimal API + **SignalR**.
- **Stockage : JSON plat** (`data.json`, sous verrou) — pas de SQLite. Médias sur disque dans `media/`.
- **Bibliothèque** : `Ical.Net` (lecture des fichiers ICS, récurrences et fuseaux). Aucun autre paquet.
- **Outils externes (facultatifs mais recommandés)** : ffmpeg ou VLC sur le serveur, pour la conversion vidéo. Windows : `winget install Gyan.FFmpeg` ou VLC.
- **Player** : HTML + JS vanilla « ancien » (`var`, promesses, pas d'async/await), pas de Blazor sur la clé. Client SignalR chargé depuis un CDN (jsdelivr) et mis en cache par le service worker.

```
Linkii.Poc/
├── Program.cs               # auth cookie, porte d'entrée, API player, météo, hub
├── Models.cs                # Tenant, Screen, MediaItem, Playlist… + options d'écran + statut
├── JsonStore.cs             # persistance data.json
├── Notifier.cs              # publication + notifications SignalR
├── WeatherService.cs        # Open-Meteo + cache
├── VideoConverter.cs        # détection ffmpeg / VLC + conversion en MP4 H.264 1080p max (+ Mp4Probe)
├── MediaProbe.cs            # dimensions des images et vidéos (format affiché en médiathèque)
├── MediaImporter.cs         # import des médias, conversion vidéo en arrière-plan
├── DataServices.cs          # calendriers (ICS), Microsoft Graph, transports, import CSV du menu
├── Passwords.cs             # hachage PBKDF2
├── DemoSlides.cs            # diapos SVG d'exemple (assistant + pack)
├── Hubs/ScreenHub.cs
├── Components/              # App, Routes, Logo, ScreenFormat, DataWidgetEditor, Onboarding
│   ├── Layout/              # MainLayout (menu utilisateur), AuthLayout
│   └── Pages/               # Login, Screens, Media, Playlists, Data, Settings
└── wwwroot/
    ├── app.css
    ├── modele-menu.csv      # modèle d'import du menu
    └── player/              # index.html, player.js, sw.js
```

## 5. Modèle de données (JSON)

```csharp
Db        { Tenant; List<Screen>; List<MediaItem>; List<Playlist>; List<MenuRow> Menu }
Tenant    { Name; AdminName; AdminEmail; PasswordHash; Timezone; BrandColor; DefaultDurationSec;
            ReloadHour; WeatherCity; WeatherUnit; DefaultScreenType/Orientation/Resolution; MsTenantId; MsClientId; MsClientSecret }
Screen    { Id; Name; PairingCode; Token; PlaylistId; AppliedRevision; LastSeenUtc;
            ScreenType; Orientation; Resolution; DetectedW; DetectedH }
MediaItem { Id; Name; Type (image|video|app); FileName; AppKind (clock|weather|youtube|room|schedule|agenda|menu|transport); City; Unit;
            Title; Source (ics|m365); SourceUrl; Mailbox; ShowTitles; Stop; Limit }
MenuRow   { Date (aaaa-mm-jj); Category; Name; Price; Allergens }
Playlist  { Id; Name; PublishedVersion; DraftChanged; Draft: [PlaylistItem]; Published: [PublishedItem] }
PlaylistItem  { MediaId; DurationSec; Placement (full|top-left|top-right|bottom-left|bottom-right|top|bottom) }
```

- **Brouillon vs publié** : le back-office édite `Draft` ; *Publier* fige `Published` et incrémente la version. Le player ne lit jamais le brouillon.
- **Révision** envoyée à l'écran = playlist publiée + format de l'écran + fuseau + heure de redémarrage. Tout changement de réglage ou de format déclenche donc une mise à jour de l'écran concerné.
- **État d'un écran** (calculé) : *Hors ligne* si pas de signe de vie depuis 90 s ; *Mise à jour* si la révision confirmée ≠ la révision attendue ; sinon *En ligne*.

## 6. API et temps réel

| Méthode | Route | Rôle |
|---|---|---|
| `POST` | `/api/pairing/start` | Crée un écran non appairé, renvoie `{ screenId, code }` (+ résolution de l'appareil) |
| `GET` | `/api/pairing/status?screenId=` | Le player sonde jusqu'à recevoir son jeton |
| `GET` | `/api/player/playlist` (`X-Token`) | `{ version, items, screen, settings }` |
| `GET` | `/api/player/version` (`X-Token`) | Sondage de secours + signe de vie |
| `POST` | `/api/player/ack` (`X-Token`) | Confirme la version reçue + résolution détectée ; avec `stage` (`downloading` / `ready`), signale seulement l'étape de réception (suivi de publication, §27) |
| `GET` | `/api/weather?city=&unit=` (`X-Token`) | Météo (cache serveur 10 min) |
| `GET` | `/api/data/{id}` (`X-Token`) | Données d'un widget (salle, horaires, agenda, menu, départs) ; 502 + message si la source est injoignable |
| `GET` | `/api/preview/playlist` (`X-Preview`) | Simulateur : même réponse que `/api/player/playlist` pour l'écran, le mur ou la liste du jeton d'aperçu, plus `meta` (nom et couleur de chaque contenu) ; 401 si le jeton a expiré (voir §18) |
| `GET` | `/api/preview/data/{id}` (`X-Preview`) | Simulateur : données d'une app, comme `/api/data/{id}` |
| `POST` | `/logout` | Déconnexion du back-office |

**Hub SignalR** `/hubs/screen` : le player rejoint le groupe de son écran ; le serveur envoie `PlaylistChanged(version)` à la publication, au changement de playlist, de format ou de réglage. Reconnexion à délai croissant ; sondage toutes les 60 s en filet de sécurité ; ping de vie toutes les 15 s.

**Sécurité** : tout le back-office exige une session (cookie) ; restent ouverts le login, `/api/*` (jeton d'écran), `/hubs/*`, `/media` et `/player`.

## 7. Player : robustesse

- **Cache** : service worker (page, JS, lib SignalR) + cache des médias de la playlist (relus en blob, sans service worker) ; le service worker n'a pas pu être testé dans le navigateur intégré de l'outil de développement (redémarrage hors ligne de la page non vérifié) ; dernière playlist et dernière météo en `localStorage` → démarrage et diffusion sans réseau.
- **Hors ligne** : fine bordure rouge pulsante + pastille « Hors ligne » en bas à droite après ~30 s sans réponse du serveur ; disparaît dès le retour du réseau.
- **Rechargement nocturne** à l'heure réglée dans les paramètres du tenant (défaut 04:00).
- **Rotation / échelle** automatiques selon le format et la résolution configurés.
- Non fait : détection d'horloge aberrante, quota de cache, preuve de diffusion.

## 8. Décisions prises pour le POC

- Multi-tenant : voir §11.
- Pas de code propre/réutilisable exigé : ce dépôt peut être jeté.
- Navigateur de bureau suffisant pour valider ; la clé HDMI est un bonus. Choix du matériel reste à faire.
- H.264 uniquement pour la vidéo.
- Devise, tarifs et points ouverts commerciaux : hors POC.

## 9. Catégories d'usage — A et B (implémentées)

**Principe** : le *matériel* (téléviseur, tablette Android, totem, orientation, résolution) est indépendant de l'*usage*. Un usage se définit par la **source de données**, le **gabarit d'affichage** et l'**interaction**. Priorités retenues : **A** (contenus) et **B** (données). **C** (tactile), **D** (commercial) et **E** (supervision) restent hors périmètre.

### A. Diffusion de contenus — fait
Information générale, accueil et orientation, communication interne, sécurité et alertes, ambiance : images, vidéos, pack d'exemples, playlists, portrait pour totem. Reste à prévoir : une **alerte qui prend la main sur tous les écrans** (non fait).

### B. Écrans pilotés par des données — fait (B1 à B5)
Chaque usage est un **widget lié à une source**, posé dans une playlist comme les autres widgets (pleine page ou angle). Tout se crée depuis **Médiathèque › + Widget de données** ou depuis l'**assistant de démarrage**.

| Étape | Usage | Source | Rendu | Positions |
|---|---|---|---|---|
| **B1** | **Porte de salle** | Calendrier : lien **ICS** ou **Microsoft 365** | Plein écran **vert Libre / orange Bientôt occupé (≤ 15 min) / rouge Occupé / gris Indisponible**, réunion en cours, 3 suivantes ; en compact : pastille d'état | Toutes (pleine page, angles, bandeaux) |
| **B2** | **Menu du restaurant** | **CSV** importé (page Données) | Menu du jour par catégorie, prix, allergènes ; à défaut, prochain jour renseigné | Pleine page, angles |
| **B3** | **Horaires du jour** · **Agenda de la semaine** | ICS ou Microsoft 365 | Liste du jour (en cours surligné, passé estompé) · événements groupés par jour | Pleine page, angles |
| **B4** | **Prochains départs** | API ouverte des transports publics suisses (transport.opendata.ch) | Ligne, destination, quai, « dans 4′ », retard en rouge | Pleine page, angles |
| **B5** | **Connecteur Microsoft 365** | « Se connecter avec Microsoft » (OAuth 2.0 + PKCE, permissions déléguées en lecture seule) | Agendas du compte et annuaire des salles dans le sélecteur, calendriers lus sans lien ICS | — |

### Comment ça marche
- **Les sources sont lues côté serveur**, avec un cache de 30 à 45 s ; les écrans ne voient jamais l'URL ICS (souvent secrète) ni les identifiants Microsoft. Le player appelle `GET /api/data/{id}` avec son jeton d'écran.
- **ICS** : lecture avec la bibliothèque **Ical.Net** — récurrences (RRULE), fuseaux horaires, événements annulés ou « libres » ignorés. Liens `webcal://` acceptés.
- **Microsoft 365** : compte Microsoft connecté par l'organisation (`MicrosoftAuth`, même principe que `GoogleAuth`), jeton de renouvellement chiffré et renouvelé à chaque emploi. Lecture de `calendarView` (`/me/calendars/{id}` pour un agenda du compte, `/users/{adresse}` pour une salle ou une boîte), annuaire via `places/microsoft.graph.room`. Permissions déléguées en lecture seule, toutes disponibles pour les comptes personnels (outlook.com) comme pour les comptes professionnels : `Calendars.Read`, `Calendars.Read.Shared` ; OneDrive et SharePoint : `Files.Read.All`. L'annuaire des salles (`Place.Read.All`) et les sites SharePoint (`Sites.Read.All`) ne sont pas demandés : ils n'existent pas pour les comptes personnels. Le client ne configure rien : un bouton, la page de connexion Microsoft, et c'est tout.
- **CSV du menu** : colonnes `date; categorie; plat; prix; allergenes` (séparateur `;` ou `,`, dates `2026-10-12` ou `12.10.2026`, en-têtes français ou anglais) ; un nouvel import remplace le menu ; les lignes invalides sont signalées sans bloquer le reste. Modèle téléchargeable.
- **Confidentialité** : pour une porte de salle, l'option « Afficher l'intitulé des réunions » (sinon « Réservé ») est appliquée **sur le serveur** : l'intitulé ne quitte pas le serveur.
- **Hors ligne** : le player garde la dernière réponse et **calcule l'état Libre / Occupé avec sa propre horloge** toutes les 15 s. Après 10 min sans données, un avertissement « Données de HH:MM » s'affiche ; après 30 min, la salle passe en gris « Indisponible » (jamais de faux « Libre »).
- **Mise à l'échelle** : les listes pleines pages réduisent automatiquement leur texte pour tenir dans la zone libre (les bandeaux et angles réservent leur place).

### Vidéos : tous formats, lecture hors ligne
- **Un « plugin VLC » dans le navigateur n'est pas possible** : NPAPI/ActiveX ont été supprimés de tous les navigateurs (Chrome 2015). On utilise donc **ffmpeg ou VLC (gratuits) côté serveur** : à l'import, toute vidéo qui n'est pas un MP4 est convertie en **MP4 H.264, sans son, 1080p maximum**, le seul format lu partout (Chrome, WebView Android, Raspberry Pi) et mis en cache pour le hors ligne.
- **Détection automatique** : ffmpeg (PATH ou variable `LINKII_FFMPEG`) est préféré, sinon VLC (installation standard, PATH ou variable `LINKII_VLC`). La Médiathèque indique l'outil utilisé ; sans outil, seuls les MP4 H.264 sont acceptés et le message l'explique.
- **Conversion en arrière-plan** : la tuile affiche « Conversion en cours… » puis la vidéo devient disponible ; en cas d'échec (fichier corrompu, format inconnu) la tuile reste en erreur avec le motif. Une vidéo en cours de conversion ou en échec n'est ni publiable ni proposée dans les playlists. Les MP4 plus grands que 1080p (2K, 4K) sont aussi convertis (§20).
- **Lecture hors ligne côté écran** : le player télécharge la vidéo dans le cache du navigateur **avant** d'appliquer la playlist, puis la relit depuis ce cache sous forme de blob (`blob:`) — sans réseau et **sans dépendre du service worker**. Il redemande le stockage persistant et re-télécharge seul tout média purgé du cache.
- **Vérifié** : conversion VLC d'un AVI (MPEG-4) et d'un MKV (H.264) en MP4 lu par Chrome ; lecture en boucle de la vidéo **serveur coupé** (bordure rouge + pastille Hors ligne affichées).
- **Pour un vrai lecteur natif multi-formats** (sans conversion, hors ligne) : intégrer **LibVLC** (gratuit, LGPL) dans l'application Android de la clé / tablette. À prévoir avec l'APK.

### Configurer « Se connecter avec Microsoft » (administrateur Linkii, une seule fois pour toute la plateforme)
Les clients n'ont rien à créer ni à saisir : l'application Entra ID est celle de la plateforme.

1. **Portail Entra** (https://entra.microsoft.com) › **Identité › Applications › Inscriptions d'applications › Nouvelle inscription** : nom `Linkii`, type de compte « Comptes dans un annuaire d'organisation et comptes Microsoft personnels » (multilocataire), URI de redirection de type *Web* : `https://<domaine>/microsoft/callback` (en local : `http://localhost:<port>/microsoft/callback`).
2. **Certificats et secrets › Nouveau secret client** (24 mois au plus) : copier la *valeur* tout de suite, et noter la date d'expiration (à l'expiration, toutes les connexions Microsoft s'arrêtent).
3. **Autorisations d'API › Microsoft Graph › Autorisations déléguées** : `User.Read`, `offline_access`, `Calendars.Read`, `Calendars.Read.Shared`, `Files.Read.All`. Ne pas accorder le consentement administrateur pour le locataire de Linkii : chaque client consent à sa connexion.
4. **Image de marque et propriétés** : logo, page d'accueil, conditions d'utilisation, politique de confidentialité ; puis vérification de l'éditeur (Microsoft Cloud Partner Program), faute de quoi les utilisateurs d'autres organisations ne peuvent pas consentir eux-mêmes.
5. **Configuration du serveur** : `Linkii:Microsoft:ClientId`, `Linkii:Microsoft:ClientSecret` (variable d'environnement `Linkii__Microsoft__ClientSecret` en production) et `Linkii:Microsoft:RedirectUri` (identique à l'adresse déclarée à l'étape 1 ; à défaut, l'adresse du back-office + `/microsoft/callback`).

Si l'organisation d'un client interdit le consentement des utilisateurs, Linkii affiche « Votre organisation demande l'approbation de Linkii par son administrateur Microsoft 365 » : l'administrateur du client approuve l'application une fois (rôle Administrateur général ou Administrateur d'application cloud), puis l'utilisateur recommence.

### Obtenir un lien ICS (alternative sans compte d'application)
Il faut **publier le calendrier** ; le lien est collé dans le widget (Porte de salle, Horaires, Agenda).

- **Outlook sur le web (Microsoft 365)** : Paramètres (⚙) › Calendrier › **Calendriers partagés** › *Publier un calendrier* › choisir le calendrier et le niveau de détail (« Peut voir tous les détails » pour afficher les intitulés, « Peut voir quand je suis occupé » pour un simple Occupé) › **Publier** › copier le lien **ICS** (pas le lien HTML).
- **Google Agenda** : Paramètres et partage du calendrier › **Intégrer l'agenda** › copier l'**Adresse secrète au format iCal** (finit par `basic.ics`). Éviter l'adresse publique.
- **Salle de réunion Microsoft 365** : c'est une boîte aux lettres de ressource ; son calendrier se publie avec les droits du propriétaire ou d'un administrateur (sinon : DSI, ou connecteur Microsoft 365).

À savoir :
- Vérifier le lien : collé dans un navigateur, il doit **télécharger un fichier `.ics`** ; dans Linkii, **Tester la source** indique le nombre d'événements lus.
- Le lien est un **secret** (quiconque le possède lit le calendrier) ; Linkii le garde côté serveur. Republier le calendrier change le lien et invalide l'ancien.
- Un calendrier publié par Outlook peut mettre **jusqu'à environ une heure** à refléter une modification : c'est le principal défaut du lien ICS pour une porte de salle, le connecteur Microsoft 365 est plus réactif.
- Certaines organisations **désactivent la publication de calendrier** : l'option n'apparaît alors pas.

### Assistant de démarrage
L'étape 1 est **« Que voulez-vous afficher ? »** : *Des contenus · Porte de salle · Horaires du jour · Agenda · Menu du restaurant · Prochains départs*. Les usages de données créent seuls le widget, une playlist d'un contenu pleine page et la publiée ; la porte de salle présélectionne le matériel **Tablette Android**. L'étape 3 (code à 6 chiffres) est inchangée.

### Matériel (ex-« Type d'écran »)
Renommé **Matériel** : téléviseur, moniteur, totem / borne, **tablette Android** (retenue pour la porte de salle). Le player tourne tel quel dans Chrome en kiosque sur la tablette ; **l'application WebView / APK reste à écrire**.

### Limites connues
- **Vidéos hors ligne** : lecture depuis le cache en blob (voir ci-dessus) ; le service worker sert aussi les requêtes `Range` par tranches (plus de chargement complet en mémoire). Les très gros fichiers restent limités par le quota de stockage de la clé ; la conversion plafonne à 1080p / 3,5 Mb/s avec VLC.
- **YouTube** : pas de lecture hors ligne (ni cache), pas de son, et certaines vidéos refusent l'intégration (le lecteur affiche alors une erreur). Une page sans référent HTTP provoque l'« erreur 153 » (le lien d'intégration ouvert seul dans un onglet l'affiche : normal). Vérifier les conditions d'utilisation de YouTube pour un usage d'affichage public.
- **Connexion Microsoft non testée contre un vrai compte** : l'adresse de consentement, les retours d'erreur et la déconnexion sont couverts par des tests (`MicrosoftAuthTests`), mais ni l'échange du code, ni la lecture des agendas, de l'annuaire ou des fichiers n'ont été vérifiés avec un vrai compte Microsoft 365. À valider une fois l'application Entra créée.
- Les données sont **rafraîchies par sondage** (30 s pour une salle, 60 s ailleurs), pas poussées par SignalR.
- Le secret client Microsoft est stocké **en clair** dans `data.json`.
- Pas de réservation depuis la tablette (catégorie C), pas de Google Calendar (hors lien ICS).
- Un seul menu pour toute l'organisation ; un seul fuseau horaire (celui des Réglages).
- Si les angles et bandeaux sont tous utilisés, la zone de contenu devient petite ; les listes réduisent leur texte en conséquence.

### Hors périmètre pour l'instant
- **C. Interactif / tactile** : réservation depuis la tablette, annuaire interactif, enregistrement des visiteurs, sondages.
- **D. Commercial** : publicité ciblée, preuve de diffusion, partage des revenus 30 / 70, sponsors.
- **E. Supervision** : tableaux de bord d'équipe, états de services.

### Points à trancher
1. **Offre** : le mode Porte de salle et les widgets de données sont-ils inclus dans le tout-inclus à 5 / 9, ou dans un palier supérieur ? *(question restée ouverte — la réponse « inclus ou palier supérieur » n'a pas tranché)*
2. **Validation informatique** : quand contacter la DSI de l'UNIL pour le consentement administrateur Microsoft ? Prévoir une alternative si le lien ICS est désactivé.
3. **Menu** : un import CSV par restaurant, ou plusieurs restaurants par organisation ?
4. **Application Android** (WebView / APK en mode kiosque) pour la tablette : à planifier.
5. **Réservation depuis la tablette** (catégorie C) : à confirmer comme étape suivante.

## 10. Points ouverts (techniques)
- [ ] Apps : voir §12 (« Reste à faire »).
- [ ] Clé HDMI à tester en premier : Fire TV, Android TV ou Raspberry Pi ? (étape APK)
- [x] Application Android (WebView, kiosque, hors ligne) : `Linkii.AndroidPlayer/`, §19. Reste : essai sur un vrai boîtier (fluidité des vidéos), publication Google Play, PIN piloté depuis le back-office.
- [ ] Tester le redémarrage hors ligne du player (service worker) dans un vrai Chrome / WebView.
- [ ] Installer ffmpeg sur le serveur de production srvweb01 (Debian 12 : `apt install -y ffmpeg`, puis redémarrer Linkii) : meilleur rendu et plus rapide que VLC, et seul outil détecté sur Linux (§20).
- [ ] Tester le connecteur Microsoft 365 sur un vrai locataire (voir §9, limites connues).
- [ ] Indicateur hors ligne : le rendre configurable (aucun / icône / bordure) dans les Réglages ? Une bordure rouge visible du public n'est pas toujours souhaitable.
- [ ] Les widgets d'angle réservent une rangée en haut / en bas ; alternative possible : colonnes latérales.

## 11. Multi-tenant et white-label (implémenté)

**Hiérarchie** : revendeur → client → écran, une seule instance. Chaque ligne (écran, média, playlist, menu) porte le `ClientId` ; les utilisateurs portent leur revendeur et leur client.

### Comptes et rôles
| Rôle | Où | Voit |
|---|---|---|
| **PlatformAdmin** (équipe Linkii) | domaine principal | Console : Revendeurs, Clients et accès (+ journal), Supervision, Facturation |
| **ResellerAdmin** | domaine de son revendeur | Clients, Marque, Équipe, Abonnement ; peut *entrer* dans l'espace de ses clients |
| **ClientAdmin** | domaine de son revendeur | Administrateur de l'organisation : toutes les aires, aires, membres, réglages, intégrations |
| **ClientMember** | domaine de son revendeur | Membre : un rôle par aire de gestion (Utilisateur ou Administrateur), voir §14 |

- **Premier lancement** : le revendeur par défaut « Linkii » et le compte plateforme sont créés. Sans configuration, `admin@linkii.local` reçoit un mot de passe aléatoire **affiché une seule fois dans la console** ; sinon `Linkii:PlatformAdmin:Email` / `Linkii:PlatformAdmin:Password`. Le mot de passe se change dans *Mon compte* (menu utilisateur).
- **Création de compte libre-service** (« Créer un espace » sur /login) : uniquement pour les revendeurs avec `AllowSelfSignup` (Linkii par défaut). Par e-mail, Microsoft ou Google, avec validation de l'adresse et essai gratuit : voir §15.
- **Migration** : un ancien `data.json` mono-client est converti au démarrage (sauvegarde `data.json.pre-multitenant.bak`) en un client du revendeur Linkii.
- **Données** : `Linkii:DataDir` (défaut : dossier du projet) contient `data.json`, `media/` et `brand/`.
- **E-mails (SMTP)** : **réglable dans la console › Réglages de la plateforme** (serveur, port, SSL, identifiant, mot de passe chiffré, adresse d'expédition ; bouton « envoyer un e-mail de test » ; commun à toutes les organisations) — à défaut, la configuration du serveur : `Linkii:Smtp:Host`, `Port` (587), `User`, `Password`, `From` (adresse d'expédition), `Ssl` (true). À la création d'un client, d'un revendeur ou d'un membre d'équipe revendeur, le compte reçoit un mot de passe provisoire par e-mail (nom d'expéditeur = marque du revendeur, réponse à `SenderEmail`) et doit le changer à la première connexion (`/login/password`, `User.MustChangePassword`). Sans SMTP configuré, le mot de passe provisoire est affiché à la personne qui crée le compte.
- **Historique de diffusion** : le player note chaque contenu affiché en plein écran (contenu, début, durée) dans son stockage local et l'envoie par lots chaque minute (`POST /api/player/plays`, 2 000 diffusions au plus en attente hors ligne). Stockage : `plays/<client>.jsonl` dans le dossier des données (hors `data.json`), conservé 90 jours (`PlayLog`). Affiché dans *Statistiques*.
- **Écran découpé** : `Screen.Layout` (`ScreenLayouts` : 1, 50-50, 66-33, 33-33-33), zone 1 = `PlaylistId`, zones 2 et 3 = `ZonePlaylistIds`. Découpages permis selon le format (`ScreenLayouts.For`) : smartphone 1 zone, tablette 2, portrait 2 (1/2 seulement en HD), paysage HD 2, paysage Full HD+ 3. Publication : `Published` (zone 1 + widgets) et `PublishedZones` ; le player reçoit `layout` {dir, sizes} et `zones`, chaque zone tourne sa propre boucle (`zoneLoop`).
- **Canva** (Connect API, `CanvaService`) : `Linkii:Canva:ClientId`, `Linkii:Canva:ClientSecret`, `Linkii:Canva:RedirectUri` (défaut `https://<BaseDomain>/canva/callback`, à déclarer dans le portail développeurs Canva ; scopes `design:meta:read design:content:read profile:read`). Intégration unique pour la plateforme (plus de saisie par organisation) ; chaque organisation connecte seulement son compte dans Intégrations › Canva › Gérer (`/canva/connect`, OAuth + PKCE ; jeton de renouvellement chiffré dans `Tenant.CanvaRefreshToken`). L'app « canva » exporte le design choisi (PNG par page ou MP4) dans la médiathèque (`Info["canva"]`) et le diffuse comme un diaporama ; un nouvel export remplace les fichiers à l'enregistrement.
- **Connexion LinkedIn et GitHub** (`ExternalLogin`) : « Continuer avec LinkedIn / GitHub » sur la page de connexion et d'inscription (identité seulement). LinkedIn : OpenID Connect, identité lue sur `/v2/userinfo` (e-mail vérifié exigé). GitHub : adresse principale vérifiée lue sur `/user/emails` (portée `user:email`, adresses noreply écartées). Configuration : `Linkii:LinkedIn:ClientId|ClientSecret|RedirectUri` et `Linkii:GitHub:ClientId|ClientSecret|RedirectUri` (variables `Linkii__LinkedIn__ClientSecret`…) ; retours `/linkedin/callback` et `/github/callback` (GitHub n'accepte qu'une adresse : fixer `RedirectUri` sur le domaine principal). Les boutons n'apparaissent que si le couple identifiant / secret est renseigné. Pas de rattachement automatique par e-mail (seul Google le fait).
- **Compte Google** (`GoogleAuth`) : « Se connecter avec Google » par organisation (OAuth 2 + PKCE, `access_type=offline`, `include_granted_scopes`), un seul compte partagé par **Google Calendar** (`calendar.readonly`, `GoogleConnector` : événements et liste des agendas) et **Google Drive** (`drive.readonly`, `DriveService`) ; chaque intégration ajoute son accès (`/google/connect?for=calendar|drive`). Application OAuth de la plateforme : `Linkii:Google:ClientId`, `Linkii:Google:ClientSecret`, `Linkii:Google:RedirectUri` (défaut : adresse du back-office + `/google/callback`). Jeton de renouvellement chiffré `Tenant.GoogleRefreshToken`, accès accordés `GoogleScopes`, compte `GoogleUser` (ancienne connexion Drive `GoogleDrive*` reprise au démarrage) ; déconnexion = révocation chez Google (arrête les deux). Le compte de service Google Agenda est abandonné.
- **Mise à jour des écrans** : `/api/player/version` renvoie aussi `build`, empreinte des fichiers de `wwwroot/player` calculée au démarrage ; un écran ouvert recharge sa page dès qu’elle change (nouveaux rendus d’apps), sans attendre le rechargement nocturne.
- **Texte libre** : l'app `text` est un widget d'écran (comme horloge et météo), avec largeur en % et défilement ; elle n'est plus proposée dans les playlists (les contenus existants restent diffusés).

### Domaines
- Le revendeur est déduit du **nom de domaine** de chaque requête (`ResellerResolver`) : domaine propre déclaré, ou `slug.<Linkii:BaseDomain>` (défaut `linkii.com`), ou `slug.localhost` en local (ex. `http://acme.localhost:5080`). Domaine neutre (localhost, IP) = revendeur par défaut ; sous-domaine inconnu = 404.
- Un compte ne se connecte que sur le domaine de son revendeur ; l'équipe Linkii sur le domaine principal. Le cookie est revérifié à chaque requête HTTP (compte actif, domaine, client et revendeur non suspendus).
- **Domaine propre** : déclaré par l'équipe Linkii (console › Revendeurs), le revendeur crée un CNAME vers `Linkii:CnameTarget`. Caddy émet le certificat à la demande en interrogeant `GET /internal/tls-allow?domain=` (voir `deploy/Caddyfile`).
- **Écrans** : adresse neutre, le **code d'appairage identifie le client**. Le domaine (ou `?r=slug` sur l'URL du player) ne sert qu'à habiller l'écran d'appairage ; ensuite la marque vient du client de l'écran et est mémorisée pour un démarrage hors ligne.

### Isolation
- Le back-office n'accède aux données que via `TenantStore` / `ClientDb` / `ScopedList<T>` (filtre global par `ClientId`) ; sans espace client sélectionné, l'accès échoue au lieu de retomber sur un autre client.
- API player : l'écran ne lit que ses widgets et son menu ; client ou revendeur suspendu = 403 (le player affiche « Service suspendu »).
- **Médias** : `/media/{fichier}` n'est servi qu'à un écran du client propriétaire (en-tête `X-Token` ou `?t=`) ou à un utilisateur connecté dans l'espace de ce client ; sinon 404.
- **Tests** : `dotnet test` dans `Linkii.Poc.Tests` (données dans un dossier temporaire) : isolation des listes, liens de médias, données des widgets, suspension, résolution par domaine, TLS, accès au back-office.

### Marque (par revendeur)
Nom affiché, couleur principale, logo (PNG / JPEG / WebP ≤ 1 Mo, SVG refusé), adresse d'envoi, mention « Propulsé par Linkii ». Appliquée au back-office, à la connexion, à l'assistant et au player ; un changement notifie tous les écrans du revendeur. La couleur par client (anciens Réglages) est supprimée : c'est celle du revendeur.

### Impersonation
*Entrer* dans un client (revendeur pour ses clients, équipe Linkii pour tous) : bandeau jaune, cookie de session, entrée et sortie **journalisées** (console › Clients et accès).

### Limites connues
- Le contrôle de session ne s'applique qu'aux requêtes HTTP : un circuit Blazor déjà ouvert survit jusqu'à son rechargement après une suspension.
- Questions ouvertes : support de premier niveau chez les clients des revendeurs, et mention « Propulsé par Linkii » (réglable par revendeur pour l'instant).

## 12. Catalogue d'apps (phase 1)

Une **app** est décrite par un **manifeste** JSON (`Apps/*.json`) : identité, positions possibles, paramètres, fournisseur de données. Le formulaire de paramétrage est généré à partir de ce manifeste et validé côté serveur. Un manifeste invalide est refusé au démarrage (journal d'erreur) ; `AppCatalog.Check` le contrôle.

| App | Type | Fournisseur serveur | Positions | Paramètres |
|---|---|---|---|---|
| 🕒 **Horloge** | intégrée | — | toutes | format 12/24 h, secondes, date, fuseau |
| ⛅ **Météo** | intégrée | `weather` (Open-Meteo) | toutes | ville, unité |
| 📅 **Agenda** | intégrée | `calendar` (ICS ou Microsoft 365) | pleine page, 4 angles, bandeaux haut et bas | titre, source, lien ICS (secret), calendrier M365, nombre de jours |
| 📝 **Texte / annonce** | sans code | — | toutes | titre, texte, couleurs, taille, alignement, période de validité |
| 📰 **Flux RSS** | sans code | `rss` | pleine page, bandeaux | adresse du flux, titre, nombre d'articles, résumés |
| in **LinkedIn** | sans code + fournisseur `linkedin` | `linkedin` | pleine page (liste ou une à la fois), bandeaux, angles | **Page entreprise** : connexion OAuth d un administrateur, publications lues par l API officielle, validation avant diffusion, QR code. **Publications choisies** : liens collés (aperçu officiel) ou textes saisis (`date:`, `image:`, `lien:`, blocs séparés par `---`). Voir « LinkedIn : mise en service » |
| 🌐 **Page web** | sans code | `webpage` (test seulement) | pleine page | adresse, rechargement périodique |
| ▶️ **YouTube** | intégrée | `youtube` (test oEmbed) | pleine page + 4 angles | lien vidéo / playlist, son, début, fin, durée maximale |
| 🍽️ **Menu restaurant CSV** | intégrée | `menu-csv` | pleine page + 4 angles | titre, source (**fichier CSV importé** ou **lien d'un CSV publié**), prix, allergènes |
| 🍽️ **Menu restaurant API** | intégrée | `menu-api` | pleine page + 4 angles | titre, adresse de l'API (secret), authentification (aucune / Bearer / clé dans un en-tête), chemin de la liste, noms des champs, prix, allergènes |

Types de champ : `text`, `textarea`, `number`, `select`, `bool`, `color`, `date`, `url`, `secret`, `mailbox`, `youtube`, `textfile` (fichier texte importé : lu en UTF-8 ou ISO-8859-1, **stocké chiffré comme un secret**, donc jamais envoyé à l'écran ; `template` = lien d'un fichier modèle).

**Menus de restaurant** (`Apps/menu-csv.json`, `Apps/menu-api.json`, code dans `Apps/MenuApps.cs`) : les deux apps produisent les mêmes données (`kind = menu`, plats d'aujourd'hui + 7 jours) et réutilisent le rendu « menu » du player (`makeApp` → `buildData`). Le CSV est lu par `MenuCsv` (colonnes `date`, `categorie`, `plat` ; `prix`, `allergenes` facultatives). L'API est lue par `MenuJson` : liste de plats, objet contenant la liste (`rows`, `menu`, `items`, `data`…), ou jours contenant leurs plats ; noms de champs reconnus sans tenir compte des accents ni de la casse, ou fixés dans « Noms des champs ». Appels sortants par `SafeHttp` (pas de réseau interne), cache de 5 minutes avec repli sur la dernière version connue. Les menus importés dans « Données » (ancien widget) restent disponibles. `showIf` (« clé=valeur ») masque un champ ; un champ masqué n'est ni exigé ni enregistré.

### Parcours
1. **Intégrations** (`/integrations`, ancienne adresse `/settings?tab=applications` redirigée) : un interrupteur par app du catalogue la rend **disponible** dans les listes de lecture (`AppInstall`, liée au client). Le désactiver est **non destructif** : l'app disparaît des choix, ses contenus existants restent modifiables et diffusés. Fiche détaillée : `/apps/{id}`.
2. **Playlist › panneau Apps** : cliquer une app ouvre son formulaire (nom + paramètres + **Tester**) ; à l'enregistrement, le contenu est créé et ajouté à la fin de la playlist. Le ⚙ d'un contenu d'app réunit ses paramètres et sa position / durée dans la playlist. Le contenu apparaît aussi dans la **Médiathèque** (⚙ → `/apps/instance/{id}`) et peut être ajouté à d'autres playlists depuis celle-ci.
3. **Positions** : celles proposées viennent du manifeste.
4. **Publier, écran par écran** (page Écrans : cases à cocher + bouton **Publier** en haut, ou menu ⋮ › Publier, toujours avec confirmation) : `Notifier.PublishScreen` fige la playlist de l’écran **et ses widgets** dans `Screen.Published` (`PublishedItem`). Les autres écrans de la même playlist ne bougent pas. Toute modification (playlist : `Playlist.Touch()` → `Revision++` ; widgets : `WidgetsRevision++` ; autre playlist choisie) fait apparaître « Modifications à publier » sur la carte de l’écran (`Helpers.HasPending`). Choisir une playlist ou appairer un écran publie aussitôt. Un écran jamais publié depuis ce changement lit encore l’ancien instantané de la playlist (`Playlist.Published`, repli). Les données serveur (`/api/data/{id}`) lisent aussi l'instantané publié, pas la configuration en cours d'édition.
5. **Écran** : `apps.js` (un module de rendu par app, mis en cache hors ligne) ; l'agenda réutilise le moteur des listes de données.

### Bandeaux défilants
Les bandeaux haut et bas (agenda, flux RSS) passent par `host.ticker` (`player.js`) : l'agenda affiche une ligne « Aujourd'hui 14:30 Réunion • Demain … » qui **défile seulement si elle est plus large que l'écran** (sinon elle est centrée et immobile) ; le flux RSS défile toujours. Le texte inchangé n'est pas redessiné : le défilement ne repart plus à zéro toutes les 15 secondes (défaut qui touchait le bandeau RSS).

### Sécurité
- **Secrets** (lien ICS…) : chiffrés (Data Protection, clés dans `<DataDir>/keys`), jamais renvoyés au formulaire (champ vide = conservé), jamais envoyés au player.
- **Appels sortants** (`SafeHttp`) : l'adresse IP est contrôlée à la connexion (redirections et DNS compris) : pas de boucle locale, réseau privé, lien local ni métadonnées cloud ; schémas http / https seulement ; 2 Mo et 15 s maximum ; pas de proxy. Utilisé aussi pour les liens ICS des anciens widgets.
- Flux RSS : DTD interdite (XXE), HTML retiré des résumés, liens non http ignorés.
- Un écran ne lit que les données de **sa** playlist et de **son** client.

### Ajouter une app
Déposer un manifeste dans `Apps/`. Si elle lit des données : implémenter `IAppProvider` (`Fetch`, `Test`), l'enregistrer dans `Program.cs`, et ajouter son rendu dans `wwwroot/player/apps.js` (`LinkiiApps.register`). Une app « sans code » n'a besoin que du manifeste et d'un rendu générique.

Toute nouvelle app suit le **standard de configuration d'un contenu** (§30) : classer ses champs en zones (source, main, dvanced) dans le manifeste.

### Reste à faire
- Migrer les 4 anciennes apps de données (porte de salle, horaires, menu, départs) : leur éditeur et l'assistant de démarrage ont été retirés, elles ne se créent plus ; d'éventuels éléments existants continuent de s'afficher par l'ancien chemin (`AppKind`).
- Console Linkii : gestion du catalogue (publier une app / version, la réserver à une formule) ; masquer des apps par revendeur.
- Connexion au niveau de l'installation (identifiants partagés par les instances) : les connexions de calendrier sont désormais réglées une fois par source (Intégrations › Calendriers partagés). Microsoft 365 et OneDrive passent par « Se connecter avec Microsoft » (plus de secret par client) ; le secret de l'application Entra de la plateforme est dans la configuration du serveur.
- Aperçu du rendu dans l'éditeur ; mise à jour de version d'une app (instances épinglées sur leur version).
- Derrière un proxy d'entreprise, les appels sortants échouent (le contrôle d'adresse impose la connexion directe).

## 13. YouTube (app) et retraits

**App YouTube** (`Apps/youtube.json`, rendu dans `apps.js`, moteur de lecture dans `player.js`) :
- **Lecture** : API IFrame de YouTube (`youtube-nocookie.com`), sans commandes. Vidéo ou playlist (`watch?v=`, `youtu.be/`, `shorts/`, `embed/`, `playlist?list=`) ; le lien est normalisé à l'enregistrement. Seul le domaine exact de YouTube est accepté.
- **Paramètres** : lien, son (si la lecture automatique avec son est refusée, repli en muet après 2,5 s), début et fin en secondes (vidéo seule), durée maximale dans la boucle (1 à 240 min, 30 par défaut).
- **Paramètres par élément de playlist** : les lignes de la page Playlists n'affichent qu'un résumé (position, durée) ; le bouton « ⚙ Paramètres » ouvre une fenêtre modale avec les réglages utiles du contenu (position pour une app, durée pour tous). Chaque élément YouTube choisit « Jusqu'à la fin » (défaut, plafonné par la durée maximale de l'instance) ou « Durée fixe » (en secondes, initialisée à la durée de la vidéo si elle est connue) : la vidéo est alors coupée ou rejouée à ce terme. Le choix est propre à l'élément : la même instance peut figurer plusieurs fois avec des durées différentes.
- **Durée « content »** : nouveau mode de manifeste (`"duration": "content"`). Dans une boucle de plusieurs contenus, l'écran passe au suivant à la fin de la vidéo ou à la durée maximale ; seule ou en incrustation, la vidéo recommence. Dans les playlists, la durée n'est plus saisie (« Jusqu'à la fin du contenu »).
- **Test** : le serveur interroge oEmbed (sans clé d'API) : vidéo ou playlist trouvée, introuvable, ou privée / intégration désactivée (avertissement).
- **Erreurs** : code d'erreur YouTube affiché sur l'écran, puis passage au contenu suivant. Pas de lecture hors ligne.
- **Miniature** dans la Médiathèque et les playlists, avec la **durée** de la vidéo (ou « Playlist »). La durée est lue par le navigateur du back-office via l'API IFrame de YouTube (`wwwroot/yt-probe.js`, composant `YoutubeDurations`), mémorisée dans `MediaItem.Info` et effacée si le lien change. Elle apparaît quelques secondes après l'ouverture de la page ; elle manque si YouTube est injoignable ou refuse l'intégration.
- **Plusieurs vidéos dans une playlist** : le lecteur attend l'insertion du widget dans la page avant de créer le player YouTube (la 2e vidéo restait vide quand l'API était déjà chargée).

**Retirés** : assistant de démarrage (`/start`, `Onboarding`), éditeur de widgets de données (`DataWidgetEditor`), pack d'exemples (`DemoSlides`), zone de glisser-déposer de la Médiathèque (remplacée par les boutons Images et Vidéos), ancien chemin YouTube (`AppKind = youtube`).


## 14. Aires de gestion et membres (8 octobre 2026) — rôles remplacés par les niveaux de droits (§46)

Une organisation range ses écrans, listes de lecture et médias dans des **aires de gestion** (un site, un bâtiment, un service). Les membres d'une aire partagent tout son contenu ; les autres aires leur sont invisibles. Les **zones** d'une aire classent ses écrans (filtres de la page Écrans), sans effet sur les droits.

| Rôle | Contenus de l'aire (écrans, listes, médias, publier) | Zones et membres de l'aire | Aires, déplacements entre aires, réglages, intégrations, widgets |
|---|---|---|---|
| Utilisateur d'aire | oui | non | non |
| Administrateur d'aire | oui | oui | non |
| Administrateur de l'organisation (`ClientAdmin`, plusieurs possibles) | oui, toutes les aires | oui | oui |

- **Modèle** : `Area` (`Db.Areas`, `Zones`) ; `Screen`, `Playlist`, `MediaItem` portent `AreaId` (`IAreaOwned`), `Screen.ZoneId` ; `User.LevelId` (niveau de droits, §46 : le rôle par aire a disparu), `User.LastAreas` (aire ouverte, par organisation), `LastLoginUtc` / `LastActiveUtc` (actifs sur 30 jours).
- **Isolation** : `TenantStore` ouvre un `ClientDb` limité à l'aire ouverte (`AreaAccess`) : `Screens`, `Media`, `Playlists` ne voient que cette aire, les ajouts y sont rangés. Exception : les fichiers des dossiers Drive (`AreaId` vide) sont communs à toutes les aires, les connexions (calendriers, Drive, Canva) restant celles de l'organisation. Un membre sans aire ne voit rien. Le player et `Notifier` lisent la racine : sans changement.
- **Migration** : au démarrage, chaque organisation reçoit une aire « Principale » (`Seed.EnsureAreas`) où rejoignent ses données ; un nouveau client la reçoit à sa création.
- **Pas d'interrupteur** : avec une seule aire et aucun membre, le back-office est celui d'avant. Le sélecteur d'aire (haut du menu) et les pages **Aires de gestion** (`/areas`) et **Membres** (`/members`) apparaissent dès la deuxième aire ou le premier membre ; entrée dans **Réglages › Organisation**.
- **Déplacements** (administrateur de l'organisation, `AreaOps`) : un écran garde sa publication et perd les listes de son ancienne aire ; une liste de lecture part avec les contenus qu'elle seule utilise (sinon refus en nommant les listes concernées) ; un fichier ne part que s'il n'est utilisé par rien dans son aire. Une aire ne se supprime que vide, jamais la dernière.
- **Console** : utilisateurs (administrateurs, membres, actifs sur 30 jours, invitations) et aires par organisation, détail des comptes dans une fenêtre ; colonnes Utilisateurs dans l'espace revendeur et Facturation.
- **Tests** : `AreasTests` (visibilité, migration, zones, déplacements, suppression, comptage).

## 15. Création de compte organisation et essai gratuit (9 octobre 2026)

Inscription libre et gratuite (revendeurs avec `AllowSelfSignup`, Linkii par défaut), essai de **7 jours** réglable dans **Console › Réglages** (`/admin/settings`, `Db.Platform`, commun à toutes les organisations). Maquette : `maquettes/inscription-organisation.html`.

| | Par e-mail | Avec Microsoft ou Google |
|---|---|---|
| Formulaire | organisation, nom, e-mail, mot de passe + consentements | retour du fournisseur, puis organisation + consentements (« Dernière étape ») |
| État à la création | **À valider** : seul `/login/verify` est accessible | **Validé** (adresse prouvée par le fournisseur) |
| Validation | lien `/login/verify/confirm?t=` (48 h, usage unique, empreinte SHA-256 seule stockée) | — |
| Ensuite | e-mail de bienvenue, essai démarré | e-mail de bienvenue, essai démarré |

- **Consentements** (`ConsentSwitches`) : conditions générales **obligatoires** (fenêtre `TermsDialog`, texte provisoire `TermsText`, version `Terms.Version`), offres commerciales et nouveautés et astuces **facultatives**, désactivées par défaut. Stockés sur `User` (version et date des CGU, `MarketingOptIn`, `NewsOptIn` et leurs dates), modifiables dans Réglages › Compte et Mon compte (`ConsentPrefs`).
- **Vérifiez votre e-mail** (`VerifyEmail.razor`) : corriger l'adresse (faute de frappe, nouveau lien aussitôt), renvoyer (une fois par minute, cinq par heure), fermer (déconnexion). L'ancien lien ne marche plus après un nouvel envoi.
- **Microsoft / Google** (`ExternalLogin`) : identité seulement (`openid email profile`), applications OAuth et adresses de retour existantes (`/google/callback`, `/microsoft/callback` reconnaissent leur `state`). Le retour donne un ticket à usage unique repris sur le domaine du revendeur (`/login/sso/finish`). Compte relié par `User.ExternalProvider` + `ExternalId` (pas de mot de passe Linkii) ; boutons aussi sur la page de connexion. Google exige `email_verified` et relie un compte existant de même adresse ; Microsoft ne relie jamais par adresse (l'e-mail d'un annuaire n'est pas garanti).
- **Connexion rapide** (`SsoButtons`) : les mêmes boutons Microsoft, Google, LinkedIn et GitHub servent à se connecter et à s'inscrire. Compte existant : connexion ; sinon : « Dernière étape » de la création d'un espace. La fenêtre « Créez votre espace » ne propose plus que l'inscription par e-mail. Un refus du fournisseur (ex. portée LinkedIn non autorisée) affiche son motif et le journalise, distinct d'une annulation par la personne.
- **Essai** : `Tenant.TrialEndsUtc` = validation + durée réglée (une modification ne touche que les nouveaux comptes). Bandeau dans l'espace de l'organisation ; rappel par e-mail 2 jours avant ; à la fin, les écrans affichent « Essai terminé » (`Tenancy.Blocked` → 403 `{reason:"trial"}`), le back-office reste accessible. Console : prolonger, ou mettre fin à l'essai (client abonné).
- **Clients créés par un revendeur ou par Linkii** : pas d'essai ; validés à la première connexion avec le mot de passe provisoire reçu par e-mail. Migration : les comptes déjà connectés sont validés (`Signup.MarkVerified`).
- **Console › Clients et accès** : colonnes Inscription (`Tenant.SignupSource` : E-mail, Google, Microsoft, Revendeur, Linkii), Créé le, État (À valider / Validé), Essai (jours restants), E-mails (consentements) ; filtres par état et par type d'inscription ; actions renvoyer le lien, valider, essai, entrer, suspendre.
- **Tâche de fond** (`SignupWorker`, toutes les 30 min) : suppression des inscriptions jamais validées après 7 jours (réglable), rappel de fin d'essai, écrans prévenus à la fin de l'essai.
- **E-mails** : tous les e-mails de la plateforme portent en pied de page un lien vers le site (`Linkii:SiteUrl`, défaut `https://linkii.com`). Sans SMTP, leur contenu (lien de confirmation compris) est écrit dans le journal du serveur, pour tester en local.
- **Tests** : `SignupTests` (consentements, lien, renvoi, correction, remplacement, Microsoft/Google, purge, fin d'essai, migration) et `SignupFlowTests` (lien de bout en bout, player « Essai terminé », page d'inscription).

## 16. Comptes connectés (9 octobre 2026)

Maquette validée : `maquettes/comptes-connectes.html` (option A, deux onglets). Les **comptes** (Microsoft, Google, Canva, serveurs CalDAV et Exchange) sont séparés des **intégrations** qui les utilisent.

- **Page Intégrations** : deux onglets, *Intégrations* et *Comptes connectés* (`/integrations?tab=comptes`, pastille rouge si un compte est à reconnecter). Bouton **Connecter un compte** : types pas encore connectés, grisés si la plateforme n'est pas configurée pour ce fournisseur.
- **Modèle** : un compte par fournisseur et par organisation, sans nouvelle table. `ConnectedAccounts.Of(tenant)` (`Apps/ConnectedAccounts.cs`) calcule la liste à partir des champs existants (`Ms*`, `Google*`, `Canva*`, `CalendarAccounts` caldav / ews) ; `ConnectedAccounts.Kinds` relie chaque type aux cartes d'intégration qui l'utilisent.
- **Compte perdu** : quand le fournisseur refuse le jeton, le compte n'est plus effacé mais marqué (`MsLostUtc`, `GoogleLostUtc`, `CanvaLostUtc`) : adresse et accès d'avant restent affichés, avec « Reconnecter ». Une connexion réussie ou une déconnexion efface la marque.
- **Accès** : depuis Comptes connectés, Microsoft et Google demandent agendas et/ou fichiers en une fois (`/google/connect?for=all&tab=comptes`, `AccountPurposes`) ; un seul accès accordé suffit, l'autre s'affiche « non autorisé » avec **Autoriser**. Au retour, les intégrations dont l'accès est accordé sont activées. Microsoft : un autre compte remplace les accès au lieu de les cumuler.
- **Cartes d'intégration** : compte utilisé (pastille cliquable vers le compte), « Sans compte » ou « Aucun compte connecté » ; états « Compte à reconnecter » et « Accès à autoriser ». Les fenêtres *Gérer* (calendriers, Drives, Canva) n'ont plus de bouton Déconnecter mais **Voir le compte** ; la déconnexion se fait dans Comptes connectés, après une fenêtre qui liste les intégrations touchées.
- Catégorie du manifeste Canva : « Service en ligne » (groupe « Services en ligne »).
- **À trancher plus tard** : comptes par aire de gestion plutôt que par organisation ; plusieurs comptes par fournisseur (il faudrait alors une vraie liste de comptes dans le modèle).

## 17. Parcourir les Drives et sous-dossiers (9 octobre 2026)

Maquette validée : `maquettes/drive-parcourir.html`. Aucune nouvelle autorisation : la lecture seule déjà accordée (`drive.readonly`, `Files.Read.All`) suffit pour parcourir.

- **Sélecteur de dossier** (`DriveFolderPicker`, ouvert par **Choisir un dossier…** dans Intégrations › Drive › Gérer) : emplacements à gauche (Google : Mon Drive, Partagés avec moi, Drive partagés ; Microsoft : Mon OneDrive, Partagés avec moi, SharePoint), fil d'Ariane, recherche d'un dossier par son nom. Un clic sélectionne un dossier, la flèche ou un double-clic l'ouvre ; sans sélection, c'est le dossier ouvert qui est ajouté. Le sélecteur montre les images et vidéos du dossier (ce que les écrans afficheront) et compte les autres fichiers, ignorés. Les dossiers déjà ajoutés sont marqués « Déjà ajouté » (`DriveSources.SameRemote` : pour Google, la clé de sécurité du lien ne compte pas).
- **Lien de partage** : toujours possible (« ou coller un lien de partage »), vérifié par `DriveService.Resolve`.
- **SharePoint** : lister les sites demanderait `Sites.Read.All` (donc reconnecter les comptes Microsoft) ; on colle le lien d'une bibliothèque, puis on la parcourt dans le sélecteur. Les dossiers SharePoint partagés avec le compte apparaissent dans « Partagés avec moi ».
- **Lecture** (`DriveService.Browse`, `SearchFolders`) : Google Drive API v3 (`'id' in parents`, `sharedWithMe`, `drives`, recherche `name contains` sur tous les Drive) ; Microsoft Graph (`/me/drive/root/children`, `/me/drive/sharedWithMe` via `remoteItem`, `/drives/{d}/items/{i}/children`, `/me/drive/root/search`). La recherche OneDrive ne couvre que le OneDrive du compte ; un résultat de recherche Google a pour emplacement « Google Drive › Nom ».
- **Sous-dossiers** (`DriveFolder.Subfolders`) : case **Inclure les sous-dossiers** à l'ajout, puis menu ⋮ du dossier (la synchronisation est relancée aussitôt). Tous les niveaux sont parcourus, 300 fichiers au plus au total (`MaxFiles`), 200 sous-dossiers au plus, 5 000 éléments lus au plus. Au-delà de 300, les fichiers laissés de côté sont comptés (`DriveFolder.Skipped`) et un avertissement s'affiche sur la ligne du dossier. Un fichier présent dans plusieurs dossiers (Google) n'est copié qu'une fois.
- **Ordre « par nom »** : selon le chemin dans le dossier (`Info["drivePath"]`, ex. `2026/affiche.jpg`), donc sous-dossier puis fichier. Un fichier déplacé d'un sous-dossier à l'autre garde sa copie, seul son chemin est mis à jour. Les fichiers synchronisés avant cette version reçoivent leur chemin à la synchronisation suivante.
- **Menu ⋮ d'un dossier** : inclure les sous-dossiers, renommer (sur la ligne), ouvrir dans Google Drive / OneDrive (`DriveFolder.WebUrl`, déduite de l'identifiant pour Google si absente), retirer.
- **Modèle** : `DriveFolder` gagne `WebUrl`, `Subfolders`, `Skipped`, `SubfolderCount` (sous-dossiers vus à la dernière synchronisation ; sans l'option, les directs seulement).
- **Non vérifié contre les vrais comptes** dans le navigateur (connexion au back-office requise) : à tester avec un compte Google et un compte Microsoft connectés.

## 18. Simulateur d'écrans (9 octobre 2026)

Maquette validée : `maquettes/simulateur-ecrans.html`. Le simulateur ne redessine rien lui-même : il affiche **le player des écrans en mode aperçu** dans une fenêtre intégrée, réduite (`SimulatorDialog`, `wwwroot/sim.js`). Fonction de base, sans option dans Réglages.

- **Simuler un écran** : bouton « Simuler » sur la vignette de la carte (au survol, toujours visible au doigt), menu ⋮ de la carte et fiche de l'écran. On voit la **dernière publication** de l'écran (format, découpage, widgets, apps). Écran d'une zone synchronisée : badge **En direct · comme l'écran** (même contenu, au même moment) ; pause, précédent / suivant ou un clic sur la frise passent en **lecture libre** (l'écran, lui, continue), « Revenir au direct » réaligne. Modifications non publiées : un avertissement le signale.
- **Simuler un mur** : depuis le mur (page Écrans). Le player dessine **tout le mur en une seule image** (`wall.whole` : pas de recadrage sur une place), les cadres et numéros des écrans sont tracés par-dessus (`sim-grid`) : léger même pour 16 écrans. Options : *Image du mur* / *Écrans séparés*, cadres aucun / fins / épais (simple affichage, pas de compensation des bordures), numéros, *Montrer l'état réel* (écrans hors ligne, places libres hachurés). La publication et la grille sont celles du premier écran placé.
- **Réglages du mur** (onglet du simulateur du mur, à côté de *Affichage* ; maquette `maquettes/mur-reglages-simulateur.html`) : orientation, puis matériel et résolution (« Réglages avancés »), avec le même formulaire que la fiche d'un écran (`ScreenFormat`, sans découpage). Un réglage changé ne modifie que le simulateur (jeton d'aperçu au format essayé, bandeau « pas encore appliqués ») ; **Appliquer aux N écrans** enregistre les mêmes réglages sur tous les écrans du mur et les prévient (`Notifier.NotifyScreen`) : ils se rechargent, sans republier. Un seul réglage pour tout le mur ; si ses écrans diffèrent, un avertissement liste les écarts et « Appliquer » les aligne. Réservé à qui gère les zones de l'aire (lecture seule sinon) ; fermer sans appliquer demande confirmation. Le simulateur d'un écran seul n'a pas de réglages (ils sont dans sa fiche).
- **Essayer une liste** (« Essayer sur… », éditeur de la liste et menu ⋮ des cartes de listes) : joue la liste **dans son état actuel, même non publiée** (`Notifier.Items`, l'instantané qu'une publication figerait), sur un format au choix (paysage, portrait, mur 2 × 2 ou 3 × 1, ou comme un écran ou un mur existant) et, sur option, à une **date et heure choisies** (heure de l'organisation) : les contenus hors de leur période de validité sont sautés et listés sous la frise. Rien n'est envoyé aux écrans.
- **Jeton d'aperçu** (`PreviewGrants`, singleton) : créé par le back-office pour une personne qui a accès à l'écran, au mur ou à la liste (`TenantStore`), désigne exactement ce qu'il montre (`PreviewGrant` : écran, mur ou liste, format), gardé **en mémoire**, valable 1 h après sa dernière utilisation. Après un redémarrage du serveur, le simulateur affiche « Aperçu expiré » : il suffit de le rouvrir. Les médias sont servis par la session du back-office (`/media` accepte déjà un utilisateur de l'organisation).
- **Mode aperçu du player** (`/player/?preview=jeton`, `&at=ms` date simulée, `&sound=1`) : mémoire en RAM au lieu du `localStorage` (aucun conflit avec un écran appairé sur le même poste), ni service worker, ni cache des médias, ni accusé de réception, ni diffusions comptées, ni connexion au hub (aucun ordre reçu : désappairer, recharger, identifier) ; l'écran n'est pas marqué « vu ». Les publications sont reprises en relisant toutes les 20 s. Date simulée : `Date` est décalé dans la page du player (horloges, « aujourd'hui », périodes de validité) ; les données des apps (agendas…) restent celles du moment réel.
- **Échanges** (`postMessage`, même origine) : le player envoie son état (contenus, contenu affiché, temps restant, direct / libre / pause, contenus sautés) ; le simulateur envoie `pause`, `play`, `goto`, `next`, `prev`, `live`. La pause arrête l'enchaînement et les vidéos du contenu affiché ; les diaporamas internes d'une app et YouTube continuent.
- **Son** : coupé à l'ouverture (`sound` des contenus forcé à faux) ; l'activer recharge le player (en direct, il reprend au même moment).
- **Code partagé** : `PlayerFeed` (`Preview.cs`) construit la réponse de `/api/player/playlist` pour un écran (`ForScreen`) et pour une liste essayée (`ForPlaylist`) ; `PlayerFeed.AllItems` sert aussi `/api/data/{id}`.

## 19. Application Android du player (9 octobre 2026)

Maquette validée : `maquettes/player-android.html`. Projet `Linkii.AndroidPlayer/` (Kotlin, **aucune dépendance**, pas d'AndroidX) : une coquille native autour du player web. Le player garde l'appairage par code, la diffusion et le cache hors ligne (service worker + Cache Storage, §7) ; l'application ajoute ce que le navigateur ne peut pas faire.

- **Lancement** : démarrage au boot et après une mise à jour (`BootReceiver`, nécessite l'autorisation « par-dessus les autres applications » sur Android 10+ ; menu technicien › *Autoriser le démarrage automatique*), ou **écran d'accueil de l'appareil** (filtre `HOME`, recommandé sur un boîtier). Plein écran immersif permanent, écran jamais en veille, bouton Retour sans effet. Verrouillage kiosque (`startLockTask`) seulement si l'appareil est géré (MDM) : sinon Android demanderait une confirmation à chaque lancement.
- **Appairage** : celui du player (code à 6 chiffres affiché, saisi dans *Écrans › Connecter un écran*) ; rien à développer côté back-office. L'agent utilisateur du WebView se termine par `LinkiiPlayer/<version> Android`.
- **Pannes** : serveur injoignable au lancement (ni réseau ni cache) → page de secours (`assets/offline.html`) puis nouvel essai toutes les 10 s ; processus de rendu tué par Android → la vue est recréée et le player rechargé.
- **Menu technicien** : 5 appuis sur OK (télécommande) ou 5 touchers dans le coin supérieur droit, en moins de 3 s. Code PIN (**9999 par défaut**, local à l'appareil) puis diagnostic (serveur, version, réseau, stockage libre) et actions : reprendre, recharger le player, changer de serveur, définir le PIN, autoriser le démarrage, désappairer (efface `lk_token`, `lk_screen`, `lk_playlist`, `lk_acked`, `lk_brand`, comme `unpair()` du player), quitter.
- **Serveur** : `https://linkii.duckdns.org` par défaut (`-PserverUrl=…` à la compilation, modifiable dans le menu). **HTTPS obligatoire** (sans lui, pas de service worker donc pas de hors ligne) ; seuls `localhost` et `10.0.2.2` sont admis en HTTP (`network_security.xml`).
- **Compiler** : `Linkii.AndroidPlayer/gradlew :app:assembleDebug` (APK) ou `:app:bundleRelease` (`.aab` pour Google Play, signature par `LINKII_KEYSTORE*` dans `~/.gradle/gradle.properties`). AGP 9.4.1, Gradle 9.7.1, JDK 17+ (celui d'Android Studio convient), SDK 35 ; minSdk 24. Détails et publication Play Store : `Linkii.AndroidPlayer/README.md`.
- **Vérifié sur l'émulateur Android TV** (image `android-36;android-tv`) : installation, affichage du code d'appairage du serveur, appairage, diffusion, menu technicien (PIN 9999), redémarrage de l'application avec la diffusion reprise depuis le cache. Les vidéos y saccadent (décodeur logiciel de l'émulateur, 1,5 Go de RAM d'origine) : **la fluidité reste à confirmer sur un vrai boîtier**. Le test « hors ligne » n'est pas concluant (réseau de l'émulateur pas réellement coupé, `ping` bloqué).
- **Limites connues** : cache hors ligne = celui du WebView (quota géré par Android, plan B : téléchargement natif des médias) ; PIN non piloté depuis le back-office ; l'indicateur « Réseau » du menu ne vérifie pas l'accès à Internet ; icône et bannière TV provisoires.

## 20. Vidéos limitées à 1080p et format affiché (10 octobre 2026)

- **Limite à l'import** (téléversement, adresse web, **Drives**, Canva : tout passe par `MediaImporter`) : une vidéo plus grande que **1920×1080** (paysage) ou **1080×1920** (portrait), par exemple en 2K ou 4K, est convertie en MP4 H.264 1080p. Un MP4 déjà dans la limite est publié tel quel, sans réencodage. Sans convertisseur, un MP4 trop grand est publié tel quel plutôt que refusé. Les vidéos déjà importées ne sont pas converties (les réimporter).
- **Taille lue sans outil** (`Mp4Probe`, piste vidéo de la boîte `tkhd`, rotation de 90° comprise ; `MediaProbe` pour les images : PNG, GIF, JPEG, WEBP, SVG).
- **ffmpeg** : filtre `scale` qui tient dans la boîte 1920×1080 / 1080×1920 sans jamais agrandir, dimensions paires, sortie `-f mp4`. **VLC** n'a pas de « tenir dans une boîte » : le facteur de réduction est calculé (`scale=`) à partir de la taille lue ; taille inconnue → `maxwidth=1920,maxheight=1080` (paysage supposé).
- **Détection de VLC sous Windows** : `vlc.exe` n'écrit rien pour `--version` (il dépose un `vlc-help.txt` dans le dossier courant) ; la version est lue dans les propriétés du fichier. Sans cela, VLC n'était pas détecté sur Windows.
- **Serveur Linux** : installer **ffmpeg** (`apt install -y ffmpeg` sur Debian/Ubuntu), puis redémarrer Linkii (la détection est faite une fois). Au besoin `LINKII_FFMPEG=/usr/bin/ffmpeg` dans l'unité systemd. Le dépôt `packages.microsoft.com` non signé fait échouer `apt update` sans empêcher l'installation depuis Debian.
- **Format dans la médiathèque** (`MediaItem.Width` / `Height`, `MediaProbe.Label`) : à côté de la taille en octets, « **1080p** » pour une vidéo (côté court ; « 4K » à partir de 2160), « **1920×1080** » pour une image ; info-bulle avec les pixels exacts. Affiché sur les cartes de `/media` et dans le sélecteur de médias. Les fichiers importés avant sont lus à la première ouverture.
- **Vérifié** : conversion réelle d'un MP4 2560×1440 en 1920×1080 (VLC, environ 40 s) ; tests `VideoLimitTests` et `MediaProbeTests`.

## 21. Indicateur d'import d'un dossier Drive (10 octobre 2026)

L'import d'un dossier Drive (surtout avec des vidéos) tourne sur le serveur sans rien montrer : un bandeau d'avancement (`DriveSyncBar`) le rend visible, pour **toute synchronisation** (ajout d'un dossier, sous-dossiers, *Synchroniser maintenant*, passage automatique toutes les 15 minutes).

- **Phases** (`DriveService.SyncProgress`) : *Lecture du dossier* (barre indéterminée), *Import* (« 12 sur 45 fichiers », barre à l'octet près, fichier en cours), *Mise à jour des écrans*. Si le dossier contient des vidéos, une ligne prévient que téléchargement puis conversion en 1080p peuvent durer plusieurs minutes.
- **Où** : sous le titre de `/media` (la page se rafraîchit seule pendant l'import et relit la liste à la fin) et dans la fenêtre *Intégrations › Drives › Gérer*. Visible de toute personne du client qui ouvre la page, même pour une synchronisation automatique.
- **Technique** : avancement **en mémoire** (`DriveService.Running(clientId)`, événement `ProgressChanged` limité à ~4 par seconde) ; `MediaImporter.ImportStream` rend les octets reçus. Perdu si le serveur redémarre en cours de synchronisation (reprise à la suivante). La conversion vidéo qui suit reste signalée par la carte « Conversion… » de la médiathèque.
- **Non vérifié contre un vrai Drive** (bandeau jamais vu à l'écran) ; tests `DriveProgressTests` sur le calcul d'avancement.

## 22. Licences Base et Growth (10 octobre 2026)

- **Deux licences par organisation** (`Client.Plan`, classe `Plans`) : **Base** (affichage : écrans, médiathèque, apps, aires, zones) et **Growth** (en plus : contrôle du direct, supervision, murs d'écrans). Réglée par l'administrateur Linkii dans *Admin › Clients*. Les fonctions Growth restent visibles mais verrouillées (« Disponible avec la licence Growth ») en Base.
- Note : le mur d'écrans (§ Zones) est réservé à Growth ; vérifier ce périmètre avant toute vente.

## 23. Contrôle du direct et supervision (Growth, 10 octobre 2026)

- **Commandes vers l'écran** (`Control.cs`, `ScreenControl`, `ScreenCommand`) : *Pause* (durée réglable ou reprise manuelle, `PauseResumeMinutes`), *Reprise*, *Redémarrer l'application*, *Recharger les listes*, *Aperçu du direct*. Envoi par SignalR (`ScreenHub`) avec secours dans la réponse du sondage du player ; accusés `sent → received → done/failed` (`POST /api/player/command-ack`) ; sans accusé après 2 min : échec. Refusée si l'écran est hors ligne, si la licence n'est pas Growth, ou si une commande identique est en cours. Les 20 dernières commandes sont gardées par écran.
- **Aperçu du direct** : le player (web et Android, `Capture.kt`) envoie une capture (`POST /api/player/capture`, fichier `captures/{id}.jpg`, servi par `/screens/{id}/capture.jpg`). Un contenu protégé (certaines vidéos) peut apparaître noir. L'aperçu est remis dans le sens configuré (rotation, miroir). Pour un mur : `WallLivePanel` montre chaque écran à sa place, sans les miroirs.
- **Aperçu depuis un navigateur** (`webCapture` dans `player.js`) : le player dessine les images et vidéos affichées dans un canvas (portion du mur pour un écran de mur), les envoie en `POST /api/player/capture?plain=true` (booléen : `plain=1` est refusé en 400 ; sans miroir à remettre à l'endroit). Sont absents de l'image : widgets, YouTube, pages web intégrées. Les échecs sont détaillés dans l'historique des commandes (dessin impossible, envoi refusé HTTP xxx, image vide). Non vérifié dans un vrai navigateur.
- **Murs** : chaque écran du mur affiche son logo d'appareil et son système (puce de la carte du mur), globe pour un navigateur.
- **Panneaux** : `LivePanel` (écran) et `WallLivePanel` (mur) dans l'éditeur ; `DeviceLogo` affiche le type d'appareil.
- **Pause** : visuel de pause = image de la médiathèque (`PauseMediaId`) ou visuel Linkii ; tous les écrans d'un mur se mettent en pause ensemble.
- **Alerte hors ligne** (`AlertOfflineMinutes`, `AlertEmail`, `Mailer.cs`) : une alerte par panne quand un écran est hors ligne depuis N minutes, par e-mail et/ou notification selon Réglages › Alertes et pause (voir §64).
- **Appareil remonté par le player** : `DeviceKind` (androidtv / android / web), système, modèle, version de l'application, dernier démarrage.
- **Miroir** (écran et mur : `MirrorH`, `MirrorV`) : pour un écran vu dans un miroir ; combiné par OU exclusif écran/mur ; ne demande aucune publication mais recharge les écrans (`SyncStamp`). Maquette : `maquettes/direct-ecrans.html`.
- **Médiathèque** : miniatures WEBP 320 px générées dans `Linkii.Poc/thumbs/` (données d'exécution, ignorées par git).
- Tests : `Linkii.Poc.Tests/ControlTests.cs`. Non vérifié sur un parc réel d'appareils.

## 24. Reconnexion automatique du back-office (10 octobre 2026)

Après un arrêt/redémarrage du serveur, le circuit Blazor est perdu et la page restait bloquée sur « Could not reconnect to the server ». `wwwroot/reconnect.js` (Blazor démarré avec `autostart="false"` dans `App.razor`) remplace l'écran par défaut : **écran bloquant** (voile bleu nuit flouté sur toute la page, inert sur le reste de l'application, carte centrée « Connexion perdue / Reconnexion en cours… / Merci de ne pas fermer cette page »), sondage du serveur toutes les 2 s (sans limite), puis « Serveur retrouvé » et **rechargement automatique** dès qu'il répond, ou au retour du réseau / de l'onglet. Maquette : maquettes/reconnexion.html. Non vérifié par un vrai redémarrage.

## 25. Refonte des cartes et de la liste des écrans (10 octobre 2026)

Page *Écrans* (`Components/Pages/Screens.razor`, styles en fin de `wwwroot/app.css`). Maquettes validées en conversation (aperçu en en-tête, orientations, murs, liste PC).

- **Classe `scr-card`** : réservée aux cartes d'écrans et de murs. Les cartes de listes de lecture partagent `sc-card` : ne jamais styler la refonte avec `.sc-card` (cela cassait la page *Listes de lecture*).
- **Vignettes (variante A)** : l'aperçu est l'en-tête, une *scène* sombre à hauteur fixe (`--stage-h`, 128 px ; 150 px en une colonne ; 260/200 px pour un mur ouvert) où l'écran ou le mur s'ajuste à ses vraies proportions (paysage ou portrait, `--ar` et `--rows` pour un mur). Écran indépendant : la ligne du titre (case, point d'état, nom, menu ⋯) reste au-dessus de la scène ; dessous, une ligne *appareil · zone · synchro* (`.sc-meta`), le sélecteur « En diffusion » pleine largeur, puis **Aperçu** et **Pause/Reprendre**. **Redémarrer** passe dans le menu ⋯ (grisé hors ligne ou sans licence Growth). Hors ligne : aperçu grisé.
- **Mur en vignettes** : case, badge d'état (« n sur m en ligne », jaune si incomplet) et menu ⋯ posés sur la scène ; écrans repliés en une rangée de pastilles d'état, dépliable (`expandedWalls`) ; « Redémarrer les écrans » dans le menu, le bouton « Publier le mur » de la carte est retiré (déjà au menu). Un mur de moins de 4 colonnes occupe une colonne de la grille.
- **Liste PC** (≥ 641 px) : grille de colonnes alignées avec en-têtes (`.sc-listhead`) : case, aperçu, chevron du mur, écran, état (pastille + texte), en diffusion, actions en icônes, menu. Un mur est une ligne de groupe ; ses écrans se déplient en lignes sur les mêmes colonnes (sous-grille, `.wc-kids`), avec case et nom cliquable (pas de menu par écran).
- **Mobile** : cartes uniquement ; les règles sont dupliquées pour une vue « liste » mémorisée depuis un PC.
- Non vérifié visuellement dans le navigateur (données réelles) ; à relire après redémarrage de l'application.

## 26. Drives Nextcloud, SFTP et Dropbox (11 octobre 2026)

Trois sources de plus dans Intégrations › Drives, avec le même sélecteur de dossiers, la même synchronisation (toutes les 15 min, copie dans la médiathèque) et l'app « Dossier Drive ». Vérifié dans le navigateur (formulaires, erreurs, Comptes connectés, déconnexion) et par 11 tests (`DriveRemotesTests`, `DropboxAuthTests`) ; **jamais essayé contre un vrai serveur** (voir « À valider »).

- **Couche commune** (`Apps/DriveRemotes.cs`) : `RemoteSession` (lire un dossier, ouvrir un fichier, résoudre un chemin, tester, chercher) avec une implémentation par source ; `RemoteDrives.Open(tenant, source)` ouvre la session. `DriveService` l'appelle dès que `DriveSources.IsRemote(source)` ; Google et Microsoft gardent leur code. Une session sert à une opération (parcours, import d'un dossier) : une seule connexion pour tous les fichiers. Le type d'un fichier vient de son extension (`DriveMime`).
- **Comptes** : `Tenant.DriveAccounts` (`DriveAccount` : Url, Port, User, Secret chiffré, HostKey, LastError, LostUtc). Ils apparaissent dans Comptes connectés (`ConnectedAccounts.Of`, types `nextcloud`, `sftp`, `dropbox`) ; déconnecter les efface (Dropbox : révocation chez Dropbox).
- **Nextcloud** (`NextcloudSession`) : WebDAV sur `{serveur}/remote.php/webdav/` (les fichiers de l'utilisateur authentifié, quel que soit son identifiant interne), `PROPFIND` profondeur 1. Authentification : identifiant + **mot de passe d'application**. HTTPS par défaut ; l'interrupteur « Autoriser HTTP » (`DriveAccount.AllowHttp`, par compte, avec avertissement) accepte une adresse http://, refusée sinon à l'ouverture de la session. Dossier de départ = racine ; on peut saisir un chemin ou coller l'adresse `…/apps/files/?dir=/Affichage`. Pas de recherche (on parcourt). Version d'un fichier = ETag.
- **SFTP** (`SftpSession`, paquet SSH.NET) : mot de passe, clé privée (PEM, avec phrase secrète) ou les deux. **Empreinte du serveur** (SHA-256) retenue à la première connexion puis exigée ; une empreinte différente bloque la connexion avec un message explicite, et « Oublier l'empreinte » la réinitialise (elle l'est aussi si serveur, port ou identifiant changent). Les liens symboliques sont suivis. Dossier de départ = répertoire de connexion ; pas de recherche.
- **Dropbox** (`DropboxAuth` + `DropboxSession`) : « Se connecter avec Dropbox » (OAuth 2 + PKCE, `token_access_type=offline`, un compte par organisation, lecture seule : `files.metadata.read`, `files.content.read`, `account_info.read`). Application Dropbox de la plateforme : `Linkii:Dropbox:AppKey`, `Linkii:Dropbox:AppSecret`, `Linkii:Dropbox:RedirectUri` (défaut : adresse du back-office + `/dropbox/callback`, à déclarer dans la console Dropbox). Routes `/dropbox/connect` et `/dropbox/callback`. Dossiers repérés par leur identifiant (`id:…`, ils survivent à un renommage) ; téléchargement par lien temporaire (`get_temporary_link`) ; recherche de dossier par nom (`search_v2`). Le bouton n'apparaît que si la clé et le secret sont renseignés (sinon « Indisponible »).
- **Réseau** : comme les autres appels sortants, le serveur Nextcloud et le serveur SFTP **ne peuvent pas être sur le réseau interne** du serveur Linkii (`SafeHttp.IsPublic`, vérifié aussi pour SFTP avant la connexion, à l'adresse résolue). Un Nextcloud ou un SFTP d'établissement doit donc être joignable depuis internet.
- **Interface** : `DriveAccountForm` (connexion Nextcloud / SFTP : « Enregistrer et tester » ; secrets jamais réaffichés, champ vide = inchangé ; la source est activée si le test réussit) dans `DriveSourceDialog` ; le sélecteur masque la recherche pour Nextcloud et SFTP ; le champ « ou saisir un chemin » remplace le lien de partage.
- **À valider** : un vrai Nextcloud, un vrai serveur SFTP (clé + mot de passe, changement d'empreinte) et une application Dropbox (création sur dropbox.com/developers, permissions ci-dessus, adresse de retour) ; gros dossiers (limites 300 fichiers, 200 sous-dossiers). Pas de partage par lien public Nextcloud / Dropbox (compte requis).

## 27. Suivi de publication, YouTube sans overlays, interrupteurs (11-12 octobre 2026)

- **Suivi de publication** (`Helpers.Delivery`, carte de l'écran dans Écrans, `.sc-delivery`) : une pastille dit où en est la dernière publication d'un écran — *Affichée à HH:MM* / *À jour* (vert), *Envoi en cours*, *Téléchargement en cours*, *Reçue, bientôt affichée* (orange), *Pas encore reçue* (écran hors ligne) ou *Reçue ? Non confirmée* (en ligne, rien confirmé depuis plus de 10 min ; rouge). Le player envoie des accusés par étape (`POST /api/player/ack` avec `stage` = `downloading` puis `ready`, avant l'accusé d'affichage habituel) ; ils sont rangés dans `Screen.DeliveryRevision`, `DeliveryStage`, `DeliveryUtc`, et `AppliedUtc` retient l'heure d'affichage. Testé par `DeliveryTests`.
- **YouTube › Masquer les overlays** (réglage `hideOverlays` de l'app, désactivé par défaut) : le lecteur est recadré (≈ 9 % en haut et en bas hors champ, sans interaction) et un voile noir couvre le titre du début, la pause, le chargement et les suggestions de fin ; il se lève 1,5 s après le début de la lecture (4,5 s si l'API YouTube n'est pas disponible). L'image est légèrement recadrée.
- **Interrupteurs** : les cases à cocher des formulaires utilisent la classe `ios` (interrupteur Linkii) : réglages d'app (`AppForm`), SSL/TLS (SMTP de la plateforme), « Propulsé par Linkii » (marque du revendeur), simulateur, membres, « Autoriser HTTP » de la connexion Nextcloud (§26). Seules les cases de sélection des tableaux (Écrans, Médiathèque, Listes de lecture) restent des cases.

## 28. Vignettes des vidéos de la médiathèque (12 octobre 2026)

- **Problème** : chaque vidéo des listes (médiathèque, sélecteurs, éditeur de liste, vignettes d'écrans) était un `<video preload="metadata">` : un décodeur et un téléchargement par vidéo, d'où un défilement saccadé.
- **Correction** : `MediaThumb` affiche une **image** pour les vidéos quand ffmpeg est installé sur le serveur (`VideoConverter.CanPoster`). `/media/{fichier}?w=320` renvoie alors une vignette JPEG (`Thumbnails.GetVideo`, `VideoConverter.Poster` : image à 1 s, sinon la première, 160 / 320 / 640 px), créée à la première demande, gardée dans `thumbs/` et supprimée avec la vidéo. Au plus 2 extractions ffmpeg en parallèle ; `loading="lazy"`. Sans ffmpeg (VLC ne sait pas extraire une image) : ancien comportement, un lecteur par vidéo.
- **À valider** : l'extraction n'a pas pu être essayée (pas de ffmpeg sur le poste de développement) ; à vérifier sur le serveur après déploiement. Une vidéo dont l'extraction échoue renvoie 404 (vignette vide).

## 29. Intégration PDF : app « Document PDF » (13 octobre 2026)

Maquette validée : `maquettes/pdf.html` (états : PDF prêt, aucun PDF, conversion, trop long, refusé ; options avancées). Codée le 13 octobre ; **non vérifiée visuellement dans le navigateur** (login requis) ni sur le serveur de production.

- **Principe** : le PDF est **converti en images sur le serveur** (une page = une image JPEG 1080p de la médiathèque), puis lu comme un diaporama : durée par page, cadrage, transition, hors ligne, zones 2 et 3, simulateur, tout est celui des images. Pas de pdf.js dans le player (lourd en WebView Android et inutilisable hors ligne). Le player ne change que d'une ligne : `A.register('pdf', …)` dans `wwwroot/player/apps.js` réutilise `slideshow-images`.
- **Moteur** : PDFium via **Docnet.Core 2.6** (paquet NuGet, binaires natifs Windows / Linux / macOS inclus, aucun outil externe à installer). Un seul rendu à la fois (`PdfService.Gate`). SkiaSharp compose le résultat (page sur blanc, fond, JPEG qualité 90).
- **Service** : `Apps/PdfService.cs`. `SaveSource` garde le PDF reçu dans `pdf/` (hors de `media/`, jamais servi ; 100 Mo maximum ; un PDF protégé par mot de passe ou illisible est refusé avec un message) ; `Convert` produit les images (`MediaItem` de type image, `Info["pdf"]` = identifiant du PDF, nom « Titre — page n ») ; `ParseRange` lit « 1-5, 8, 10-12 ». **50 pages au maximum** : au-delà, les 50 premières de la sélection et un message.
- **Rendu** : une page tient dans 1920×1080 (paysage) ou 1080×1920 (portrait) selon son propre sens, sans agrandissement au-delà de 4×. Avec un fond (blanc ou page floutée), l'image est au format de l'écran (paysage ou portrait, réglage « Sens de l'écran »), la page nette posée au centre, le flou étant la page agrandie, floutée et assombrie. **Le fond et les pages choisies sont appliqués à l'import** (décision du 13 octobre : pas de calcul de flou dans le player) : les modifier reconvertit le PDF gardé, à la validation des options avancées (`AppInstanceForm.ApplyAdvanced`).
- **Manifeste** `Apps/pdf.json` (catégorie Médiathèque, durée « content ») : source `pdf` (champ `PdfField`, une ligne : icône PDF, nom, « n pages » ou « 50 pages sur 84 », bouton « Choisir un PDF » / « Remplacer ») ; champs gérés `pdfTitle`, `pdfPages`, `pages` (médias image, `managed`) ; en `main` la durée par page (10 s), le cadrage (Page entière / Remplir l'écran) et la transition ; en `advanced` pages à afficher, fond (Noir / Blanc / Page floutée), sens de l'écran (si fond), vitesse de la transition. Validation : type `pdf` (le PDF doit exister et avoir des pages converties). L'aperçu est le carrousel 3D habituel (`ContentPreview`, champ `pages`).
- **Nettoyage** : une nouvelle conversion du même PDF retire les images précédentes que rien n'utilise ; remplacer le PDF d'un contenu enregistré retire ses anciennes images (comme Canva) et l'ancien fichier ; supprimer le contenu supprime le PDF gardé. `MediaLibrary.UnusedExports(…, "pdf")` généralise `UnusedCanva`. Dans la médiathèque, les pages sont filtrables par la provenance « PDF ». Un PDF choisi puis abandonné sans enregistrer laisse ses images dans la médiathèque (à supprimer à la main), comme un export Canva abandonné.
- **Mise en page de la ligne source** : identique à celle d'une image (`ci-pick`) : cadre blanc avec le nom, bouton « Choisir un PDF » (icône et texte sur une ligne, taille normale) ou « Remplacer » à côté.
- **Écarts avec la maquette** : « Durée de la première page » et « Ordre aléatoire » ne sont pas faits (le player n'a pas de durée par page, et mélanger les pages d'un document n'a pas de sens) ; pas de « Depuis la médiathèque » (un PDF n'est pas un média de la médiathèque) ; l'icône du bandeau est « sliders », comme toutes les apps.
- **Tests** : `PdfServiceTests` (PDF généré à la main : pages, sens, plages, fonds blanc / flou, 50 pages, nettoyage, source absente) ; les classes qui touchent `AppPaths` (statique) sont dans la collection `platform` pour ne plus se marcher dessus. Les 5 `AppsTests` en échec (agenda v1) l'étaient déjà.
- **À valider** : conversion sur le serveur Linux (le binaire `pdfium.so` est inclus, à confirmer après déploiement) ; rendu de vrais PDF (polices, images, PDF de grande taille) ; fenêtre de configuration dans le navigateur.
- **Deuxième temps** : les PDF des dossiers Drive (aujourd'hui ignorés comme « autres fichiers » par `DriveFolderPicker`). Reste à décider s'ils deviennent des pages d'un diaporama (une image par page, synchronisées) ou une app dans la boucle.

## 30. Standard de configuration d'un contenu (11 octobre 2026)

Toute fenêtre de création / modification d'un contenu (`Components/AppInstanceForm.razor`, ouverte depuis la liste de lecture et la page d'édition) suit la **même mise en page**, pilotée par le manifeste de l'app. Maquettes validées : `maquettes/contenus.html` (les 8 types, avec sélecteur) et `maquettes/detail-contenu.html` (YouTube, référence).

- **Bandeau** : icône de l'app, titre (= nom du contenu), **icône « Éditer »** (crayon : petite fenêtre « Renommer » ; le nom n'est plus un champ de la configuration, pour gagner de la place), **icône « Options avancées »** (`sliders`, seulement si l'app a des champs avancés), croix.
- **Ligne 1, source** : ce qu'on va afficher (lien, fichiers de la médiathèque, design Canva, dossier Drive, calendrier). Fond grisé. Pour un lien (RSS, YouTube, page web) : bouton **Rechercher**, qui charge l'aperçu ; pour un calendrier, l'aperçu se charge au choix.
- **Ligne 2, deux colonnes** : à gauche **Aperçu** (`ContentPreview`), à droite **Configuration** (champs « main » seulement).
- **Options avancées** : fenêtre par-dessus (`ci-adv`) avec Annuler (restaure les valeurs à l'ouverture) / Valider. Les apps sans champ avancé n'ont pas l'icône : rien de caché.
- **Aperçu** : une image fixe pour **un seul** élément ; **carrousel 3D** (`Carousel3D`, `wwwroot/carousel3d.js`) **dès qu'il y a plus d'un élément** (diaporamas, pages Canva, fichiers d'un dossier Drive) : élément central de face, voisins inclinés ; flèches, clic, clavier, glisser, défilement horizontal ; bouton « Grille » pour la vue à plat. Apps à fournisseur : rendu des données lues par le serveur (`AppService.Preview` = test + `Fetch`) : miniature YouTube, articles RSS, événements du calendrier, cadre de page web ; sous l'aperçu, le résultat du test (vert / ⚠ orange / rouge). Le bouton « Tester » du pied de fenêtre n'existe plus que pour une app à fournisseur **sans** aperçu (ex. météo : `AppInstanceForm.NeedsTest`).
- **Manifeste** (`AppField`) : `zone` = `source` | `main` (défaut) | `advanced` ; `ui: "segmented"` (select de 2 à 3 choix courts affichés côte à côte, ex. Cadrage) ; `unit` (unité d'un champ number : « secondes », « jours »…). `AppCatalog.Check` refuse une zone inconnue ou un `ui` hors select. `AppManifest.InZone(zone)`, `HasAdvanced`.
- **Règles pour une nouvelle intégration** : un seul champ source (ou un champ média / drive / canva) en zone `source` ; les réglages que presque tout le monde regarde en `main` (nom, durée, transition, cadrage, son) ; tout le reste en `advanced` ; interrupteurs pour les oui/non ; choix courts en `segmented` ; unités dans `unit`, pas dans le libellé. L'aperçu vient de la nature de la source : champs `media` / `medias` / `canva` / `drive` → fichiers (image fixe ou carrousel), fournisseur avec une source → rendu des données ; un nouveau rendu s'ajoute dans `ContentPreview`.
- **Composants** : `AppForm` (paramètre `Zone`, `OnChange` pour rafraîchir l'aperçu), `MediaField` (barre de choix + liste d'ordre repliée), `CanvaField`, `ContentPreview`, `Carousel3D`. Styles `.ci-*`, `.cp-*`, `.car-*`, `.seg`, `.unit` en fin de `app.css`.
- **Ligne source compacte (Canva)** : la ligne 1 doit rester basse (≈ 70 px). Canva (CanvaField) tient sur **une ligne** : design choisi (« titre · n pages exportées ») avec « Mettre à jour » et « Changer » (ou « Choisir un design Canva »), puis le format d'export en deux boutons (« Pages en images | Vidéo ») ; « Connecté : compte » est une pastille sur la ligne du libellé (.cv-chip) ; l'aide « après une modification… » est l'infobulle de « Mettre à jour ». Maquette : `maquettes/canva-compact.html` (qui place « Mettre à jour » sous l'aperçu ; codé dans la barre faute de lien entre le champ et l'aperçu). Règle générale : pas de texte d'aide long ni de bloc à plusieurs lignes dans la zone source ; l'aide passe en infobulle, l'état en pastille.
- **Non vérifié** : le rendu complet dans le navigateur (carrousel avec de vraies images, aperçus RSS / calendrier / YouTube, fenêtre d'options avancées) n'a pas été contrôlé de bout en bout ; à relire au premier lancement. Les 5 tests `AppsTests` qui échouent (agenda v1 : `icsUrl`, `mailbox`) étaient déjà cassés avant ce changement.
## 31. Écrans et murs au même standard (11 octobre 2026)

Les fenêtres *Modifier l'écran* (`ScreenEditor`) et *Assembler / Réorganiser un mur* (`WallDialog`) suivent le standard du §30 : bandeau avec **Renommer** (même bouton que la page *Listes de lecture*) et **icône réglages** (`sliders`, même bouton que le détail d'un contenu), colonne de configuration à droite, rien de technique dans la vue principale. Maquettes validées : `maquettes/ecran-config-epure.html`, `maquettes/mur-config-epure.html`.

- **Écran** : deux onglets, **Écran** (zone ou note de mur, orientation, découpage) et **Widgets** (liste « Sur l'écran » avec régler sliders et retirer, puis les widgets actifs à **glisser sur l'aperçu** ou à cliquer ; recherche au-delà de 6) : le glisser-déposer reste le geste principal, d'où l'onglet plutôt qu'un menu. Sous l'aperçu : une ligne d'aide, le détail clavier est dans l'infobulle « i ». **Options avancées** (icône du bandeau, dvanced dans ScreenEditor) : matériel, résolution (avec la résolution détectée) et miroir horizontal / vertical ; Annuler rétablit les valeurs d'ouverture (type, orientation, résolution, découpage, miroir). ScreenFormat : Compact = orientation + découpage, Compact + AdvancedOnly = matériel + résolution.
- **Mur** (maquette finale : `maquettes/mur-config-compact.html`) : **une seule colonne, sans défilement** (ni vertical ni horizontal). En haut, la **Grille** en boutons et le conseil « Contenus à créer en L × H px » ; l'avertissement « formats différents » seulement s'il s'applique ; puis la **Disposition** : le mur est dessiné à ses vraies proportions, sa largeur étant bornée à la fois par la fenêtre et par une hauteur maximale de 230 px (`--ar` : un 3 × 1 remplit la largeur, un 1 × 3 est une colonne haute et étroite, centrée) ; places à échanger par deux clics, « Identifier sur les écrans » dessous. **Plus de choix de liste de lecture** dans la fenêtre : un mur créé reprend la liste du premier écran sélectionné, et la liste d'un mur existant se change hors de cette fenêtre (page Écrans). Le champ « Nom de la zone » est remplacé par **Renommer** (nom explicite gardé tel quel ; sans renommage, le nom proposé suit la grille) ; le **miroir du mur** est dans les options avancées.
- **Non vérifié** dans le navigateur (pas d'écran appairé dans l'environnement de développement) : à relire au premier lancement : éditeur d'écran (menu Ajouter, glisser vers l'aperçu, options avancées, annulation), assistant de mur (création, réorganisation, grille 1 × 3 sans défilement).
## 32. Cartes écrans et murs à plat, vue cartes (11 octobre 2026)

Nouvelle carte pour la **vue cartes** de la page *Écrans* (`Screens.razor`, branche `view != "list"` en tête de la boucle) ; la **vue liste garde le balisage `scr-card` du §25, inchangé**. Classes `.gc*` en fin de `app.css` (ne pas styler la vue cartes avec `.scr-card`). Écran et mur ont **la même taille**. Maquette validée avec les données réelles (LG TV_LPR, Samsung A57 5G, Mur 3 × 1).

- **Aperçu** (`.gc-stage`, 16:9, dimensions en `cqw`/`cqh`) : case et menu ⋮ en haut ; en bas à gauche la pastille d'état (En ligne, Hors ligne, Mise à jour, En pause avec heure de reprise ; pour un mur « n sur N en ligne » ou « Mur incomplet ») ; en bas à droite le **statut de publication** (Affichée à…, Pas encore reçue, Reçue bientôt affichée ; détail en infobulle) et « À publier ». Écran découpé : une vignette par zone. Un écran hors ligne est grisé.
- **Pied** : nom et logo de l'appareil ; ligne appareil (OS · App version, ou modèle pour un navigateur) ; puces zone et Synchronisé / Indépendant ; **liste de lecture en liste déroulante** (accès rapide, `Assign`), avec Aperçu et Pause en icônes (cadenas en licence Base). Écran à plusieurs zones : un bouton « liste · n zones » ouvre une fenêtre avec une liste par zone (`AssignZone`, `SetZonePlaylist`, diffusée à la prochaine publication).
- **Mur** : pas de liste d'écrans dans la carte ; la puce « ● ● ● n écrans » ouvre une fenêtre (numéro, nom, appareil, position, état ; un clic ouvre les paramètres de l'écran). Menu ⋮ et confirmation « Dissoudre » inchangés.
- **Clics** : aperçu d'un écran → paramètres de l'écran ; **un écran du mur → paramètres de cet écran** ; reste de l'aperçu du mur ou place libre → fenêtre **Réorganiser** (si on gère les zones). **Pas de bouton Simuler au survol** (retiré à la demande) : Simuler reste dans le menu ⋮. Au survol, l'écran (ou la tuile du mur) est mis en surbrillance (contour bleu et éclaircissement).
- **Non vérifié** dans le navigateur : l'application tournait pendant les modifications (seule la compilation a été contrôlée) ; à relire au redémarrage : hauteurs égales écran / mur, fenêtres des écrans du mur et des zones, mur 4 colonnes (n'occupe plus deux colonnes), états de livraison.

## 33. Mise en veille des écrans (11 octobre 2026)

Plages horaires pendant lesquelles les écrans sont éteints (nuit, week-end). **Fonction avancée, désactivée par défaut** : interrupteur *Plages de mise en veille* dans Réglages › Paramètres (`Tenant.SleepEnabled`) ; avec une seule aire, la grille se règle dans la même carte, sinon dans la fenêtre de l'aire (Aires de gestion). Tout est dans `Sleep.cs`.

- **Grille** : 7 jours × 24 heures, une chaîne de 168 caractères « 0 » / « 1 » (lundi 00 h en premier, « 1 » = en veille). On clique ou on glisse pour peindre (`SleepGrid`) ; le bouton « Nuits et week-ends » propose 19 h – 7 h en semaine et le week-end entier (`Sleep.Suggested`).
- **Niveaux** : l'aire porte la grille par défaut de ses écrans et **son fuseau** (`Area.SleepGrid`, `Area.SleepTz`, à défaut le fuseau de l'organisation) ; chaque écran peut la suivre (`inherit`, défaut), ne jamais s'éteindre (`never`) ou avoir la sienne (`own`) : `Screen.SleepMode`, `Screen.SleepGrid`, composant `SleepChoice` dans l'édition de l'écran. **Pas de niveau zone.** Un écran d'un **mur** suit toujours son aire (le mur s'éteint d'un bloc).
- **Édition d'un écran** : un bouton à icône (lune) dans le bandeau de la fenêtre, entre *Simuler* et *Options avancées* (pas d'onglet), visible si la fonction est activée et si l'écran n'est pas dans un mur. Il ouvre une fenêtre (*Suit l'aire*, *Jamais en veille*, *Personnalisée* + grille) ; **Valider** garde le réglage dans la copie de travail, **Annuler** le laisse comme il était, l'enregistrement se fait avec le reste de l'écran. L'icône passe en bleu quand l'écran a un réglage propre, l'infobulle en donne le résumé (`ScreenEditor.razor`, `SleepChoice`).
- **Mettre en veille** : le menu ⋮ propose aussi *Mettre en veille* (*Mettre en veille les écrans* pour un mur) quand la fonction est activée et que l'écran est éveillé. La veille est manuelle (`Screen.ForcedSleep`) : l'écran dort **jusqu'à « Réveiller »**, quelles que soient ses plages ; le player reçoit une plage couvrant toute la semaine (`Sleep.ForPlayer`), la carte affiche « En veille ». *Réveiller* annule cette veille.
- **Réveiller** : le menu ⋮ d'un écran (vue cartes et vue liste) ou d'un mur propose *Réveiller* / *Réveiller les écrans* quand l'écran (ou un écran du mur) est en veille. Le réveil est manuel et dure `Sleep.WakeMinutes` (60 min), puis l'écran reprend ses plages. Il est gardé sur l'écran (`Screen.WakeUntilUtc`), fait partie de l'empreinte de veille (`Sleep.Stamp`) et est envoyé au player (`sleep.wakeUntil`, honoré par `isAsleep`) et dans la réponse de `/api/player/version` (`wakeUntil`). Pas de licence Growth requise, et l'écran peut dormir : **pendant la veille matérielle l'app Android demande elle-même au serveur, chaque minute** (`SleepController.pollWake`, appelé par l'alarme de `SleepReceiver`, jeton de l'écran), donc le réveil arrive en une minute au plus. Non testé sur un appareil.
- **Action groupée** : *Mise en veille…* dans le menu des écrans sélectionnés (`Screens.razor`, `ApplySleep`) ; elle remplace le réglage de chaque écran choisi.
- **Serveur** : `Sleep.ForPlayer` envoie au player `sleep: { tz, ranges }`, des plages en minutes depuis le lundi 00 h (une fin au-delà de 10 080 franchit la fin de semaine) ; `null` s'il n'y a rien à appliquer. `Sleep.Stamp` entre dans la révision de l'écran (`Helpers.SyncStamp`) : changer une plage fait recharger la configuration aussitôt, **sans publier**.
- **Back-office** : la carte d'un écran en veille affiche « En veille · réveil HH:mm » (`Sleep.State`) et « Écran éteint ». La **supervision** ne le compte pas comme une panne : pas d'alerte hors ligne pendant la veille ni dans les 5 minutes qui suivent le réveil (`Control.cs`).
- **Player** (`sleepTick` dans `player.js`, toutes les 15 s) : pendant une plage, écran noir (`#sleepview`), rien n'est lu ni téléchargé ; au réveil, la lecture reprend. Dans l'app Android, le player confie ses plages à l'appareil (`LinkiiNative.sleepSchedule`).
- **App Android** (`SleepController.kt`, version 1.2.0) : l'appareil applique seul les plages, même si le WebView est suspendu ou le réseau coupé. Une alarme exacte (`AlarmManager.setAlarmClock`) est programmée à chaque début et fin de plage ; depuis Android 12 elle demande l'autorisation « alarmes exactes » (`SCHEDULE_EXACT_ALARM`, à accorder dans les réglages de l'appareil) et, à défaut, l'app programme une alarme approximative (`setAndAllowWhileIdle`) : le contrôle toutes les 15 s de l'activité prend le relais tant que le player tourne. Aucune exception de la veille ne doit faire tomber le player (`apply` les attrape). Au démarrage, le player envoie toujours ses plages à l'appareil, même « aucune » (`sleepSig` à `null`), pour effacer d'anciennes plages. Début : écran verrouillé (`DevicePolicyManager.lockNow`) si l'app est **administrateur de l'appareil**, sinon repli « écran noir, luminosité minimale ». Fin : écran rallumé et app ramenée au premier plan. Droit administrateur (`SleepAdmin`, `res/xml/device_admin.xml`) : menu technicien › *Activer la mise en veille de l'écran*, ou `adb shell dpm set-active-admin ch.linkii.player/.SleepAdmin` si l'appareil n'affiche pas la confirmation. Le menu technicien indique si des plages ont été reçues, si l'extinction est réelle ou en repli, et si les alarmes sont exactes ou approximatives.
- **Non vérifié** : jamais testée sur un vrai appareil (extinction réelle, réveil, Android TV). L'extinction réelle dépend du boîtier et du droit administrateur.

## 34. Luminosité et contraste selon l'heure (11 octobre 2026)

Réglage de la luminosité et du contraste des écrans de l'**app Android** selon l'heure (jour, soirée, nuit). Même principe que la mise en veille. **Fonction avancée, désactivée par défaut** (`Tenant.DimEnabled`, interrupteur dans Réglages › Paramètres). Maquette validée : `Linkii.Poc/brand/maquette-luminosite.html`. Code serveur : `Dim.cs`.

- **Modèle** : `DimPlan` = des profils (`DimProfile` : nom, luminosité 10 – 100 %, contraste 50 – 150 %, 100 % = image inchangée) et une grille de 168 chiffres (un par heure de la semaine, indice du profil). Trois profils proposés : Jour 100/100, Soirée 60/110, Nuit 25/120 ; le bouton « Jour, soirée et nuit » peint soirée 17 h – 19 h et nuit 19 h – 7 h. Stocké sur l'aire (`Area.Dimming`, par défaut de ses écrans) et sur l'écran (`Screen.DimMode`, `Screen.Dimming`).
- **Par défaut : « Aucun réglage »** sur chaque écran (`Dim.Never`) ; l'écran peut suivre son aire (`inherit`) ou avoir le sien (`own`), composant `DimChoice`. Un écran d'un mur suit toujours son aire. Un réglage dont tous les profils utilisés valent 100/100 est ignoré (`Dim.Active`).
- **Édition d'un écran** : un bouton à icône (soleil) dans le bandeau de la fenêtre, avant *Options avancées* (pas d'onglet), visible si la fonction est activée et si l'écran n'est pas dans un mur. Il ouvre une fenêtre (*Aucun réglage*, *Suit l'aire*, *Personnalisé* + éditeur) ; **Valider** garde le réglage dans la copie de travail, **Annuler** le laisse comme il était, l'enregistrement se fait avec le reste de l'écran. L'icône passe en bleu quand un réglage est actif (`DimChoice`, `ScreenEditor.razor`).
- **Interface** : `DimEditor` (profils réglables, semaine peinte, courbe de luminosité du jour choisi, aperçu au survol d'une case, encadré des capacités par appareil), `AreaDim` (fenêtre de l'aire et Réglages, avec son interrupteur) ; les heures de veille y sont hachurées et non modifiables. Les heures suivent le fuseau de l'aire (`Area.SleepTz`, à défaut celui de l'organisation).
- **Serveur → player** : `Dim.ForPlayer` envoie `dim: { tz, profiles: [{ b, c }], grid }` (`Preview.cs`, donc aussi dans le simulateur) ; `Dim.Stamp` entre dans la révision de l'écran : un changement est appliqué aussitôt, sans publier.
- **Player** (`dimTick` dans `player.js`, toutes les 15 s) : le **contraste** est toujours un filtre CSS sur la scène (`#stage`). La **luminosité** passe par `LinkiiNative.setBrightness(%)` dans l'app Android : réelle sur **smartphone et tablette** (luminosité de la fenêtre, `MainActivity.kt`), l'appel renvoie `true` ; sur **Android TV** il renvoie `false` (le rétroéclairage dépend du téléviseur) et le player assombrit par filtre. Navigateur et simulateur : filtre seulement. 100 % rend la luminosité à l'appareil ; le repli de la veille la rétablit au réveil.
- **Appareil sans réglage de rétroéclairage** : `MainActivity.isTv()` considère comme téléviseur ou boîtier un appareil en mode « télévision », avec l'interface Leanback ou sans écran tactile ; `setBrightness` renvoie alors `false` et le player assombrit par filtre. Sans cette détection, un boîtier Android TV pris pour une tablette réglait une luminosité de fenêtre que le téléviseur ignore et le filtre n'était pas appliqué : rien ne changeait.
- **Réglage de départ** : à l'activation, `Dim.Suggested()` peint déjà le soir (17 h – 19 h) et la nuit (19 h – 7 h) ; toute la semaine en « Jour » (100 %/100 %) ne change rien (`Dim.Active`) et donnait l'impression que la fonction ne marchait pas. Pour qu'un écran applique le réglage, il doit être sur *Suit l'aire* ou *Personnalisé* (le défaut *Aucun réglage* ne fait rien) et l'aire enregistrée avec son interrupteur actif.
- **Non fait** : action groupée sur plusieurs écrans, remontée au back-office de ce que l'appareil sait faire (l'encadré est un texte fixe), ajout ou suppression de profils (trois fixes), luminosité réelle d'un téléviseur (SDK constructeur).
- **Non vérifié** : le projet web compile ; l'interface n'a pas été essayée dans le navigateur, ni le code Kotlin compilé ou essayé sur un appareil.

## 35. Médias hors ligne : zones, anneau à la publication, diagnostic (11 octobre 2026)

Constat sur une Android TV (écran avec images et vidéos) : tout marchait en ligne ; wifi coupé, les images passaient mais pas les vidéos, et le menu technicien n'indiquait que « 6/6 en local, 7 Mo » (les images). Trois corrections, dans `player.js` (servi par le serveur) et dans l'app Android (`MediaStore.kt`, `MainActivity.kt`, à réinstaller).

- **Cause : les zones 2 et 3 n'étaient jamais préchargées.** Le player ne donnait à l'app que `pl.items` (première zone) pour la médiathèque locale. Les contenus des zones 2 et 3 d'un écran découpé (`pl.zones`, publiés par `Notifier.PublishScreen`) restaient en ligne. Désormais `allItems(pl)` rassemble `items` et toutes les zones ; `mediaUrls` en tire les adresses `url` et `urls` (diaporamas, Canva, Drive).
- **Anneau dès l'arrivée d'une publication.** L'anneau (`DownloadBadge`) ne s'affichait qu'à l'arrivée du premier octet (`MediaStore.visible`), donc jamais pendant la récupération de la liste, ni quand tous les fichiers étaient déjà là. Le player appelle maintenant `LinkiiNative.working(true)` dès qu'une nouvelle version est détectée (en même temps que l'étape « downloading » envoyée au back-office) et `working(false)` quand elle est prête. Tant que `working` est vrai, l'anneau tourne (mode indéterminé, puis se remplit avec les octets) ; coche et disparition à la fin. Absent d'une ancienne app : l'appel est ignoré (`typeof … === 'function'`).
- **Attente bornée.** `prefetchNative` attend la fin du téléchargement avant de basculer sur la nouvelle liste. Si plus aucun octet n'arrive pendant **90 s** (fichier en échec, espace plein), la nouvelle liste s'applique quand même et l'app réessaie en arrière-plan (toutes les 30 s) : l'anneau ne tourne pas sans fin et la publication n'est pas bloquée. Le suivi lit `status().progress` (octets sur le disque, fichiers `.part` compris, `MediaStore.diskBytes()`) : `bytes` seul ne bougerait pas pendant un gros fichier.
- **Menu technicien plus précis.** Sous « Médias : x/y en local (N Mo) » : « dont images : a/b, vidéos : c/d (N Mo) » (`Snapshot.videosReady`, `videosTotal`, `videoBytes`, extensions mp4 m4v webm mov ogv) et, tant que tout n'est pas en local, « dernier échec : … » (`Snapshot.lastError` : code HTTP et nom du fichier, espace insuffisant (réserve de 200 Mo libres), ou type d'exception réseau). Une vidéo qui n'arrive pas se voit donc au lieu d'échouer en silence.
- **Conversion des vidéos : inchangé.** Une vidéo encore en conversion (`MediaItem.Status != null`) n'est volontairement pas publiée (`Notifier.Items` et `Snapshot`) ; la médiathèque l'indique (« Conversion en MP4… »). Les vidéos du cas ci-dessus étaient prêtes, la conversion n'était pas en cause.
- **Pour reprendre ces corrections** : le player est servi par le serveur (les écrans se rechargent quand le code change) ; l'APK doit être recompilé et réinstallé (`Linkii.AndroidPlayer/README.md`, « Mettre à jour l'app sur un appareil »). Sur une ancienne app, seule la correction des zones agit.
- **Vérifié** : `player.js` passe `node --check` ; l'app compile (`:app:assembleDebug`) et s'installe sur l'Android TV (ADB). **Non vérifié** : le comportement sur l'appareil (téléchargement des vidéos de la zone 2, hors ligne, anneau à la publication, lignes du menu technicien) : à contrôler après republication. Pas de test automatisé du pont Android.
- **Démarrage avec serveur lent ou injoignable** (`player/sw.js`, `NET_WAIT` = 4 s) : la page et les scripts du player restent « réseau d'abord », mais si le réseau ne répond pas en 4 s (ou échoue), le service worker sert la copie locale ; sans copie, il attend le réseau. Vérifié : serveur coupé, `/player/player.js` servi depuis le cache en 10 ms. Non vérifié : le cas « serveur lent » et le comportement sur l'Android TV.

## 36. Refonte épurée de l'éditeur d'écran et des listes de lecture (11 octobre 2026)

Visuels validés avant le code (voir la mémoire « maquette avant code »). Aucun changement de modèle ni de serveur : seulement l'interface.

- **Icônes du bandeau de l'éditeur d'écran** (`ScreenEditor.razor`) : le soleil (luminosité) et la lune (mise en veille) reflètent l'état **réel**. Actif : icône bleue ; inactif : icône barrée (`sun-off`, `moon-off` dans `Ico.razor`) de la couleur des options avancées. Actif = un réglage s'applique à l'écran, le sien ou celui de son aire (`DimActive`, `areaDimActive`, `DimAsleep != null`). Avant, la couleur ne dépendait que du mode choisi : un écran qui suivait une aire réglée n'était pas bleu.
- **Onglets Écran et Widgets** : onglets soulignés avec icône (`.se-utabs`), compteur de widgets conservé ; l'icône de Portrait est tournée de 90°. La phrase d'aide sous le découpage (`LayoutHint`) n'apparaît plus dans l'éditeur (elle reste dans l'ancien formulaire).
- **Onglet Widgets : une seule liste** (`.wg-list`). D'abord les widgets déjà sur l'écran (fond grisé, emplacement « Haut gauche · Zone 1 » calculé par `Where()`, boutons régler et retirer), puis les widgets disponibles avec un bouton « + » (glissables sur l'aperçu). Un widget déjà posé quitte la partie « + » : on ne pose plus deux fois le même depuis cet onglet. La phrase d'aide sous l'aperçu et l'encadré d'aide ont été retirés (l'astuce clavier n'est donc plus affichée).
- **Bouton Simuler** : sorti du bandeau, posé en transparence en haut à droite de l'aperçu (`.wg-sim`, dans `.wg-stage`).
- **Page d'une liste de lecture** (`PlaylistEditor.razor`) : le titre prend un crayon de renommage, la ligne de résumé indique « N contenus · boucle de m:ss », et « Essayer sur… » et « Ajouter un contenu » passent dans l'en-tête de la page. Plus de numéros : la poignée de déplacement est toujours visible. Glisser-déposer, « Insérer ici » au survol, menu ⋮ et lien vers les paramètres sont inchangés.
- **Fenêtre « Ajouter un contenu »** : grille de tuiles centrées (icône + nom), la description passe en infobulle ; recherche, pastilles de catégories et « Inséré en position N » sont conservés.
- **Non vérifié** : le projet compile ; l'interface n'a pas été essayée dans le navigateur. Les maquettes proposaient aussi une frise de la boucle, des descriptions et un fond en carte unique : **non retenus**.

## 37. Planning de diffusion (11 octobre 2026)

Quelle liste de lecture passe, quand, sur une zone ou un écran. Maquette validée : `maquettes/planning-v2.html` (la v1 `planning.html` est dépassée). **Facultatif et de base** : « Planning » est dans le menu entre « Listes de lecture » et « Médiathèque », sans réglage à activer ; sans créneau, un écran joue sa liste en permanence, comme avant.

- **Modèle** (`Planning.cs`) : `Slot` = liste de lecture + jours (masque : lundi = 1 … dimanche = 64) + début/fin en minutes (fin jusqu'à 1 440 = minuit, pas de créneau qui passe minuit) + période de dates facultative (`DateFrom` / `DateTo`, inclus). Rattaché à une **zone** (`Zone.Slots`, un mur compris) ou à un **écran** (`Screen.OwnPlanning` + `Screen.Slots`). Un écran suit le planning de sa zone tant qu'on ne lui en crée pas un propre ; un écran d'un mur n'en a jamais.
- **Priorité** (`Planning.Effective`) : créneaux propres de l'écran, puis ceux de sa zone, puis la liste par défaut (celle de l'écran ou du mur, **réglée dans l'édition de l'écran**, jamais dans la page Planning). À chaque niveau, un créneau daté passe avant un récurrent, puis l'ordre de création. Heures et dates : fuseau de l'aire (`Area.SleepTz`), à défaut celui de l'organisation.
- **Hors ligne** : chaque écran garde un instantané des listes qu'il peut jouer (`Screen.PublishedLists`, avec `PublishedListRevs`). Il est pris à la publication de l'écran (`Notifier.PublishScreen`) **et** à l'enregistrement d'un créneau (`Notifier.PublishPlanning`) : enregistrer un créneau l'applique tout de suite aux écrans touchés, sans republier leur liste par défaut. Le contenu pris est le brouillon actuel de la liste programmée. Une liste modifiée ensuite rend l'écran « à publier » (`Helpers.HasPending`). L'empreinte des créneaux (`Planning.Stamp`) fait partie de la révision de l'écran (`Helpers.SyncStamp`) : changer un créneau, ou l'écran de zone, recharge le player.
- **Player** (`player.js`) : le flux contient `schedule` = `{ tz, slots: [{ days, from, to, dateFrom, dateTo, items }] }` dans l'ordre de priorité ; `slotIndex` choisit le premier créneau actif, `itemsOf` renvoie ses contenus (plus les widgets d'écran) à la place de la liste par défaut. Un minuteur de 10 s (`scheduleTick`) change de liste dès qu'un créneau commence ou finit, sans attendre la fin du contenu. Les fichiers de tous les créneaux sont préchargés (`allItems`), donc le planning tourne hors ligne. Le simulateur d'un écran suit aussi son planning, à l'heure réelle.
- **Page** (`PlanningPage.razor`, `/planning`, maquette `maquettes/planning-v9.html`) : le **calendrier passe en premier** et occupe toute la hauteur. En-tête sur trois lignes : titre et « Programmer » ; sélecteur **Zone / écran** (menu : zones, leurs écrans, « planning propre » / « suit la zone ») ; changement de semaine, « Aujourd'hui », « Que voit l'écran ? » (tiroir à la demande) et Semaine / Journée à droite. Grille semaine **sur 24 h (00:00 – 24:00)** avec les plages de **mise en veille** de l'écran (sinon de l'aire) en hachuré gris quand la fonction est activée dans les Réglages, aussi en vue journée (clic sur une plage de 30 min = nouveau créneau, clic sur un créneau = modification, créneaux de la zone en hachuré sous ceux d'un écran), bandeau « suit la zone » / « Créer un planning propre » / « Revenir au planning de la zone », éditeur **en fenêtre modale** (liste, jours, heures, période, « s'applique aussi à » d'autres zones, avertissement de chevauchement), vue **journée** de toutes les zones. La liste par défaut n'est plus un bloc : la légende dit « Hors créneau : … · Modifier ».
- **Accès** : menu latéral ; pastille « Planning » à côté du filtre de zone de la page Écrans (`?zone=`) ; « Planning de cet écran… » dans le menu ⋮ d'un écran et « Planning du mur… » dans celui d'un mur (`?screen=`) ; icône calendrier du bandeau de l'éditeur d'écran.
- **Cohérence** : supprimer une liste, la déplacer vers une autre aire ou déplacer un écran d'aire retire les créneaux concernés ; supprimer une zone renvoie ses écrans dans « Défaut » (ils reçoivent alors les listes du planning de leur nouvelle zone). Une liste programmée compte comme « utilisée » dans la page des listes.
- **Analyse (pourquoi, pour qui)** : sans planning, un écran affiche sa liste jusqu'à ce qu'on la change à la main ; les contenus périmés restent à l'écran et chaque changement d'horaire ou de campagne demande une connexion le jour J. Le planning sert les responsables de communication (campagnes préparées à l'avance), les gestionnaires d'une aire (menu du midi puis carte du soir, sans administrateur), les administrateurs (moins de sollicitations, plus d'oubli de retrait) et les clients multi-sites (un même créneau sur plusieurs zones). Cas type : un restaurateur avec deux aires, une zone « Entrée » (menu + publicité) et une zone « Stand service » (menu + prix) qui ne diffusent pas la même liste aux mêmes heures. Décisions : le planning se rattache à une zone ou à un écran (pas d'objet à affecter) ; il est facultatif et de base (pas de réglage à activer) ; la liste par défaut garde une source unique ; le calendrier passe avant tout le reste (maquettes `planning-v2.html` pour le modèle, `planning-v9.html` pour la page). Limites assumées : pas de créneau qui passe minuit, planning de la zone 1 seulement pour un écran découpé, créneaux prioritaires par type (daté avant récurrent) sans règle plus fine.
- **Hors périmètre** (à revoir plus tard, décidé le 11 octobre) : créneau d'urgence prioritaire sur tout, et copie d'un planning vers une autre aire. Le planning ne vise que la zone 1 d'un écran découpé (zones 2 et 3 gardent leurs listes).
- **Vérifié** : tests `PlanningTests` (priorités, dates, validation, chevauchements, empreinte, déplacements) ; navigateur sur une organisation de démonstration : création et modification de créneaux (zone, plusieurs zones, écran propre), vue journée, quatre accès, mobile ; player réel : liste par défaut puis liste du créneau dans les 5 s après l'heure de début, mise à jour poussée à l'enregistrement. **Pas essayé** : fin de créneau en direct (même mécanisme que le début), app Android, créneaux avec dates sur plusieurs semaines, vidéos et apps dans une liste programmée.

## 38. Nettoyage des exports Canva et menu latéral (13 octobre 2026)

- **Exports Canva** : chaque export est marqué `Info["canva"]` (identifiant du design). Un nouvel export du même design retire les précédents que rien n'utilise (`CanvaService.PurgeUnused`) ; enregistrer un contenu dont les pages ont changé retire les anciennes (`AppService.SaveInstance`) ; supprimer un contenu retire ses exports devenus inutiles (`AppService.PurgeExports`). Un export encore diffusé (publié sur un écran ou une liste) n'est jamais retiré avant la publication suivante.
- **Médiathèque** : un bandeau « n exports Canva ne servent plus » (`MediaLibrary.UnusedCanva`) ouvre une confirmation, puis `DeleteUnusedCanva` supprime les fichiers (définitif ; le design peut être exporté à nouveau). `MediaLibrary.UnusedExports(…, clé)` est la recherche générale, aussi utilisée par le PDF (§29).
- **Menu latéral** (`MainLayout.razor`) : Médiathèque passe sous un trait après Planning ; Statistiques (tous les membres) et Réglages (administrateurs) forment un groupe en bas, au-dessus du compte.
- **Tests** : `CanvaCleanupTests`.

## 39. Intégrations Google Slides, Google Docs et Microsoft PowerPoint (13 octobre 2026)

**Vue d'ensemble** (les trois apps, une seule mécanique ; §39 décrit Google, §40 ajoute PowerPoint) :

| App | Fichier | Compte connecté | Conversion en PDF | Durée par défaut |
|---|---|---|---|---|
| `google-slides` Google Slides | présentation Google native (Mon Drive, Partagés avec moi, Drive partagés) | Google (`drive.readonly`) | export Google (`files.export`, 10 Mo au plus) | 10 s par diapositive |
| `google-docs` Google Docs | document Google natif | Google (`drive.readonly`) | export Google | 15 s par page |
| `ms-powerpoint` Microsoft PowerPoint | `.pptx`, `.ppt`, `.ppsx`, `.odp`… (OneDrive, SharePoint) | Microsoft (`Files.Read.All`) | conversion Graph (`content?format=pdf`) | 10 s par diapositive |

Dans les trois cas : le PDF est gardé dans `pdf/` (non versionné, `.gitignore`), converti en images 1080p par PDFium, lu comme un diaporama hors ligne (50 pages au plus), mis à jour toutes les heures par défaut quand le fichier change, sans lien public. Un `.pptx` ou `.docx` stocké dans Google Drive n'est pas exportable par Google (le sélecteur l'indique) ; dans OneDrive, PowerPoint le convertit directement. Une carte d'intégration affiche le compte utilisé (`ConnectedAccounts` : Slides et Docs sous Google, PowerPoint sous Microsoft). Voir aussi le PDF (§29), dont ces apps réutilisent `PdfService`.
Maquette validée : `maquettes/google-slides-docs.html` (sélecteur Slides / Docs ; états : prêt, compte non connecté, aucun fichier, export en cours, modifié dans Google, trop long, accès refusé ; options avancées ; schéma du parcours). **Codé le 13 octobre ; jamais essayé avec un vrai compte Google** (tests automatiques seulement). Décisions de l'utilisateur : **deux intégrations distinctes** (apps `google-slides` et `google-docs`, catégorie Médiathèque) ; **pas de lien public** ; **Google Sheets écarté** ; mise à jour automatique **toutes les heures** par défaut ; **export PDF puis conversion en images** (pas l'API Slides, dont les images sont plafonnées à environ 1600 px).

- **Principe** : même parcours que le PDF (§29). Le fichier est choisi dans le Drive du compte Google connecté, **exporté en PDF par Google** (`files.export`, `application/pdf`), gardé dans `pdf/` comme un PDF choisi, puis converti en images 1080p par `PdfService` (PDFium) et lu comme un diaporama : hors ligne, zones 2 et 3, simulateur, durée, cadrage et transition sont ceux des images. Player : `A.register('google-slides' | 'google-docs')` réutilise `slideshow-images` ; `Helpers.ImagesSeconds` les compte comme des diaporamas d'images.
- **Droits Google** : aucune autorisation nouvelle. `files.export` fonctionne avec `GoogleAuth.DriveScope` (`drive.readonly`), déjà demandée ; ni API Slides ni API Docs, aucun nouveau scope, pas de nouvelle validation de l'application Google. Le compte connecté doit pouvoir **lire** le fichier ; si le propriétaire a interdit le téléchargement aux lecteurs, l'export est refusé (message dédié). Limite de Google : 10 Mo par export (message dédié).
- **Code** :
  - `Apps/CloudFiles.cs` (généralisé au §40) : `CloudFileKind` (Slides, Docs, PowerPoint), `CloudFileService` (`Import` = lecture du fichier + export + `PdfService.SaveStream` + `Convert` ; `Apply` ; `HasChanged` ; `Update` ; `CheckAll`) et `CloudFilesWorker` (toutes les 5 minutes, chaque contenu a sa propre fréquence : réglage `refresh`, 15 min / 1 h / 1 jour, une heure par défaut ; `Info["gcheck"]` garde la dernière vérification).
  - `DriveService` (`Apps/Drives.cs`) : `BrowseFiles` et `SearchFiles` (sélecteur), `CloudFileInfo` (nom, date de modification, vérifie le type), `ExportPdf` (Google : messages pour 401, 403, 404, 10 Mo). `GoogleGet` accepte un mot (« Fichier ») pour son message d'erreur.
  - `PdfService` : `SaveStream` (garde un PDF lu dans un flux ; avec un identifiant, **remplace** le PDF déjà gardé après avoir vérifié le nouveau, un échec laisse l'ancien intact) ; `Options.Unit` (« diapositive » pour Slides via le champ caché `gkind`, « page » sinon, dans le nom des images : « Titre — diapositive 3 ») ; `PurgeUnused` devient public.
  - `Notifier.RefreshPages` : après une mise à jour automatique, les éléments déjà publiés reçoivent les nouvelles images sans republier (comme `RefreshDrive`) ; les anciennes images sont retirées ensuite.
  - Champ `gfile` (`AppCatalog` : type, `accept` = `presentation` | `document`, validation : le PDF doit exister et avoir des images) ; composants `CloudFileField` (une ligne : fichier, nombre de diapositives ou pages, date de modification, « Mettre à jour », « Changer » ; compte connecté en pastille ; avertissement « Le fichier a changé dans Google ») et `CloudFilePicker` (parcours Mon Drive / Partagés avec moi / Drive partagés, recherche par nom).
  - Manifestes `Apps/google-slides.json` et `Apps/google-docs.json`. Clés partagées avec le PDF : `pdf`, `pdfTitle`, `pdfPages`, `pages`, `range`, `background`, `canvas` ; propres : `gfile`, `gfileName`, `gfileMod`, `gkind`, `auto`, `refresh`.
  - Nettoyage : comme le PDF (`AppService` : le PDF gardé suit son contenu, l'ancien est retiré si on change de fichier ; images marquées `Info["pdf"]`, provenance « PDF » dans la médiathèque). `AppInstanceForm.ApplyAdvanced` reconvertit pour les trois apps quand les pages ou le fond changent.
- **Slides** : durée par diapositive 10 s, cadrage (Diapo entière / Remplir l'écran), transition ; avancé : diapositives à afficher, mise à jour automatique + fréquence, fond (Noir / Blanc / Diapo floutée), sens de l'écran, vitesse. **Docs** : durée par page 15 s, mêmes réglages avec « Page entière » et « Page floutée » ; pages portrait A4, petites sur un écran paysage (le fond et le sens de l'écran servent à ça). Les commentaires et suggestions ne sont pas exportés.
- **Écarts avec la maquette** : « Ignorer les diapositives masquées » n'existe pas (Google décide de ce que contient son export PDF, non vérifié) ; « Sens de l'écran » est proposé pour les deux apps (dès qu'un fond est choisi).
- **Limites assumées** : animations, vidéos intégrées et liens perdus (images fixes) ; 50 pages au plus ; polices absentes remplacées par Google ; dans une zone synchronisée, la durée de la boucle est figée à la publication : après une mise à jour qui change le nombre de pages, elle ne se recale qu'à la publication suivante.
- **Tests** : `CloudFilesTests` (manifestes, validation, `SaveStream` et échec de mise à jour, noms, fréquence, apps d'images) ; `AppsTests` liste les deux apps. Les 5 `AppsTests` en échec (agenda v1) l'étaient déjà.
- **À valider** : export de vrais fichiers (polices, grandes tailles, diapositives masquées, 10 Mo), avec un compte Google réel ; fenêtre de configuration et sélecteur dans le navigateur (non vus) ; mise à jour automatique en conditions réelles (modification dans Google, écran qui reçoit les nouvelles images) ; message d'erreur d'un fichier dont l'export est interdit.
## 40. Intégration Microsoft PowerPoint (13 octobre 2026)

Même base que Google Slides et Google Docs (§39), demandée par l'utilisateur le 13 octobre : app `ms-powerpoint` (« Microsoft PowerPoint », catégorie Médiathèque). **Codée, jamais essayée avec un vrai compte Microsoft 365** (tests automatiques seulement) ; pas de maquette à part (même fenêtre que Google Slides : `maquettes/google-slides-docs.html`).

- **Principe** : la présentation est choisie dans OneDrive ou SharePoint du compte Microsoft connecté, **convertie en PDF par Microsoft Graph** (`GET /drives/{drive}/items/{item}/content?format=pdf`, redirigé vers le fichier ; le jeton n'est pas transmis à l'hôte de la redirection), gardée dans `pdf/`, puis convertie en images 1080p par `PdfService` (PDFium) et lue comme un diaporama : tout le reste est identique à Google Slides (§39).
- **Droits Microsoft** : aucune autorisation nouvelle, `Files.Read.All` (`MicrosoftAuth.DriveScope`, déjà demandée pour OneDrive et SharePoint) suffit. Le compte doit pouvoir lire le fichier. Formats acceptés (par l'extension) : `.pptx`, `.ppt`, `.pptm`, `.ppsx`, `.pps`, `.potx`, `.odp`. Le compte Microsoft ne sert pas à Google Slides, ni l'inverse.
- **Généralisation du code Google** : `GoogleFileKind` / `GoogleFileService` / `GoogleFilesWorker` / `GoogleFileField` / `GoogleFilePicker` deviennent `CloudFileKind` (source `gdrive` ou `onedrive`, type de fichier, compte), `CloudFileService`, `CloudFilesWorker`, `CloudFileField`, `CloudFilePicker` (`Apps/CloudFiles.cs`, `Components/Cloud*.razor`). Les clés des valeurs sont inchangées (`gfile`, `gfileName`, `gfileMod`, `gkind`, `auto`, `refresh`, `pdf`…) pour ne pas casser les contenus Google déjà créés ; le champ `gfile` accepte `presentation`, `document` ou `powerpoint`.
- **Sélecteur** (`CloudFilePicker`) : pour Microsoft, Mon OneDrive, Partagés avec moi et SharePoint (on colle le lien d'une bibliothèque, comme pour les dossiers Drive, §17), recherche dans le OneDrive du compte ; les fichiers du type voulu sont listés avec les dossiers. Pour Google, un `.pptx` ou `.docx` du Drive n'est pas exportable : le sélecteur le signale (« Enregistrer en tant que Google Slides »).
- **Autres changements** : `DriveService.BrowseFiles / SearchFiles / CloudFileInfo / ExportPdf` (Google ou Microsoft) ; `GraphService.Token` ; `GraphGet` accepte un mot pour son message d'erreur ; la carte du compte Microsoft (Comptes connectés) et la ligne « Compte » de l'intégration listent PowerPoint (`ConnectedAccounts`, index 0 et 1 inchangés : agendas, fichiers) ; icône orange ; lecture par le player = `slideshow-images` ; durée fixée en zone synchronisée par `Helpers.ImagesSeconds`.
- **Réglages** : identiques à Google Slides (durée 10 s, cadrage, transition ; avancé : diapositives à afficher, mise à jour automatique toutes les heures par défaut, fond, sens de l'écran, vitesse). Les animations, transitions et vidéos intégrées ne sont pas conservées.
- **Tests** : `CloudFilesTests` (manifeste, extensions reconnues, un compte par fournisseur, nom des images, comptes listés) et `AppsTests` (catalogue). Les 5 `AppsTests` en échec (agenda v1) l'étaient déjà.
- **À valider** : conversion de vraies présentations (polices, mise en page, grandes tailles, présentations SharePoint) avec un compte Microsoft 365 réel ; limite de taille ou de durée de la conversion Graph ; fenêtre et sélecteur dans le navigateur ; mise à jour automatique (`lastModifiedDateTime`).
## 41. Comptage des scans de QR code (12 octobre 2026) : codé

Demande : connaître le nombre de « vues » d'un contenu par scan de QR code, activé par un interrupteur sur le contenu dans la liste de lecture. Maquette validée : `maquettes/qr-comptage.html`. **Codé le 12 octobre 2026, vérifié dans le navigateur** (serveur de test, licence Base puis Growth, glisser réel, simulateur « Essayer », scans par `curl`-équivalent) ; jamais essayé sur un vrai écran ni derrière un nom de domaine public.

### Ce qui est codé
- **Modèle** (`Models.cs`) : `PlaylistItem` porte `QrOn`, `QrCode` (adresse courte, créée à la première activation, jamais changée), `QrUrl` (destination), `QrLabel`, `QrBase` (https://domaine relevé à l'enregistrement, par `NavigationManager.BaseUri`), `QrX`, `QrY`, `QrSize` (en % de la zone du contenu). `PublishedItem.Qr` (`PublishedQr` : tracé « côté|tracé », x, y, taille, texte) part au player (`qr` dans `ForPlayer`) ; **la destination ne part jamais au player**.
- **Logique** (`QrTracking.cs`) : codes de 8 caractères (`NewCode`), `Destination` (http/https seulement, jamais `javascript:` ni `file:`), `ShortUrl`, `Overlay` (QR publié seulement si contenu **pleine page**, activé, **licence Growth**, code et destination valides), `ClampSize` / `ClampPos` (3 à 45 %, toujours entier dans l'écran), `LooksLikeBot`. `ScanLog` : journal JSON Lines par client (`scans/{client}.jsonl`, 90 jours, comme `PlayLog`), sel secret dans `scans/salt.bin`.
- **Publication** (`Notifier.Snapshot`) : le QR est ajouté à chaque élément publié (fichiers et apps), via `QrTracking.Overlay`. Dupliquer une liste (`Playlists.razor`) donne un **nouveau code** au QR copié (les scans ne se mélangent pas).
- **Route publique** `/q/{code}` (`Program.cs`, ouverte dans la porte d'entrée) : compte le scan puis redirige en 302 (`Cache-Control: no-store`, `Referrer-Policy: no-referrer`). Elle redirige **toujours** tant que le contenu existe, même suivi coupé ou licence Base (un QR affiché ne doit jamais casser) ; elle ne **compte** que si le suivi est actif, l'organisation est Growth et non suspendue, et que ce n'est pas un robot. Code inconnu ou mal formé : 404.
- **Comptage** : empreinte du visiteur = HMAC (sel du serveur + date du jour + IP + navigateur) : aucune IP stockée, change chaque jour, donc « visiteurs » = un téléphone par jour. Même visiteur et même contenu dans les 10 secondes : un seul scan. Robots écartés (requêtes HEAD, agents de messageries, antivirus, `curl`, agent absent…).
- **Player** (`player.js` `addQr`, `index.html` `.qr-ov`) : le QR est posé en dernier dans le calque du contenu (donc **au premier plan**, il apparaît et disparaît avec lui), **fond blanc opaque** (il recouvre un QR présent dans le contenu), position et largeur en % de la zone (1em = 1 % de la largeur de la zone), texte à 11 % de la largeur du QR. Aucun calcul de QR côté écran (le tracé vient du serveur, comme le widget QR).
- **Back-office** : `PlaylistEditor.razor` ; fenêtre d'un contenu d'app avec **onglets « Contenu » / « QR code »** (`.se-utabs`) en tête de la colonne de droite, via de nouveaux paramètres d'`AppInstanceForm` (`ConfTabs`, `OtherTab`, `OtherPane`, `OtherPreview` : la configuration de l'app est masquée, pas retirée, elle garde son état). Onglet « QR code » : interrupteur, adresse de destination (validée à l'enregistrement), texte sous le QR, taille (curseur 3 à 45 %). L'aperçu de gauche devient l'**aperçu de l'écran** (`QrPlacer.razor`, image du contenu en fond, `MediaThumb Wide`) où le QR se **glisse à la souris ou au doigt** (`wwwroot/qr-place.js`, aimanté aux axes du centre, flèches du clavier, Maj = 5 %) ; position enregistrée en % au lâcher. Sur la ligne de la liste : pastille « N scans » (90 jours), un clic ouvre l'onglet.
- **QR visible partout (correctif du 12 oct.)** : `QrBadge.razor` dessine le QR (même position et taille qu'à l'écran, en % de l'aperçu 16:9) sur la **vignette de la ligne** de la liste (version mini, sans texte), sur l'aperçu de l'onglet « Contenu » (image seule, **chaque diapositive du carrousel et de la grille** `Carousel3D`, miniature YouTube, aperçus page web / flux / agenda de `ContentPreview`) : même comportement pour toutes les intégrations. L'aperçu suit les réglages en cours (avant d'enregistrer) ; la vignette suit les réglages enregistrés. Le tracé d'une adresse est mis en cache (`QrTracking.Build`). Carrousel / grille / aperçus page web, flux, agenda : codés, vus seulement pour l'image seule et la vignette.
- **Licences** : Base → onglet visible avec « 🔒 Growth », interrupteur grisé, « Disponible avec la licence Growth. » ; Growth → complet. **Contenu en angle ou bandeau → onglet grisé** (« Le QR code n'est disponible que pour un contenu en pleine page »). Les widgets d'écran (texte, horloge, météo, QR…) ne sont jamais dans une liste de lecture : sans objet.
- **Tests** : `QrTrackingTests` (26 : codes, destinations, adresse courte, QR publié selon licence / placement / suivi, bornes, rien d'interne vers le player, robots, journal des scans, visiteurs par jour, sel conservé). Les 5 `AppsTests` en échec (agenda v1) l'étaient déjà.

### À savoir, à valider
- **Domaine public** : le téléphone d'un visiteur doit joindre le serveur ; `QrBase` est le domaine utilisé **à l'enregistrement** (en local : `http://localhost:5080`, inutilisable pour un téléphone). Après un changement de domaine, rouvrir le contenu, enregistrer et republier.
- **Republier** : les écrans gardent le QR (position, adresse) de leur dernière publication ; le comptage et la destination, eux, suivent l'état courant. Un contenu **retiré** de la liste donne un 404 au scan (tant que des écrans affichent encore son QR).
- **Création d'un contenu** : l'onglet n'existe que dans la fenêtre d'un contenu **déjà dans la liste** (pas dans la fenêtre d'ajout). Contenus de fichiers « anciens » (sans app) : pas d'onglet.
- **Non fait** : comptage par écran (le QR est par contenu) ; page de résultats détaillés (scans par jour, par écran, taux de scan ; seul le total des 90 jours est affiché) ; export ; essai sur un vrai écran, un vrai téléphone et derrière le reverse proxy (IP réelle via `X-Forwarded-For`, déjà gérée pour le reste de l'application).

### Analyse initiale (avant de coder)

- **Décisions de l'utilisateur** : option réservée à la **licence Growth** (`Tenant.IsGrowth`, message `Plans.LockedHint`) ; en Base, l'onglet et l'interrupteur sont affichés verrouillés (« 🔒 Growth ») ; le réglage se fait dans **un onglet « QR code » de la fenêtre de configuration du contenu** (pas dans une page à part).
- **Principe** : le QR n'encode pas l'adresse finale mais une adresse courte Linkii stable (`https://{domaine}/q/{code}`). Le serveur la calcule à la publication (comme `QrCodes.Build`, l'écran dessine le tracé hors ligne). Au scan, le serveur enregistre puis répond 302 vers la destination, modifiable sans changer le QR affiché.
- **Ce qui est compté** : des **scans**, pas des vues. Visiteurs estimés = empreinte anonyme (IP + navigateur, salée, renouvelée chaque jour, aucune IP stockée : nLPD/RGPD), un même téléphone = 1 visiteur par jour. Robots d'aperçu (messageries, antivirus : filtrés par agent et requêtes HEAD) et doubles-clics écartés. Taux de scan = scans ÷ affichages (déjà dans `PlayLog`).
- **Pistes de code** : champs sur `PlaylistItem` (suivi actif, destination, texte, position, code court) ; `ScanLog` calqué sur `PlayLog` (JSON Lines par client, 90 jours) ; point d'entrée public `/q/{code}` hors `/api` (redirige toujours, même en Base ou si le suivi est coupé, pour ne jamais casser un QR affiché) ; QR en surimpression dans le player (4 angles, texte dessous) ; statistiques dans `Stats.razor` (Growth). Pas de destination possible pour les widgets sans lien (météo, horloge) : pas d'interrupteur.
- **Maquette (fidèle à l'application)** : fenêtre `.modal.app-modal.ci-modal` (bandeau icône + titre + « Renommer » + options avancées + croix ; ligne source grise ; aperçu à gauche, configuration à droite ; pied « Annuler / Enregistrer »). Onglets **« Contenu » / « QR code »** (style `.se-utabs` de l'éditeur d'écran) en **haut de la colonne de droite**, l'aperçu restant à gauche avec le QR incrusté en direct. « Contenu » = configuration de l'app + « Dans cette liste de lecture » ; « QR code » = interrupteur (`input.ios`), destination, texte, position, adresse courte, lien vers les statistiques. Dans la liste, pastille « 128 scans » sur la ligne du contenu (ouvre l'onglet). Cinq états de démo dont QR désactivé et licence Base.
- **Reste à décider** : emplacement des onglets (colonne de droite, comme dans l'éditeur d'écran, ou pleine largeur sous le bandeau) ; scans par écran (QR avec suffixe par écran, faisable car la publication est déjà par écran) ou par contenu seulement ; durée de conservation des scans (90 jours comme les diffusions ?) ; pages Résultats détaillés (maquettées dans une première version, retirées) ; serveur joignable depuis Internet pour les téléphones des visiteurs (à vérifier côté réseau) ; vue de résultats dans la page Statistiques.

## 42. Fenêtres d'écran et de contenu : hauteur fixe, colonne de configuration qui défile (12 octobre 2026)

Demande : sur PC, garder l'aperçu visible pendant le réglage. Seule la colonne de droite défile, l'aperçu reste à gauche. Codé dans `wwwroot/app.css` (blocs `min-width` 721 px et 901 px) et `Components/ScreenEditor.razor`. **Non vérifié dans le navigateur** : à valider sur l'écran.

- **Fenêtre de contenu** (`.ci-modal`, `AppInstanceForm`) : hauteur fixe `min(720px, 100vh - 48px)`, largeur `min(1120px, 100vw - 32px)`, aperçu en colonne souple et configuration à 320 px. Seule la colonne de configuration (`.ci-conf`) défile. Mobile (720 px et moins) inchangé.
- **Fenêtre d'écran** (`.wg-modal`, `ScreenEditor`) : hauteur fixe `min(720px, 100vh - 48px)` au-delà de 900 px. La colonne de réglages (`.wg-side`) défile seule et son contenu remonte en haut. L'aperçu est dans une zone bleu nuit `#0b1f3a` avec un liseré bleu `#378ADD`.
- **Zoom de l'aperçu** : barre « − 100 % + » en bas de la zone bleu nuit, de 50 % à 150 % par pas de 10 %. Le réglage est une variable CSS `--z` sur la zone, et l'écran se dimensionne sur la hauteur (portrait) ou la largeur (paysage) de cette zone (`container-type: size`). Non mémorisé d'une ouverture à l'autre. Icône `minus` ajoutée à `Ico.razor`.
- **Non retenu pour l'instant** : quatre propositions de mise en évidence de l'écran (liseré, étiquette, titre, liseré et badge) présentées en maquette ; le choix reste à faire.
- **À valider** : rendu réel dans le navigateur sur plusieurs tailles d'écran ; défilement quand le contenu dépasse ; écran en paysage et zoom 150 % avec des widgets glissés.

## 43. Intégration Facebook : publications d'une Page (12 octobre 2026) : codée

Demande : une intégration Facebook gratuite, sur la base des intégrations existantes (modèle : le flux RSS). Analyse et maquettes : `maquettes/facebook-publications.html` et `maquettes/facebook-integration-app.html`.

- **Principe** : app « Facebook (publications d'une Page) », `Apps/facebook.json` (nocode, placements pleine page, bandeau haut et bas). Le serveur lit `/{page}/published_posts` de l'API Graph de Meta (v21.0) avec un **jeton de Page** ; le player ne reçoit que texte, date et adresse de la photo (`FacebookProvider` dans `Apps/Providers.cs`, rendu `facebook` dans `wwwroot/player/apps.js`, styles `.fb-*` dans `player/index.html`).
- **Compte connecté (« Se connecter avec Facebook »)** : kind `facebook` dans Intégrations › Comptes connectés (`Apps/FacebookAuth.cs`, routes `/facebook/connect` et `/facebook/callback`, retour sur l'onglet d'origine). OAuth 2 avec l'application Meta de la plateforme : `Linkii:Facebook:AppId`, `Linkii:Facebook:AppSecret` et, en production, `Linkii:Facebook:RedirectUri` (à déclarer dans la console Meta, sinon adresse du back-office + `/facebook/callback`). Permissions `pages_show_list` et `pages_read_engagement`. Au retour : code → jeton d'utilisateur → jeton de longue durée → `/me/accounts` : seuls les **jetons de Page** (sans expiration) sont gardés, chiffrés dans `Tenant.FacebookPages` ; le jeton de l'utilisateur est oublié. Un jeton de Page refusé (code Meta 190) passe le compte en « à reconnecter ». Déconnecter oublie le compte et les jetons ; l'accès reste listé dans les intégrations du compte Facebook.
- **Champs** : Page (type `facebookpage` : liste des Pages du compte connecté, ou saisie de l'identifiant / de l'adresse sans compte, zone « source »), mode (Mur / Carte unique / Dernière), nombre de publications (1 à 12), photos oui/non, durée de chaque page (⚙ avancées, 12 s), jeton de Page collé à la main (⚙ avancées, facultatif : prioritaire sur le compte connecté, secret chiffré jamais renvoyé au formulaire ni au player).
- **Affichage** : Mur = une grande carte et jusqu'à trois petites par page, qui défilent s'il y a plus de quatre publications ; Carte unique = une publication à la fois ; Dernière = la plus récente seulement ; bandeau = textes défilants. Une publication sans photo s'affiche en carte de texte ; une photo qui ne charge pas (expirée, hors ligne) laisse le texte.
- **Sécurité** : le jeton part dans un en-tête `Authorization` (jamais dans l'adresse ni les journaux) et doit respecter un format strict ; l'identifiant de Page est réduit à `[A-Za-z0-9.-]` (une adresse collée comme `facebook.com/Nom`, `/pages/Nom/123` ou `profile.php?id=123` est reconnue, tout autre hôte refusé) ; appels par `SafeHttp` ; seules les photos en https sont gardées ; les erreurs de l'API sont expliquées par leur code, sans reprendre le texte brut.
- **Cache** : 15 minutes côté serveur (clé : Page, nombre, empreinte du jeton, pour qu'un client n'hérite pas du cache d'un autre) ; si Facebook est indisponible, la dernière version connue est servie. Le bouton « Tester » ignore le cache.
- **Tests** : `Linkii.Poc.Tests/FacebookTests.cs` (adresse de Page, lecture de la réponse, erreurs Graph) et `FacebookAuthTests.cs` (adresse de consentement, retours refusés, Pages, jetons chiffrés, compte perdu, déconnexion, ligne dans Comptes connectés). Rendu du player vérifié dans le navigateur avec des publications factices (mur, carte unique, dernière, rotation des pages). Cinq tests d'`AppsTests` échouent déjà sur le dernier commit (installation de `clock` et `agenda`) : sans rapport avec cette intégration.
- **Jamais essayée avec un vrai compte ni une vraie application Meta**. À valider : connexion réelle (en mode développement de l'application Meta, seuls ses administrateurs et testeurs peuvent se connecter ; l'ouvrir à tous demande la revue Meta des permissions), échange POST de `/oauth/access_token`, `/me/accounts` avec les Pages d'une Page « nouvelle expérience », lecture d'une vraie Page, forme des dates, durée de vie des adresses de photos (fbcdn, signées), comportement avec un jeton retiré. Écran Intégrations et liste des Pages dans la fenêtre du contenu : compilés mais pas encore vus dans le navigateur.
- **Reste à faire / décisions** : pas de réactions ni de commentaires (permission supplémentaire) ; pas de publication épinglée ; les photos ne sont pas copiées dans la médiathèque (elles demandent internet sur l'écran) ; conditions d'usage de Meta pour l'affichage sur écran public à confirmer avant un usage commercial.
- **Type de champ `facebookpage`** (`AppCatalog.cs`) : validé à l'enregistrement de l'app (`FacebookProvider.PageRef`), message clair si la Page n'est pas reconnue.
- **Retour au compte pendant la connexion** (`wwwroot/reconnect.js`) : quand l'utilisateur quitte la page pour aller sur Facebook (ou Google, Microsoft), la surveillance de connexion du back-office ne déclenche plus l'écran « Connexion perdue » ; elle reprend après 15 s si la navigation est annulée, ou au retour de la page (`pageshow`).
- **Intégrations** (`Pages/Integrations.razor`) : carte « Facebook » dans Comptes connectés (connexion, déconnexion, état « Compte à reconnecter » sur les apps Facebook), et l'app « Facebook » est rangée dans la liste des intégrations de contenus.

## 44. Liste de lecture pleine largeur et dépôt propre (12 octobre 2026)

- **Page d'une liste de lecture** (`wwwroot/app.css`) : la liste des contenus occupait 1100 px au maximum, alors que l'en-tête et les boutons occupaient toute la largeur. La limite `max-width` de `.pe-main` est supprimée : la liste prend toute la largeur disponible, comme l'en-tête. À vérifier dans le navigateur (la session de test n'était pas connectée).
- **Dossier `Linkii.Poc/scans/`** ajouté au `.gitignore` : il contient le sel secret du comptage des QR (`salt.bin`) et les journaux de scans (`{client}.jsonl`). Ces données ne doivent jamais être versionnées, comme `keys/` et `data.json`.
- **Tests** : `dotnet test` donne 357 réussites et 5 échecs. Les 5 échecs (`AppsTests` : `Hidden_fields_are_neither_required_nor_stored`, `Secrets_are_encrypted_kept_when_left_empty_and_never_sent_to_the_player`, `Uninstalling_removes_instances_and_marks_playlists_unpublished`, `Screens_read_the_published_snapshot_of_their_own_playlist_only`, `Apps_belong_to_the_client_who_added_them`) existent déjà sur le dernier commit (`HEAD`), vérifié dans un worktree propre : ils ne viennent pas des changements de cette série. À corriger à part.

## 45. Publicité rémunérée sur les écrans (Growth) : analyse et maquette (12 octobre 2026)

Demande : permettre au propriétaire d'un compte **Growth** d'afficher des spots publicitaires **contre rémunération**, avec une **preuve de diffusion** (nombre de passages et temps d'affichage complet, par jour, par semaine et par mois). Spot pleine page, dans les **deux formats** (portrait et paysage). **Non codé** : analyse puis maquette `maquettes/publicite.html` à valider.

### Décisions (12 octobre 2026)
1. **Linkii fournit les spots et paie** : Linkii joue le rôle de régie. Le propriétaire n'apporte pas de spot en V1.
2. **Tarification au passage complet** : la métrique principale est le nombre de passages complets.
3. **Complet = 100 %** de la durée prévue, sans pause, sans veille, sans coupure.
4. **Droit de refus du propriétaire : oui, dès la V1** (annonceur, catégorie, spot, avec délai de réponse).
5. **Format manquant : on approxime** (format le plus proche, fond flouté, jamais de déformation).
6. **Conservation de la preuve : 13 mois** (journal détaillé et agrégats journaliers).
7. **Murs et écrans découpés : hors périmètre** de la V1 (un écran découpé en zones ou membre d'un mur ne diffuse pas de pub).
8. **Console des campagnes : administration Linkii** uniquement (*Admin › Publicité*). Côté client : un réglage et un tableau de revenus.

### Acteurs
- **Annonceur** : paie Linkii. **Linkii** : crée les campagnes, mesure, facture, reverse une part. **Propriétaire du compte** : autorise la pub sur ses aires/écrans, accepte ou refuse les spots, voit ses revenus.

### Le spot
- **Campagne** (annonceur, période, tarif par passage complet, part du propriétaire, plafond de passages, plafond par jour, fréquence) contenant un ou plusieurs **spots**. Un spot a un **visuel paysage 16:9 et un visuel portrait 9:16** (image ou vidéo, durée fixe, vidéo sans son par défaut). Un spot sans l'un des deux formats est accepté.
- **Choix du format** par le serveur à la publication, d'après l'orientation de l'écran (rotation réglée, `DetectedW/H`) ; le player n'a rien à deviner. Format manquant ou écran de ratio inhabituel : visuel le plus proche **centré sur fond flouté**, sans recadrage ni déformation ; le format réellement utilisé est inscrit dans chaque passage.
- **Pleine page seulement.** L'écran affiche le spot sur toute sa surface. Un écran découpé ou membre d'un mur est exclu (décision 7).
- **QR code possible sur le spot** : le comptage des scans (§41) s'applique tel quel (pleine page, Growth).

### Quand un spot passe
- Couche **indépendante du planning** : le spot s'intercale entre deux contenus (jamais au milieu), tous les N contenus ou toutes les N minutes, avec un plafond par heure et par jour et une répartition régulière du quota.
- Aucun spot, aucun comptage : écran en veille, en pause depuis le back-office (§23), aperçu et simulateur, licence repassée en Base (le serveur retire les spots à la publication, comme le QR), compte suspendu, aire / écran non autorisé, spot refusé.
- **Hors ligne** : les spots sont préchargés comme les médias (§35) ; les passages sont notés localement et remontés au retour de la connexion.

### Mesure : ce que `PlayLog` ne fait pas
| Limite de l'historique actuel | Correction pour la pub |
|---|---|
| Pas d'identifiant par diffusion : un lot renvoyé après une réponse perdue donne des doublons. | **Identifiant unique par passage**, dédoublonné côté serveur. |
| Pas de notion de « complet » (durée arrondie, rien sous 1 s). | Durée prévue, durée réellement affichée (horloge monotone), indicateur **complet**, cause d'interruption (pause, veille, coupure, rechargement). |
| Déclaratif du player, sans recoupement. | Heure recalée par le serveur (décalage d'horloge), recoupement avec la présence de l'écran, plafond de plausibilité, **captures échantillonnées pendant le spot** (fonction capture Growth, §23), lots signés par le jeton de l'écran. |
| 90 jours, 2 000 diffusions en attente au plus. | **Journal dédié `AdLog`** (JSON Lines par client), file locale compacte, **13 mois**, agrégats journaliers conservés aussi 13 mois. |

- **Définitions** : *passage complet* = affiché sur 100 % de sa durée prévue ; *passage interrompu* = compté à part, non rémunéré ; *temps d'affichage complet* = passages complets × durée du spot ; périodes **jour, semaine (ISO, lundi) et mois** dans le fuseau de l'aire (`Area.SleepTz`, à défaut celui de l'organisation).
- **Rapport de preuve** : par écran, spot et jour / semaine / mois, export **CSV et PDF** avec numéro de rapport et empreinte, et captures horodatées en annexe.

### Côté propriétaire (compte Growth)
- **Réglages › Publicité** (facultatif, désactivé par défaut) : interrupteur général, aires et écrans autorisés, catégories exclues, **spots à valider** (accepter / refuser dans le délai), tableau de **revenus** (jour, semaine, mois ; statuts à valider, validé, payé), relevé mensuel. Licence Base : section visible, verrouillée (« Disponible avec la licence Growth »).
- **Alertes** : écran hors ligne pendant une campagne, spot non préchargé, format absent.

### Pistes de code
- `Ads.cs` : `Campaign`, `Spot`, affectations (niveau plateforme, pas par client) ; `AdLog` calqué sur `PlayLog` et `ScanLog` (JSON Lines, idempotence, agrégats) ; `Plans` / `Tenant.IsGrowth` pour la licence ; `Notifier.Snapshot` pour publier les spots (`ads` dans `ForPlayer`, comme `schedule` et `qr`) ; `POST /api/player/ad-plays` ; `player.js` (insertion dans `next()`, comptage dédié, préchargement) ; app Android = même player (WebView) ; pages *Admin › Publicité* et *Réglages › Publicité*, résultats dans `Stats.razor`.

### Hors périmètre V1 (à revoir)
Priorités et pondération entre campagnes, rattrapage de volume, facturation et paiement automatisés (Linkii calcule, le paiement se fait ailleurs), mesure d'audience par caméra (écartée : vie privée), annonceur qui se connecte lui-même, pub sur mur ou écran découpé.

### Reste à décider
Barème et part du propriétaire (par campagne ou globale) ; délai de validation d'un spot avant diffusion automatique ; fréquence par défaut ; durées de spot autorisées (10, 15, 20, 30 s) ; modèle du relevé mensuel ; mention légale à l'écran ou non.

## 46. Gestion des droits : niveaux de droits (13 octobre 2026) : codé

Demande : simplifier au maximum la gestion des droits. L'administrateur seul crée des **niveaux de droits** ; chaque membre en reçoit un, à l'invitation ou en modifiant le membre. Un niveau règle les pages visibles, les intégrations et widgets utilisables, et un filtre aire › zones › écrans. La page **Membres** sort des Réglages pour le menu latéral. **Codé le 13 octobre 2026** (voir « Réalisation ») ; analyse et maquettes `maquettes/droits-fonctionnement.html` (principe, exemple, règles) et `maquettes/droits-pages.html` (menu, Membres, Niveaux, éditeur de niveau, invitation, vue d'un membre).

### Principe
- **Un niveau = trois choses** : (1) pages visibles, (2) intégrations et widgets utilisables, (3) périmètre (aires › zones › écrans). **Un membre = un niveau.** Plus de rôle par aire.
- **Administrateur de l'organisation** : n'est pas un niveau. Il a tout, sans filtre, et seul il crée, modifie et supprime les niveaux, invite, modifie et désactive les membres. Plusieurs administrateurs possibles, un au moins reste actif (règle actuelle).
- Dans le formulaire d'un membre, **une seule liste** : « Administrateur — tous les droits », puis les niveaux créés. Elle remplace l'interrupteur « Administrateur de l'organisation » et les rôles par aire.

### Menu latéral
- Membres quitte les Réglages (tuile « Membres » de l'utilisation et ligne « Gérer les membres » de l'organisation supprimées) et se place **au-dessus d'Intégrations**, avec **un trait** entre les deux. Ordre du bas du menu : *Aires de gestion, Membres, ─, Intégrations, Widgets, ─, Réglages*, puis le compte.
- Aujourd'hui Membres n'apparaît qu'à partir de la 2<sup>e</sup> aire ou du 1<sup>er</sup> invité (§14). Nouveau : **toujours visible pour l'administrateur**, car c'est de là qu'il crée ses niveaux. Un compte seul voit donc Membres (décision 1).
- La page Membres a deux onglets : **Membres** (colonnes Niveau et Périmètre au lieu d'une colonne par aire) et **Niveaux de droits** (liste, nombre de membres, création).

### Ce que règle un niveau
1. **Pages** : cases Écrans, Listes de lecture, Planning, Médiathèque. Intégrations et Widgets ne se cochent pas : la page apparaît si au moins une app (ou un widget) est cochée. **Jamais offertes** : Membres, Aires de gestion, Comptes connectés, Réglages.
2. **Intégrations et widgets** : une case par app du catalogue et par widget, avec « tout cocher ». Cocher = **utiliser** (créer et modifier des contenus avec cette app, la placer dans une liste ou sur un écran). La connexion à un compte (Comptes connectés) reste à l'administrateur. Un contenu existant dont l'app n'est plus autorisée reste visible et diffusé, mais n'est plus modifiable ni créable par ce niveau.
3. **Périmètre** : arbre aire › zone › écran à cases à trois états. Cocher un parent coche tout ce qu'il contient, **y compris les zones et écrans créés plus tard** ; décocher un enfant rend le parent partiel. Résumé en clair sous l'arbre. Un niveau sans aucun écran coché est refusé à l'enregistrement.

### Effet du périmètre
| Élément | Règle |
|---|---|
| Aires | Seules celles du périmètre figurent dans le sélecteur d'aire (menu) ; l'aire ouverte est toujours l'une d'elles. |
| Écrans, murs | Filtrés aux écrans cochés ; un mur est visible si au moins un de ses écrans l'est (un mur est toujours entier : décision 3). Contrôle du direct, simulateur, planning, suivi : mêmes écrans. |
| Listes de lecture, médiathèque | **Par aire** (comme aujourd'hui) : un membre limité à une zone voit les listes et médias de toute l'aire. Avertissement dans l'éditeur de niveau. |
| Publication | Publier une liste touche tous les écrans qui la jouent, même hors périmètre : **autorisé, avec une mention** « diffusée aussi sur N écrans hors de votre périmètre ». |
| Planning | Créneaux des zones et écrans du périmètre seulement (page visible si la case est cochée). |
| Fichiers de dossiers Drive | Sans aire (communs à l'organisation, §14) : visibles si le niveau a accès à l'app de Drive concernée. |


### Conséquences à connaître
- **L'administrateur d'aire disparaît.** Aujourd'hui il gère les membres et les zones de son aire (§14). Désormais ces deux pouvoirs sont à l'administrateur de l'organisation seul. La gestion des **zones** (page Écrans) lui passe donc aussi.
- **Filtre dans le niveau, pas chez le membre** (demande). Deux sites aux droits identiques demandent deux niveaux (« Éditeur Magasin », « Éditeur Atelier »). Variante écartée : niveau pour les droits et périmètre à part, saisi pour chaque membre (moins de niveaux, un champ de plus par invitation).
- Modifier un niveau agit aussitôt sur tous ses membres (droits relus à chaque requête). Un niveau utilisé ne se supprime pas ; la fenêtre liste ses membres à réaffecter.

### Décisions (13 octobre 2026)
1. **Membres toujours visible** pour l'administrateur, même seul : c'est l'entrée pour créer le premier niveau.
2. **Lecture seule : oui.** Une case par niveau : la personne voit tout ce que le niveau donne, sans rien modifier ni publier.
3. **Mur : on accepte le mur entier** dès qu'un de ses écrans est dans le périmètre.
4. **Publication hors périmètre : autorisée, avec une mention** dans l'éditeur de la liste.
5. **Niveaux à la création d'une organisation** : « Administrateur » (toujours là, non modifiable) et « Utilisateur » (Écrans, Listes de lecture, Médiathèque ; ni Planning ni Réglages ; toutes les apps, tous les widgets, toutes les aires).

### Réalisation (codé le 13 octobre 2026)
- **Modèle** (`Rights.cs`, `Models.cs`) : `AccessLevel` (pages, `AllIntegrations` / `AllWidgets` + `Apps`, `ReadOnly`, périmètre `AllAreas` + `AreaIds` + `ZoneIds` + `ScreenIds`) stocké dans `Tenant.Levels` ; `User.LevelId` ; l'ancien `User.AreaRoles` n'est plus lu qu'à la migration. Le périmètre est **à plat** (un élément coché donne tout ce qu'il contient, futurs éléments compris), pas imbriqué comme dans les pistes.
- **Accès** (`Tenancy.cs`) : `AreaAccess.For` calcule le niveau, les aires visibles (`LevelScope`) et un filtre d'écrans ; `ClientDb.Screens` ne rend que les écrans du périmètre. `CanManage` / `ManagesCurrent` = administrateur de l'organisation seul (zones, murs, aires, membres). `CanSee(page)`, `CanUse(app)`, `SeesZone(zone)`, `ReadOnly`.
- **Pages** : politiques `Page:screens|playlists|planning|media|integrations|widgets` (`PageHandler`, niveau relu à chaque navigation) ; Membres, Aires, Réglages, Comptes connectés restent `ClientAdmin`. Un membre refusé est renvoyé vers la première page de son niveau (`AccessDenied`).
- **Apps** (`AppService`) : `Installed()` / `IsInstalled()` ne rendent que les apps du niveau ; `SaveInstance` et `CreateEmpty` refusent une app non autorisée ; activer, désactiver, retirer une app et régler une source sont réservés à l'administrateur. Les pages Intégrations et Widgets d'un membre n'affichent que ses apps, sans interrupteur, sans « Gérer », sans comptes connectés.
- **Lecture seule** : `TenantStore.Write` lève `ReadOnlyLevelException` ; `RightsBoundary` (autour de la page, dans `MainLayout`) affiche « Lecture seule » au lieu de fermer la session ; bandeau en haut et boutons d'en-tête masqués. Le changement d'aire reste permis.
- **Menu** (`MainLayout.razor`) : entrées filtrées par le niveau ; bas du menu *Aires de gestion (dès la 2<sup>e</sup> aire), Membres, ─, Intégrations, Widgets, ─, Réglages*. Réglages perd la tuile et la ligne Membres.
- **Page Membres** : onglets *Membres* (niveau, périmètre) et *Niveaux de droits* (liste, dupliquer, supprimer) ; fenêtre d'édition du niveau avec arbre aires › zones › écrans à cases à trois états (un mur n'a qu'une case) et résumé du périmètre ; formulaire membre à liste unique.
- **Zones filtrées** : les pages Écrans et Planning n'affichent que les zones du périmètre ; `PlanOps` refuse une zone ou un écran hors périmètre.
- **Mention** (`PlaylistEditor`) : « diffusée aussi sur N écrans hors de votre périmètre ».
- **Migration** (`AccessLevels.Migrate`, au démarrage) : niveau « Utilisateur » dans chaque organisation sans niveau ; chaque jeu d'aires des anciens membres devient un niveau « Accès … » avec les quatre pages et toutes les apps (les membres gardent ce qu'ils avaient, planning compris) ; les anciens administrateurs d'aire reçoivent le même niveau et **perdent** la gestion des zones et des membres. Un membre sans aire reste sans niveau (« Aucun niveau »).
- **Suppression** : retirer une aire ou une zone la retire des périmètres (`AccessLevels.Forget`). Console Linkii : la fenêtre des comptes montre le niveau.
- **Tests** : `AccessLevelTests` (cascade, zones, écrans, mur entier, aire ajoutée plus tard, pages, apps, enregistrement et suppression de niveau, zones réservées à l'administrateur, planning, migration, niveau par défaut) et `ReadOnlyLevelTests` ; `AreasTests` et `ZonesTests` adaptés. 5 tests de `AppsTests` échouaient déjà avant ce travail (apps par défaut), sans lien.
- **Vérifié dans le navigateur** (données d'essai) : migration, éditeur de niveau, attribution à un membre, menu et pages d'un membre limité à une zone et un écran, pages refusées redirigées.

### Limites connues
- La lecture seule se joue à l'écriture (`TenantStore.Write`) : un bouton non masqué mène à « Lecture seule » ; seuls les boutons d'en-tête sont cachés. À affiner page par page.
- Un écran appairé par un membre prend la zone par défaut de l'aire : hors de son périmètre si celui-ci est limité à d'autres zones.
- Les listes de lecture et la médiathèque restent partagées par aire (avertissement dans l'éditeur de niveau). Les périmètres d'écrans ne filtrent pas encore l'API d'aperçu ni le contrôle du direct hors de la page Écrans (ils passent par `ClientDb.Screens`, donc filtrés, mais sans test dédié).
- Les maquettes (`droits-pages.html`) numérotent les sections autrement : dans l'appli, Modification est le bloc 3 et Périmètre le bloc 4.

## 47. Mode simple et mode expert (14 octobre 2026) : codé

Demande : simplifier au maximum l'écran pour les personnes qui diffusent seulement. Un interrupteur règle la vue : **simple** (par défaut) ou **expert** (toutes les fonctions). Maquette validée : `maquettes/mode-simple-expert.html`.

### Principe
- **Affichage seulement** : aucune page ni aucun droit n'est retiré. Les niveaux de droits (§46) restent le contrôle d'accès ; le mode ne fait que masquer. Un membre autorisé au planning le retrouve en passant en expert.
- Le mode est enregistré **sur le compte** (`User.ExpertMode`), donc il suit la personne sur tous ses appareils.

### Décisions
1. **Simple** : Écrans (cartes seulement), Listes de lecture, Médiathèque, Réglages réduit à *Utilisation*, *Veille et luminosité*, *Compte et confidentialité*.
2. **Expert** : Planning, Aires de gestion, Membres, Intégrations, Widgets, et le reste de Réglages. Le lien « Gérer les aires » du sélecteur d'aire suit le mode ; le sélecteur reste visible dès deux aires.
3. **Inscription** : choix Simple / Expert dans les deux formulaires, Simple présélectionné. Choisir Expert ne déclenche pas la présentation.
4. **Présentation** au premier passage en expert : fenêtre « Mode expert activé » qui liste les cinq fonctions. Elle s'affiche à **chaque passage en expert**, sauf si « Ne plus afficher ce message » est coché (décoché par défaut). Le choix est mémorisé sur le compte : repasser en simple ne le remet pas à zéro.
5. **Comptes existants** (sans choix enregistré) : expert, pour ne rien retirer à ce qu'ils utilisent.
6. **Libellé de l'interrupteur** : « Passer en mode expert » ou « Passer en mode simple » selon l'état. Le même libellé est repris partout où l'interrupteur apparaît (barre latérale, Réglages › Compte, bloc « Fonctions avancées », page réservée).

### Réalisation (codé le 14 octobre 2026)
- **Modèle** (`Models.cs`) : `User.ExpertMode` (`bool?`, `null` = expert pour un ancien compte), `User.ExpertIntroSeen`, méthode `IsExpert()`.
- **Service** (`ModeService.cs`, enregistré en scoped dans `Program.cs`) : `Expert` relu à chaque demande ; `Switch(bool)` enregistre le mode puis recharge la page (ou ouvre la présentation) ; `DismissIntro(bool)` ferme la présentation. Hors espace client et pendant une consultation par l'équipe Linkii, le mode vaut expert.
- **Barre latérale** (`MainLayout.razor`) : interrupteur au-dessus du compte. Planning, Aires, Membres, Intégrations et Widgets ne sont rendus qu'en expert.
- **Page réservée** : en simple, l'adresse directe d'une de ces pages affiche « Disponible en mode expert » avec un bouton (`MainLayout`, liste `ExpertPaths`).
- **Écrans** (`Screens.razor`) : sélecteur carte/liste masqué en simple, qui reste en cartes.
- **Réglages** (`Settings.razor`) : onglets filtrés (`SimpleTabs`), une ligne « Mode d'utilisation » dans *Compte et confidentialité*, et un bloc « Fonctions avancées » en bas en simple.
- **Inscription** (`Login.razor`, `Signup.cs`) : `Signup.Create` reçoit `expert` (défaut `false`) ; les deux chemins (e-mail, fournisseur externe) le transmettent.
- **Styles** (`app.css`) : pastilles de choix à l'inscription et libellé de l'interrupteur, sans toucher au reste.

### Limites connues
- **Non testé dans le navigateur** : compilation seulement. Le choix à l'inscription (boutons radio en rendu serveur), la présentation et le rechargement après bascule restent à vérifier sur données d'essai.
- Changer de mode recharge la page entière. C'est cohérent avec le changement d'aire, mais coûte un instant.
- Les pages d'intégration accessibles par un lien (par exemple « Comptes connectés » depuis *Utilisation*) passent par la page réservée en simple, sans lien direct vers le mode expert autre que son bouton.

## 48. Zones : menu Zones, page Zones et droit « Gérer les zones » (octobre 2026) : codé

Demande : sortir la gestion des zones de la fenêtre du bouton « Ajouter » des Écrans, et la rendre accessible à qui a le droit. Maquettes validées : `maquettes/zones-page-menu-v2.html` (menu, page, fenêtre de création) et `maquettes/ecrans-entete-ajouter.html` (en-tête des Écrans).

### Décisions
1. **Menu** : entrée **Zones** en mode expert, après « Aires de gestion » et avant « Membres ». Visible à qui a le droit « Gérer les zones » ; « Aires de gestion » et « Membres » restent aux administrateurs.
2. **Droit** : case « Gérer les zones et les murs (dans son périmètre) » dans le niveau de droits (§46). Il porte sur les aires accessibles du membre. Un niveau en lecture seule ne l'exerce pas.
3. **Page `/zones`** : tableau de toutes les aires accessibles (zone, aire de gestion, écrans, type, synchronisation, actions), filtre par aire (« Toutes les aires » par défaut). Pas de liste de lecture.
4. **Fenêtre « Nouvelle zone »** : aire de gestion (préremplie avec le filtre), nom, synchronisation. Pas de choix d'écrans.
5. **Écrans** : le bouton « Ajouter ▾ » devient un simple **Ajouter** qui ouvre « Connecter un écran ». La fenêtre de zones et son menu disparaissent de la page. « Gérer les zones… » (filtre Zone, actions de zone) mène à la page Zones.
6. **Mode simple** : l'adresse `zones` affiche « Disponible en mode expert ».

### Réalisation
- **Droit** (`Rights.cs`) : `AccessLevel.ManageZones` (copié avec le niveau) ; `LevelPages.Zones` (page `zones`, hors cases proposées, donc dans la politique « Page:zones ») ; libellé « Gérer les zones » dans le résumé du niveau.
- **Accès** (`Tenancy.cs`) : `AreaAccess.CanManageZones` (administrateur, ou niveau avec le droit hors lecture seule) ; `CanManage(aire)` = `CanManageZones` et aire accessible, ce qui couvre toutes les opérations de zone et de mur de `AreaOps` ; `ManagesCurrent` suit la même règle ; `CanSee("zones")` = `CanManageZones`.
- **Formulaire de niveau** (`Members.razor`) : la case dans « Pages visibles ».
- **Barre latérale** (`MainLayout.razor`) : bloc d'administration visible si administrateur ou page Zones ouverte ; `zones` ajouté à `ExpertPaths`.
- **Page** (`Components/Pages/Zones.razor`) : reprend la logique de `AreaZones` (renommer, synchroniser, supprimer, ajouter) ; le nombre d'écrans ne compte que ceux du périmètre du membre.
- **Écrans** (`Screens.razor`) : bouton unique, liens « Gérer les zones… » vers la page, fenêtre `showZones` retirée.
- **Tests** (`AccessLevelTests.cs`) : droit accordé dans les aires accessibles seulement ; sans droit ou en lecture seule, rien ; administrateur partout.

### Limites connues
- **Non testé dans le navigateur** : compilation et tests seulement. Les écrans, la fenêtre de création et la bascule de mode restent à vérifier sur données d'essai.
- **Murs** : listés sans menu d'actions (on les règle dans Écrans). La maquette v2 montrait un menu ; à trancher.
- **`AreaZones.razor`** reste utilisé par la page Aires de gestion. La page Écrans ne l'utilise plus.
- **Tests** : 5 échecs dans `AppsTests.cs` (fichier avec des modifications non commitées, hors de ce chantier). Non vérifiés contre la base de départ.

## 49. Dossiers dans la médiathèque et droits par dossier : analyse, maquette et réalisation (14 octobre 2026) : codé

Demande : chaque utilisateur crée des dossiers dans la médiathèque et les partage avec d'autres membres, en **écriture** ou en **lecture seule** ; l'administrateur voit tous les dossiers en écriture ; les droits se donnent aussi dans l'onglet **Niveaux de droits**. Maquette : `maquettes/mediatheque-dossiers.html` (principe et règles, médiathèque d'un membre, fenêtre de partage, « Partagés avec moi », vue administrateur, éditeur de niveau, listes de lecture). **Rien n'est codé.**

### Point de départ (code actuel)
- Un `MediaItem` appartient à une **aire** (`AreaId`) ; il est visible de tout membre dont le périmètre contient l'aire (`AreaAccess.For`, §46). Les fichiers des Drives ont `AreaId` vide et `Info["drive"]` : communs, ni déplaçables ni supprimables.
- Aucune notion de propriétaire sur un fichier, ni de dossier. Le droit « Médiathèque » est une simple case de page ; « lecture seule » est un drapeau du niveau.
- Les listes de lecture et les apps référencent les fichiers par `MediaId` ; `AreaOps.MoveMedia` refuse déjà de déplacer un fichier utilisé ailleurs.

### Principe proposé
1. **Médiathèque de l'aire = racine, inchangée** : fichiers communs de l'aire. Qui ne crée pas de dossier ne voit aucune différence.
2. **Dossier** : nom, aire, dossier parent (arborescence), **propriétaire** (le créateur), partages. **Privé** par défaut.
3. **Partage** : une personne **ou un niveau de droits**, mode **Lecture** ou **Écriture**. Il **descend** dans les sous-dossiers ; on peut ajouter un partage sur un sous-dossier, pas en retirer un hérité.
4. **Droits effectifs** d'un membre sur un dossier : administrateur = écriture + gestion ; propriétaire = écriture + gestion ; sinon le **plus fort** des partages (personne, niveau, dossiers parents). Puis **plafond** : niveau en lecture seule ⇒ lecture au plus.
5. **Écriture** : téléverser, renommer, déplacer, supprimer des fichiers, créer des sous-dossiers. **Lecture** : voir, télécharger, utiliser dans une liste. **Propriétaire / administrateur** en plus : renommer et supprimer le dossier, gérer le partage, changer de propriétaire (administrateur seul).
6. **Un dossier à la fois** pour un fichier ; déplacement par glisser-déposer, menu ⋮ et sélection multiple. Un fichier de la racine peut rester à la racine.
7. **Niveaux de droits** : nouvelle section « Dossiers de la médiathèque » : « Peut créer ses dossiers », « Peut les partager », et l'accès à des dossiers existants (Aucun / choisis en Lecture ou Écriture / Tous en lecture / Tous en écriture). Même donnée que le partage « niveau » d'un dossier, présentée de l'autre côté. « Tous » couvre les futurs dossiers partagés, **jamais** les dossiers privés d'un membre.
8. **Fonction optionnelle** (mémoire « générique et optionnel ») : réservée au mode expert, interrupteur dans Réglages « Dossiers dans la médiathèque », éteint par défaut.

### Modèle de données (esquisse)
- `MediaFolder { Id, ClientId, AreaId, ParentId?, Name, OwnerId, CreatedUtc, List<FolderShare> Shares }` ; `FolderShare { Kind: user | level, TargetId, Mode: read | write }`. Stocké dans la base d'organisation, comme `Media`.
- `MediaItem.FolderId?` (null = racine de l'aire).
- `AccessLevel` : `CanCreateFolders`, `CanShareFolders`, `AllFolders` (null | read | write). Les accès à des dossiers précis vivent dans les `Shares` du dossier (une seule source de vérité) ; l'éditeur de niveau les lit et les écrit.
- Calcul centralisé `FolderAccess.For(user, folder)` → `None | Read | Write | Owner`, à côté de `AreaAccess` ; la médiathèque, le sélecteur de média et le téléversement passent tous par lui.

### Écrans (voir la maquette)
- **Médiathèque** : colonne d'arbre à gauche (Médiathèque de l'aire, Mes dossiers, Partagés avec moi, Sources Drive), fil d'Ariane avec badges (Propriétaire, Partagé avec N), sous-dossiers puis fichiers, bouton « Nouveau dossier », « Partager… ».
- **Partager** : recherche de personnes et de niveaux (membres sans l'aire ou sans la page Médiathèque grisés avec la raison), liste des accès, sélecteur Lecture / Écriture.
- **Partagé avec moi** : bandeau « lecture seule », cadenas sur les fichiers, boutons d'écriture grisés.
- **Administrateur** : « Tous les dossiers » par propriétaire, tableau (propriétaire, aire, partagé avec, fichiers), menu : changer de propriétaire, déplacer vers une autre aire, supprimer.
- **Niveau de droits** : 4<sup>e</sup> section de l'éditeur.
- **Listes de lecture** : le sélecteur reprend l'arbre ; un fichier devenu inaccessible reste dans la liste, signalé.

### Cas limites et règles retenues
- **Fichier utilisé** : reste diffusé même si le dossier n'est plus partagé ; la liste signale « dossier non accessible » (nom et durée visibles, pas d'aperçu). Supprimer un dossier utilisé : message avec le nombre de listes, puis confirmation ou refus (comme `MoveMedia`).
- **Retirer un partage** : avertissement si l'autre personne utilise des fichiers du dossier.
- **Membre désactivé** : ses dossiers passent à l'administrateur qui agit ; partages conservés. **Niveau supprimé** : ses partages de dossier tombent (nombre annoncé). **Changement de niveau d'un membre** : ses accès « niveau » suivent le nouveau niveau.
- **Aire** : un dossier est dans une aire ; on ne le partage qu'avec les membres qui voient l'aire. Déplacer un dossier vers une autre aire = l'administrateur seul (réutilise `AreaMoveDialog`), avec les mêmes refus si des fichiers servent ailleurs.
- **Drives** : fichiers synchronisés hors dossiers, listés sous « Sources Drive » (pas de dossier virtuel dans cette première version).
- **Publication** : la règle « publication hors périmètre autorisée avec mention » (§46) s'applique telle quelle ; le droit de lecture sur le dossier suffit pour publier ses fichiers.
- **Téléverser dans un dossier en écriture seulement** ; les imports en cours gardent leur dossier cible même si l'on navigue ailleurs.

### Décisions à trancher avant de coder
1. **Profondeur** : arborescence libre (recommandé, affichage plafonné à 4 niveaux) ou un seul niveau de sous-dossiers.
2. **Partage entre aires** : recommandé = limité aux membres qui voient l'aire du dossier. Alternative : un partage ouvre le dossier même hors périmètre (« Partagés avec moi » toutes aires), mais les listes de lecture étant propres à une aire, cela crée des fichiers de l'aire A dans des listes de l'aire B.
3. **Écriture et gestion** : recommandé = l'écriture ne permet ni de partager ni de supprimer le dossier. Alternative : un troisième mode « Gestionnaire ».
4. **Fichiers existants** : restent à la racine (recommandé), ou création automatique d'un dossier « Importés » par aire.
5. **Racine commune** : garder les fichiers de l'aire visibles de tous (recommandé), ou rendre la racine propre à chacun (rupture avec l'existant).
6. **Mode simple** : fonction masquée (recommandé) ou « Mes dossiers » seulement, sans partage.

### Réalisation envisagée (par étapes)
1. Modèle et `FolderAccess` + tests (cascade, plafond lecture seule, niveau supprimé, membre désactivé).
2. Médiathèque : arbre, création, renommage, déplacement, téléversement dans un dossier.
3. Partage (fenêtre) et « Partagés avec moi » ; vue administrateur.
4. Section « Dossiers » du niveau de droits.
5. Sélecteur de média des listes et signalement des fichiers inaccessibles ; réglage « Dossiers dans la médiathèque ».
6. Migration : aucune donnée à convertir (`FolderId` nul = racine).

### Limites connues
- Maquette non validée par l'utilisateur ; les six décisions ci-dessus sont ouvertes.
- Pas de corbeille : supprimer un dossier supprime ses fichiers après confirmation (comportement actuel des fichiers).
- Pas de lien de partage externe, pas de notification au destinataire d'un partage (à discuter).

### Réalisation (14 octobre 2026) : codé

Suit les recommandations ci-dessus. Option éteinte par défaut, mode expert seulement.

- **Option** : `Tenant.FoldersOn`, interrupteur dans Réglages › Médiathèque (enregistré aussitôt). **Données** : `Db.MediaFolders` (dossiers, avec leurs `Shares`), `MediaItem.FolderId` (null : racine de l'aire), champs du niveau `CanCreateFolders`, `CanShareFolders`, `AllFolders`.
- **Droits** (`Folders.cs`, `FolderAccess`) : administrateur = propriétaire partout ; propriétaire de son dossier ; sinon le plus fort des partages qui couvrent le dossier (le dossier ou un de ses parents, à la personne ou à son niveau) et « tous les dossiers partagés » du niveau (jamais un dossier privé d'un autre) ; plafond à la lecture pour un niveau en lecture seule ; profondeur 4.
- **Opérations** (`FolderOps`) : créer, renommer, supprimer (avec sous-dossiers, refusé si un sous-dossier est à un autre), partager, donner des dossiers à un niveau, changer de propriétaire (logique seulement), ranger des fichiers, éditer un fichier. Un fichier qui quitte l'aire le quitte aussi (`AreaOps.MoveMedia`).
- **Médiathèque** (`Components/Pages/Media.razor`) : fil d'Ariane, tuiles de dossiers, « Nouveau dossier », « Partager… » (`FolderShareDialog.razor`), « Ranger dans un dossier… » (`FolderMoveDialog.razor`), téléversement dans le dossier ouvert (`MediaUploader`, contrôlé par `FolderOps.CanUploadTo`). Bandeau en lecture seule quand le dossier est partagé en lecture.
- **Niveaux** (`Components/Pages/Members.razor`) : section « Dossiers de la médiathèque » dans l'éditeur (créer, partager, Aucun / Dossiers choisis / Tous en lecture / Tous en écriture, puis le choix par dossier, grisé hors du périmètre du niveau). Supprimer un niveau retire ses partages de dossiers.
- **Membre retiré** : ses dossiers passent à la personne qui agit (`ClientDb.RemoveUser`).
- **Listes de lecture** : `MediaLibrary.Files()` ne donne que les fichiers visibles. `MediaField` garde à l'enregistrement les fichiers rangés dans un dossier que la personne ne voit pas : sans cela, ils disparaîtraient du contenu.
- **Sélection dans la médiathèque** : sur un fichier qu'on peut modifier, un simple appui sur toute la carte la coche ou la décoche (ClickCard, Media.razor). La case et le menu ⋮ n'y participent pas. Les fichiers Drive et ceux d'un dossier en lecture seule ne se sélectionnent pas.
- **Tests** : `Linkii.Poc.Tests/FolderAccessTests.cs` (22 tests : droits, partages, niveaux, lecture seule, création, suppression, rangement). Suite complète : 417 réussis, 5 échecs dans `AppsTests`, déjà signalés avant ce chantier.

**Décisions prises sur les six points ouverts** : 1 arborescence libre, quatre niveaux ; 2 partage limité aux membres qui voient l'aire du dossier ; 3 pas de mode « gestionnaire » (l'écriture ne partage ni ne supprime le dossier) ; 4 les fichiers existants restent à la racine ; 5 la racine reste commune ; 6 fonction masquée en mode simple.

**Écarts avec la maquette et limites** :
- Pas de vue « Tous les dossiers » de l'administrateur, ni de tableau. Il voit les dossiers de l'aire ouverte, comme ses fichiers ; le propriétaire est affiché sur chaque tuile.
- Pas d'arborescence « Mes dossiers / Partagés avec moi » : une seule liste à la racine, avec les dossiers vus (les miens et ceux partagés avec moi).
- Pas d'avertissement quand on retire un partage : la diffusion continue, sans message.
- Le sélecteur de média des listes reste à plat (fichiers accessibles seulement), sans arborescence.
- Changement de propriétaire : la logique existe (`FolderOps.TransferOwner`), sans interface.
- Membre seulement désactivé : ses dossiers gardent leur propriétaire (l'administrateur les voit et peut les partager).
- Option éteinte : la médiathèque revient à plat ; les droits sur les dossiers restent en vigueur.
- Mode simple : aucun dossier affiché ; les fichiers rangés restent listés à plat selon les droits.
- **Non testé dans le navigateur** : compilation et tests seulement. L'affichage, les menus et les dialogues restent à vérifier sur des données d'essai.

## Plans et prochaines étapes

- Gestion des droits : essayer avec de vrais membres (niveau en lecture seule page par page, appairage d'un écran par un membre limité à une zone, aperçu et direct) ; voir « Limites connues » du §46.
- Publicité rémunérée : maquette `maquettes/publicite.html` à valider avant de coder (§45).
- Comptage des scans de QR code : essai sur un vrai écran et un vrai téléphone, derrière le nom de domaine public ; page de résultats détaillés, comptage par écran (§41).
- Google Slides, Google Docs et Microsoft PowerPoint : essai avec de vrais comptes, conversion de vrais fichiers, mise à jour automatique (§39, §40).

- Mise en veille et luminosité : tester sur une tablette et une Android TV (extinction réelle, réveil, luminosité) ; voir §33 et §34.

- Android : tester l'app 1.1.0 (aperçu du direct, redémarrage, version remontée) sur un vrai boîtier ; PIN du menu technicien à définir depuis le back-office ; icône définitive.
- Valider un vrai Drive pour le bandeau d'import (§ 21) et la conversion 1080p sur serveur Linux.
- Supervision : statistiques de diffusion annoncées avec Growth, non codées.
- Reconnexion : tester l'arrêt/redémarrage du site en conditions réelles.
- Vignettes de vidéos : vérifier l'extraction ffmpeg sur le serveur (§ 28). Drives Nextcloud, SFTP, Dropbox : essai sur de vrais serveurs (§ 26). PDF : essai sur le serveur et avec de vrais documents (§ 29).
## LinkedIn : mise en service (source « Page entreprise »)

Le mode « Publications choisies » ne demande aucune configuration. Le mode « Page entreprise » utilise l API officielle de LinkedIn :

1. **Une seule fois, pour la plateforme** : créer une application sur <https://www.linkedin.com/developers/apps>, la rattacher à une page entreprise, puis demander le produit **Community Management API** (approbation par LinkedIn, délai incertain). Les permissions utilisées sont `r_organization_social` (lire les publications) et `r_organization_admin` (lister les pages administrées).
2. Déclarer dans le portail l adresse de retour **https** `https://<domaine de l espace>/linkedin/callback` (une par domaine de revendeur utilisé).
3. Renseigner la configuration du serveur (variables d environnement) :

| Clé | Rôle |
|---|---|
| `Linkedin__ClientId`, `Linkedin__ClientSecret` | identifiants de l application LinkedIn (obligatoires) |
| `Linkedin__RedirectUri` | facultatif : impose l adresse de retour (sinon celle de la requête) |
| `Linkedin__ApiVersion` | en-tête `LinkedIn-Version` (AAAAMM, défaut `202609`) ; LinkedIn retire les anciennes versions |
| `Linkedin__Scopes` | facultatif, défaut `r_organization_social r_organization_admin` |

4. Dans le back-office : Apps > LinkedIn > Source « Page entreprise » > **Se connecter avec LinkedIn** (compte administrateur de la page) > choisir la page > cocher les publications à diffuser > enregistrer, puis publier la playlist.

Points à connaître :

- **Jeton de 60 jours** : le renouvellement automatique est réservé à certains partenaires de LinkedIn. Il faut donc se reconnecter avant l échéance (rappel dans le panneau et dans « Tester »). Se reconnecter met à jour le jeton des playlists déjà publiées, sans republier.
- **Secrets** : le jeton est chiffré (SecretBox) dans l instance et dans son instantané publié ; il ne part jamais au player. Le player reçoit seulement texte, date, image et adresse du QR via `/api/data/{id}` ; il garde la dernière version hors ligne.
- **Images** : LinkedIn exige parfois des droits supplémentaires pour lire l image d une publication ; sans eux, la publication s affiche sans image. Les adresses d image expirent, le serveur en renvoie une fraîche à chaque lecture (cache de 10 minutes).
- **Non vérifié contre le vrai LinkedIn** : le code suit la documentation officielle (Posts API, Organization Access Control, OAuth 3-legged) et est testé sur des réponses simulées ; la première connexion réelle demande l approbation de l application par LinkedIn.

### Widgets d'écran (horloge, météo)
Horloge et Météo ne sont plus des apps de playlist (`"screenWidget": true` dans leur manifeste : absents du panneau Apps ; activés par défaut et gérés dans la page **Widgets** : un widget désactivé n’est plus proposé dans l’éditeur, ceux déjà posés restent publiés). Texte libre suit le même modèle. Page Écrans, menu ⋮ › **Éditer** (ou clic sur la carte) : `ScreenEditor`, onglets **Écran** (nom, playlist, format) et **Widgets**, sur un aperçu de l’écran à l’échelle ( glisser-déposer et flèches du clavier via `wwwroot/widget-canvas.js`), taille (Petite → Très grande) et réglages du manifeste. Stockés dans `Screen.Widgets` (X / Y en % de l'écran, `Scale`), publiés avec l'écran comme `PublishedItem` de position `free` (`x`, `y`, `scale`) ; le player les place dans `#overlays` sans réduire la zone de contenu. Les contenus horloge / météo déjà présents dans des playlists continuent d'être diffusés.

### Médiathèque et apps de médias
La médiathèque ne contient plus que des **images et des vidéos** (`MediaLibrary`). Téléversement depuis l'ordinateur (bouton ou glisser-déposer) ou **par URL** (`MediaImporter.ImportUrl`, téléchargement côté serveur via `SafeHttp.Download` : jamais le réseau interne, 2 Go max), avec une **barre de progression par fichier** (`MediaUploader`, réutilisé dans le sélecteur des apps). Taille et date d'ajout enregistrées (`MediaItem.Size`, `AddedUtc`). Menu ⋮ : Renommer, Télécharger, Supprimer ; sélection multiple pour supprimer. Supprimer un fichier le retire des apps qui l'utilisaient (playlists touchées → « À publier »).

Quatre apps prennent leurs fichiers dans la médiathèque : **Image**, **Vidéo**, **Diaporama images**, **Diaporama vidéos** (`Apps/image.json`, `video.json`, `slideshow-images.json`, `slideshow-videos.json`). Nouveaux types de champ `media` / `medias` (attribut `accept` : image | video ; valeur = identifiants séparés par des virgules) rendus par `MediaField` + `MediaPicker` (on peut téléverser depuis le sélecteur). À la publication, `Notifier` résout les fichiers en adresses `/media/…` (`PublishedItem.Urls`) ; le player les précharge pour la lecture hors ligne et les lit via `host.media(url)` (`apps.js`). Vidéo et diaporamas sont en durée « content » : contenu suivant à la fin. Les playlists n'ajoutent plus de fichier directement (les anciens éléments restent diffusés) ; un contenu d'app retiré de sa dernière playlist est supprimé.

## 50. Dossiers des listes de lecture, glisser-déposer et « Ajouter un dossier » (14 octobre 2026) : codé

Demande : les listes de lecture se rangent dans des dossiers comme les fichiers de la médiathèque (§49), avec les mêmes droits ; un ou plusieurs éléments se déplacent par glisser-déposer ; la fenêtre « Ranger dans un dossier » permet de créer le dossier de destination. **Non testé dans le navigateur à la date de rédaction** (l'appli tournait sur une version antérieure).

### Modèle
- `PlaylistFolder { Id, ClientId, AreaId, ParentId?, Name, OwnerId, CreatedUtc, Shares }`, stocké dans `Db.PlaylistFolders` ; accès aire par `ClientDb.PlaylistFolders`.
- `Playlist.FolderId?` (null = racine de l'aire, comme avant).
- `IFolderNode` : interface commune aux dossiers de la médiathèque et des listes. `FolderAccess` est générique (`For<T>`, `Chain<T>`, `Snapshot<T>`, `CanShare<T>`) : une seule logique de droits.
- Les partages de niveau sont une seule donnée pour les deux types (`FolderOps.SetLevelShares` écrit les deux). Le niveau supprimé retire ses partages des deux types.

### Règles
- **Droit sur une liste** (`PlaylistOps.RightOf`) : celui de son dossier ; à la racine, comme avant : visible de tous, modifiable, sauf niveau en lecture seule (plafond Read).
- **Visibilité** : une liste rangée dans un dossier qu'on ne voit pas n'apparaît pas sur la page Listes.
- **Partage personne** : la personne doit avoir la page « Listes de lecture ». Partage niveau : aucune condition supplémentaire.
- **Rangement** (`PlaylistFolderOps.Move`) : écriture sur le dossier d'arrivée, et sur le dossier d'origine si la liste vient d'un dossier ; vers la racine, il faut pouvoir créer à la racine. Ranger ne touche pas au contenu (pas de mise à jour à publier).
- **Suppression d'un dossier** : propriétaire seul, refusé si un sous-dossier appartient à quelqu'un d'autre ; les listes du dossier sont retirées par `PlaylistOps.Remove` (même logique que la suppression d'une liste : écrans gardant leur instantané).
- **Éditeur de liste** (`PlaylistEditor`) : les écritures passent par `WriteList`, qui refuse côté serveur une liste dans un dossier non modifiable ; bandeau « lecture seule » à l'écran.
- **Changement d'aire** : la liste arrive à la racine de la nouvelle aire (`AreaOps.MovePlaylist`).
- **Propriétaire retiré** : ses dossiers de listes passent à la personne qui agit (`ClientDb.RemoveUser`).

### Écrans
- **Page Listes** (`Playlists.razor`) : fil d'Ariane, dossiers de la position ouverte, puis la barre de filtres (même disposition que la Médiathèque). Recherche ou filtre : toutes les listes vues, à plat.
- **Glisser-déposer** : une carte (ou toute la sélection si la carte en fait partie) vers une tuile de dossier, ou vers un élément du fil d'Ariane (racine comprise). La cible s'éclaire (`.drop-target`). Côté Blazor, le dépôt exige un gestionnaire explicite sur `dragover` (`@ondragover` avec `preventDefault`), sinon le navigateur refuse le dépôt.
- **Ranger dans un dossier** (`PlaylistMoveDialog`, `FolderMoveDialog`) : lien « Ajouter un dossier » : le nouveau dossier se crée dans l'emplacement choisi (ou à la racine) et est sélectionné.
- **Partage** : `PlaylistFolderShareDialog` (copie de `FolderShareDialog`, appliquée aux listes).

### Fichiers
- Nouveaux : `PlaylistFolders.cs` (`PlaylistOps`, `PlaylistFolderOps`), `PlaylistLibrary.cs` (service et enregistrements), `Components/PlaylistFolderShareDialog.razor`, `Components/PlaylistMoveDialog.razor`, `Linkii.Poc.Tests/PlaylistFolderTests.cs`.
- Modifiés : `Models.cs`, `Folders.cs`, `Tenancy.cs`, `Rights.cs`, `Areas.cs`, `Program.cs`, `Components/Pages/Playlists.razor`, `Components/Pages/PlaylistEditor.razor`, `Components/Pages/Members.razor`, `Components/Pages/Media.razor`, `Components/FolderMoveDialog.razor`, `wwwroot/app.css`.

### Tests
- `PlaylistFolderTests` : 11 tests (droits, partage, lecture seule, niveau, rangement, suppression). Ils passent.
- Suite complète : 5 échecs sur `AppsTests`, présents aussi sur le commit HEAD (fa32ec7) ; cause non identifiée (attentes des tests contre le catalogue).

### Limites connues
- Les sélecteurs de listes (planning, écrans) ne filtrent pas encore par droits, comme pour la médiathèque.
- La fenêtre de partage des listes n'est pas testée dans le navigateur.
- Le glisser-déposer et la barre de filtres sont à valider à l'écran.

## 51. Listes de lecture et médiathèque épurées (15 octobre 2026) : codé

Demande : alléger les pages `/playlists` et `/media`, et corriger au passage les dysfonctionnements et règles métier manquantes. Maquette validée : `maquettes/listes-mediatheque-epurees.html` (7 onglets, dont « Corrections et règles »). Vérifié dans le navigateur sur une instance de test isolée (cartes, liste, dossier, recherche, sélection, suppression, aperçu, dépôt de fichier, mobile).

### Même squelette sur les deux pages
- **Titre = fil d'Ariane** (`.lx-top`) : « Listes de lecture / Événements », chaque niveau reste une cible de glisser-déposer. Une **pastille de droits** remplace le bandeau « lecture seule » et la ligne « Votre dossier · partagé » (« Lecture seule · dossier de Marie », « Votre dossier · privé »…).
- **Ligne d'état** (`.lx-status`) : comptes, choses à faire (« 1 liste a des modifications non publiées · Voir », « 2 médias non utilisés · Voir »), conseil. Plus de phrase d'explication sous le titre : elle passe dans l'état vide.
- **Une ligne d'outils** (`.lx-tools`) : recherche (touche `/`), filtres en puces, puce de portée « Dans : toute l'aire / ce dossier » (dossier ouvert + recherche ou filtre), tri, bascule cartes / liste.
- **Dossiers en puces** (`.lx-fchip`) au lieu de tuiles hautes ; menu ⋮ (partager, renommer, supprimer) ; icône partagé / lecture seule.
- **Barre de sélection flottante** (`.lx-selbar`) en bas, au lieu de boutons qui s'ajoutent à la barre d'outils. `Échap` désélectionne.
- **Vue liste = vrai tableau** (`.lx-tbl`), colonnes triables (nom, diffusion / taille, date). Sur mobile, toujours en cartes (la bascule y est masquée) : le tableau est caché et les cartes affichées (`.lx-mobile-only`).
- Clavier et dépôt de fichiers : `wwwroot/library.js` (`linkiiLibrary.attach/setDrop/detach`), appelé par les deux pages.

### Listes de lecture
- Un seul bouton **« Nouvelle liste ▾ »** (le menu propose « Nouveau dossier ») ; la carte « Créer une liste » en fin de grille disparaît.
- **Carte** : nom, une ligne de méta (« 4 contenus · 0:32 · modifiée il y a 2 h »), **un seul état** (le plus utile d'abord) : *À publier sur N écrans · Publier…* → *N contenus à compléter* → *Diffusée sur N écrans* → *Programmée · Lun – Ven · 08:00 – 12:00* → *Vide* → *Non utilisée*. Case de sélection et bouton « Essayer » au survol ; cadenas sur une liste en lecture seule. Durée et « Modifiée » ne sont plus affichées deux fois.
- **Filtres** : Toutes · Utilisées · À publier (s'il y en a) · Non utilisées. En recherche ou filtre dans toute l'aire, l'emplacement s'affiche sous chaque carte (colonne « Emplacement » en liste).
- **Sélection** : Ranger…, Autre aire…, **Dupliquer** (nouveau, en masse), Supprimer.

### Médiathèque
- **Vignettes « galerie »** (`.lx-mc`) : image d'abord, nom et une ligne dessous (« 1920×1080 · 2,4 Mo »). Étiquettes discrètes : source (Drive, OneDrive, Canva, PDF), durée d'une vidéo, « Non utilisé ».
- **Clic = aperçu** (`MediaPreviewPanel`, panneau latéral) : grand aperçu (lecteur pour une vidéo), type, définition, durée, poids, emplacement, date, **où il est utilisé** (listes avec nombre d'écrans, contenus avec nombre de listes, liens « Ouvrir »), actions (télécharger, renommer, ranger, autre aire, supprimer). `←` `→` passent au média voisin, `Échap` ferme. Pendant une sélection, ou avec Ctrl/Maj, le clic coche.
- **Import** : bouton « Importer » (fenêtre fichiers ou adresse web, qui se ferme dès qu'un import démarre) et **dépôt n'importe où sur la page** : un voile apparaît pendant le glisser de fichiers et nomme le dossier de destination. Le champ du voile reste dans la page (caché) : le fichier déposé se lit depuis ce champ (`MediaUploader.AddFiles`). Un glisser interne (vers un dossier) n'affiche pas le voile.
- Le bandeau « exports Canva inutiles · Nettoyer » disparaît : c'est un cas du filtre **Non utilisés**, qui vaut pour tous les fichiers.
- Fichiers Drive : plus de case à cocher grisée ; l'explication est dans l'aperçu.
- Import en échec : « Retirer » directement sur la vignette.

### Corrections et règles
- **Bug** : supprimer une liste retirait ses **créneaux de planning** sans prévenir. La confirmation (`DeleteImpactBox`) liste les écrans qui la diffusent et chaque créneau retiré (« zone « Rez » · Lun – Ven · 08:00 – 12:00 ») ; idem pour la suppression d'un dossier de listes.
- **Bug** : « Supprimer » en sélection ne faisait rien si les listes étaient en lecture seule. Le bouton dit « Supprimer 2 (1 en lecture seule) », se grise s'il n'y a rien à supprimer, et la confirmation nomme les listes ignorées.
- **Incohérence** : « utilisée » ne voulait pas dire la même chose partout (le filtre comptait les plannings, la pastille et le tri non). Une seule règle (`PlaylistUse.Used`) : choisie sur un écran **ou** programmée.
- **Règle** : état **« À publier »** d'une liste modifiée depuis sa publication (ou choisie mais jamais publiée) : lu dans l'empreinte de publication de chaque écran (`PlaylistUses.PublishedWith`, numéros de révision comparés en entier) et dans les révisions du planning (`PublishedListRevs`).
- **Règle** : avertissement (non bloquant) quand on crée ou renomme une liste avec le nom d'une autre liste de l'aire.
- **Incohérence** : la recherche de la médiathèque ne regardait que le dossier ouvert ; elle regarde toute l'aire, comme celle des listes, avec le chemin sous chaque résultat. Les types et sources filtrent ce qu'on voit.
- **Libellé** : la confirmation de suppression d'un média ne parle plus d'« app » (« dans aucune liste de lecture ni aucun contenu »).
- La copie d'une liste en lecture seule part à la racine : le message le dit.

### Code
- `PlaylistLibrary.cs` : `PlaylistUse`, `PlaylistDeleteImpact`, `PlaylistUses.Of / PublishedWith / Impact`.
- `PlaylistFolders.cs` : `PlaylistOps.Copy` (duplication, sortie de la page).
- `MediaLibrary.cs` : `UnusedIds()`, `Uses(id)` → `MediaUses` / `MediaUse` ; `UnusedExports(..., key: null)` = tous les fichiers.
- Nouveaux : `Components/MediaPreviewPanel.razor`, `Components/DeleteImpactBox.razor`, `wwwroot/library.js`, `Linkii.Poc.Tests/LibraryPagesTests.cs`.
- Réécrits : `Components/Pages/Playlists.razor`, `Components/Pages/Media.razor`. Modifiés : `MediaUploader.razor` (`AddFiles`), `App.razor` (script), `app.css` (bloc `lx-*` en fin de fichier).

### Tests
- `LibraryPagesTests` : 11 tests (diffusion, à publier, découpage en zones, planning, impact d'une suppression, duplication, médias sans emploi). Ils passent.
- Suite complète : toujours les 5 échecs connus de `AppsTests` (§50), sans lien.

### Reste à faire (priorité 2, maquette onglet 7)
- **Remplacer le fichier** d'un média en gardant ses utilisations (écrans « à publier »).
- **Doublons à l'import** (même contenu) : utiliser l'existant / remplacer / garder les deux.
- **Réessayer** un import en échec (aujourd'hui : « Retirer » seulement).
- **Ajouter à une liste…** depuis l'aperçu d'un média.
- Ne pas proposer une liste vide dans le choix d'un écran.
- Sélection en plage (Maj + clic) et `Suppr` pour supprimer la sélection.

## 52. Visibilité des dossiers par les niveaux de droits : médiathèque et listes de lecture (15 octobre 2026) : corrigé

Signalement : la visibilité des dossiers réglée dans un niveau de droits ne marchait pas pour la médiathèque, et n'existait pas pour les listes de lecture. Reproduit sur une instance de test (un administrateur crée « Affiches » et « Événements », règle le niveau sur « Tous, en lecture », un membre se connecte) : le membre ne voyait aucun des deux dossiers.

### Causes
- **« Tous » ne donnait que les dossiers déjà partagés avec quelqu'un.** Un dossier créé par un administrateur, jamais partagé, passait pour « privé » et n'était donné à personne : le cas le plus courant.
- La section « Dossiers » de l'éditeur de niveau n'apparaissait que s'il existait déjà un dossier : impossible de cocher « Peut créer ses dossiers » ou « Tous » dans une organisation qui n'en a pas encore.
- Les choix dossier par dossier restaient enregistrés en « Aucun » ou « Tous » : « Aucun » laissait donc l'accès.
- Listes de lecture : les sélecteurs des écrans, de l'éditeur d'écran, des murs et du planning proposaient **toutes** les listes, y compris celles rangées dans un dossier invisible ; l'éditeur de liste s'ouvrait par son adresse.

### Règles
- **« Tous, en lecture / en écriture »** donne les dossiers *de l'organisation* : ceux créés par un administrateur (ou dont le propriétaire a été retiré) et ceux qu'un membre a partagés, y compris ceux créés plus tard. Jamais le dossier privé d'un autre membre. Un sous-dossier suit son dossier ; un niveau en lecture seule plafonne à la lecture ; le périmètre (aires) s'applique toujours. Mêmes règles pour les deux sortes de dossiers (`FolderAccess.For`, qui reçoit les membres de l'organisation par `AreaAccess.MemberIds`).
- **« Dossiers choisis »** : accès dossier par dossier (partage de niveau). **« Aucun »** et **« Tous »** retirent les choix dossier par dossier à l'enregistrement.
- **Listes proposées** (`PlaylistOps.Choosable`) : celles que la personne voit, plus celles déjà en place sur un écran, une zone, un mur ou un créneau qu'elle voit (le choix actuel reste affiché). Appliqué à la page Écrans, à l'éditeur d'écran, aux murs et au planning. Une liste d'un dossier invisible ouverte par son adresse est « introuvable ».

### Écran (Membres › niveau › Dossiers)
- Section toujours présente quand l'option Dossiers est active.
- Une phrase explique le mode choisi.
- En « Dossiers choisis » seulement : la liste des dossiers, séparée en **Médiathèque** et **Listes de lecture**, avec le chemin (« Affiches › Été ») et l'accès hérité d'un dossier parent.

### Fichiers
- `Folders.cs`, `Tenancy.cs` (`AreaAccess.MemberIds`), `PlaylistFolders.cs` (`Choosable`), `Components/Pages/Members.razor`, `Components/Pages/Screens.razor`, `Components/ScreenEditor.razor`, `Components/WallDialog.razor`, `Components/Pages/PlanningPage.razor`, `Components/Pages/PlaylistEditor.razor`.
- Tests : 1 dans `FolderAccessTests`, 6 dans `PlaylistFolderTests`. Ils passent ; toujours les 5 échecs connus de `AppsTests`.
- Vérifié dans le navigateur (instance de test) : « Tous, en lecture » fait apparaître les deux dossiers au membre, en lecture seule ; « Dossiers choisis » enregistre un seul dossier ; « Aucun » retire l'accès.

### Limite
- Le serveur ne refuse pas encore qu'un membre choisisse, par une requête forgée, une liste qu'il ne voit pas : seuls les sélecteurs filtrent.

## 53. Icône Partage et nombre de personnes sur les dossiers (15 octobre 2026) : codé

Demande : sur les dossiers de la médiathèque et des listes de lecture, une icône Partage avec le nombre de personnes avec qui le dossier est partagé. Maquette validée : `maquettes/dossiers-icone-partage.html` (puces, infobulle, titre et fenêtre de partage, règles de comptage). Décisions : un niveau compte ses membres ; la pastille d'un dossier d'un autre, en lecture seule, n'est pas cliquable.

### Comportement
- **Puce de dossier** : après l'effectif, une pastille picto + nombre (`SharePill`). Gris : personnes ; **bleu** : au moins un partage par niveau ; **ambre + cadenas** : dossier d'un autre en lecture seule. Dossier privé (ou partage sans personne concernée) : rien.
- **Clic** : ouvre la fenêtre de partage, seulement pour le propriétaire et l'administrateur (même règle que le menu ⋮) ; sinon la pastille est un simple repère (focus clavier compris) qui ne fait qu'afficher l'infobulle.
- **Infobulle** (survol ou focus) : propriétaire (si ce n'est pas soi), personnes choisies avec leur droit (8 au plus, puis « et N autres »), niveaux avec leur nombre de membres, et une note quand on ne gère pas le partage.
- **Titre d'un dossier ouvert** : « 3 personnes », même pastille, même clic. La pastille « Votre dossier · partagé » devient « Votre dossier » (privé : « Votre dossier · privé »).
- **Fenêtre de partage** (médiathèque et listes) : ligne de total « 5 personnes y ont accès : 2 choisies une à une, 3 par le niveau « Éditeur » », ou un message si personne n'est concerné.

### Règle de comptage (`FolderAudiences.Compute`, un seul calcul pour tout)
- Membres de l'organisation, comptes actifs, qui voient l'aire du dossier et ont la page concernée (Médiathèque ou Listes de lecture).
- Une personne compte **une fois** (partagée en direct et par son niveau : droit le plus fort, comptée « en direct »).
- Le **propriétaire** et les **administrateurs** ne sont pas comptés.
- Un **sous-dossier** compte aussi les partages de ses parents.
- Un niveau en lecture seule ne compte jamais en écriture.
- Les accès des membres sont calculés une fois par page, pas par dossier.

### Fichiers
- Nouveaux : `Components/SharePill.razor`. Modifiés : `Folders.cs` (`FolderAudience`, `FolderAudiences`), `MediaLibrary.cs` et `PlaylistLibrary.cs` (`Audiences()`, `Audience` dans `FolderShareInfo` / `PlaylistShareInfo`), `Components/Pages/Media.razor`, `Components/Pages/Playlists.razor`, `Components/FolderShareDialog.razor`, `Components/PlaylistFolderShareDialog.razor`, `wwwroot/app.css` (`.share*`, `.fs-total`).
- Tests : 6 dans `PlaylistFolderTests`, 1 dans `FolderAccessTests` (un seul compte, propriétaire exclu, niveau et comptes désactivés, page ou aire manquante, héritage, lecture seule, page Médiathèque). Ils passent ; toujours les 5 échecs connus de `AppsTests`.
- Vérifié dans le navigateur (instance de test) : administrateur (pastille bleue « 3 » sur « Affiches », infobulle, clic → fenêtre avec total, titre « 3 personnes », « Événements » à 2) et membre (pastilles non cliquables, ambre pour la lecture seule, propriétaire cité).

### Remarque d'outillage
- Quand l'appli de dev tourne, `dotnet test` échoue (exécutable verrouillé) : les tests de cette session ont été lancés sur une copie du dépôt (`robocopy` hors `bin`, `obj`, `.git`, `Unicom-medias`).

## 54. Niveau de droits en onglets : accès et visibilité par domaine, contenus masqués (15 octobre 2026) : codé

Demande : refaire le formulaire d'un niveau de droits en onglets logiques, et donner des droits de visibilité **séparés** sur la médiathèque et sur les listes de lecture (au lieu d'un bloc « Dossiers » commun). Maquette validée : `maquettes/niveau-onglets.html`, **option C** (deux réglages indépendants, et un média caché apparaît « masqué » dans une liste visible ; la liste ne disparaît jamais à cause de son contenu).

### Le formulaire (page Membres › Niveaux)
- Onglets verticaux, chacun avec son état en une ligne : **Vue d'ensemble** · *Où* : **Périmètre** · *Quoi* : **Écrans et planning**, **Listes de lecture**, **Médiathèque**, **Contenus et widgets**. Nom du niveau dans l'en-tête, nombre de membres touchés en pied.
- Chaque domaine se règle de la même façon : **1 · Accès** (Aucun / Voir / Voir et modifier), puis pour Listes et Médiathèque **2 · Visibilité** (Tous / Dossiers choisis / Hors dossier seulement, avec « dossiers des autres en lecture seulement » quand l'accès est « Voir et modifier ») et **3 · Ses propres dossiers** (créer, partager ; demandent « Voir et modifier »).
- « Dossiers choisis » : tableau Masqué / Voir / Modifier aligné en colonnes, recherche, « Tout voir » / « Tout retirer », droit hérité d'un parent en pâle, dossier hors périmètre grisé, nombre d'éléments par dossier ; « Modifier » est grisé si l'accès est « Voir ». Compteur « N listes (médias) visibles sur M » dans le périmètre.
- Écrans : accès, puis Planning et « Gérer les zones » (ce dernier demande « Voir et modifier »).
- Vue d'ensemble : une carte par domaine avec son accès, et le raccourci « Tout en lecture seule » (chaque domaine passe à « Voir »), qui remplace l'ancienne case « Lecture seule ».

### Modèle (`AccessLevel`, `Rights.cs`)
- Nouveaux champs : `ScreensAccess`, `PlaylistsAccess`, `MediaAccess` (`DomainAccess` : none / read / write), `PlaylistsVisibility`, `MediaVisibility` (`LevelVisibility` : all / all-read / chosen / own), `PlaylistsCreateFolders`, `PlaylistsShareFolders`, `MediaCreateFolders`, `MediaShareFolders`.
- `Normalize()` (à chaque enregistrement) en déduit `Pages` (une page par domaine accessible ; Planning reste une case) et `ReadOnly` (= aucun domaine en écriture), retire « Gérer les zones » sans écrans en écriture, et efface les anciens réglages communs (`AllFolders`, `CanCreateFolders`, `CanShareFolders`).
- **Migration** (`AccessLevels.Migrate`, au démarrage) : un niveau d'avant garde exactement ses droits — lecture seule → « Voir » sur chaque page cochée ; `AllFolders` read/write → visibilité « Tous » en lecture / « Tous » ; sinon « Dossiers choisis » si le niveau a des partages de ce type, « Hors dossier seulement » sinon ; créer/partager copiés sur les deux domaines. Avant migration, `AccessTo` / `VisibilityOf` / `CreatesFolders` lisent les anciens champs.

### Droits
- `AreaAccess.AccessTo(domain)` / `CanWrite(domain)` (administrateur : tout).
- Dossiers (`FolderAccess.For`) : la visibilité « Tous » est lue **par domaine** ; tout droit est plafonné par l'accès du domaine (« Voir » ou pas de page → lecture). Sans la page Médiathèque, la visibilité s'applique toujours (médias utilisables dans les contenus).
- Éléments hors dossier : listes modifiables seulement avec « Voir et modifier » les listes (`PlaylistOps.RightOf`) ; fichiers modifiables et import hors dossier seulement avec « Voir et modifier » la médiathèque (`FolderOps.CanEditFile`, `CanUploadTo`).
- Créer et partager des dossiers : par domaine (`CanCreateAtRoot(a, domain)`, `CanShare`).
- **Garde d'écriture par page** (`TenantStore.PageDomain`) : sur `/`, `/planning`, `/zones` (écrans), `/playlists…` et `/media`, une écriture est refusée si le niveau ne fait que « Voir » ce domaine (`ReadOnlyLevelException.For`, message propre au domaine). Le bandeau du haut le dit (« Votre niveau de droits permet de voir la médiathèque, sans la modifier ») et masque les boutons d'en-tête, comme la lecture seule.

### Contenus masqués (option C)
- `MediaLibrary.HiddenFileIds()` : fichiers de l'aire rangés dans un dossier que la personne ne voit pas ; `MediaLibrary.UsesHidden(m, hidden)` : le fichier lui-même, ou un contenu (app) qui en a choisi un.
- Éditeur de liste : un tel contenu s'affiche « Contenu masqué » (vignette hachurée + cadenas, durée conservée) ; il se déplace, mais ne s'ouvre, ne se modifie et ne se retire pas (refus aussi côté code).
- Page Listes : ses médias ne servent jamais de vignette.

### Fichiers
- `Rights.cs`, `Tenancy.cs`, `Folders.cs`, `PlaylistFolders.cs`, `PlaylistLibrary.cs`, `MediaLibrary.cs`, `Components/Pages/Members.razor` (formulaire réécrit), `Components/Pages/PlaylistEditor.razor`, `Components/Pages/Playlists.razor`, `Components/Pages/Media.razor`, `Components/Layout/MainLayout.razor`, `wwwroot/app.css` (`lvt-*`, `.item-row.masked`).
- Tests : `LevelDomainTests` (12 : pages et lecture seule déduites, migration, partages de niveau, enregistrement, plafond par domaine, éléments hors dossier, visibilité par domaine, dossiers propres par domaine, sans page Médiathèque, partage compté en écriture, message, contenu masqué). Tous les anciens tests de droits passent sans changement ; toujours les 5 échecs connus de `AppsTests`.
- Vérifié dans le navigateur (instance de test) : migration du niveau existant, onglets, Médiathèque en « Voir » + « Dossiers choisis » sans dossier, Listes en « Dossiers choisis » ; côté membre, bandeau « voir la médiathèque », pas d'import, dossier « Affiches » invisible, liste « Promo rentrée » avec ses 3 contenus masqués.

### Limites
- Le simulateur « Essayer sur… » montre encore les contenus masqués (prévu : écran neutre « Contenu masqué »).
- La garde par page s'appuie sur l'adresse de la page ouverte : un composant partagé (ex. sélecteur de médias dans l'éditeur de liste) suit la règle de sa page, les opérations de la médiathèque gardant en plus leurs propres contrôles.
- Les écrans en « Voir » : les boutons restent visibles hors en-tête ; l'écriture est refusée avec un message.

## 55. Interrupteurs on/off à la place des cases à cocher de réglage (15 octobre 2026) : codé

Demande : remplacer les cases à cocher par des interrupteurs on/off au design Linkii. Le design existait déjà (pilule grise, pastille blanche, bleu Linkii une fois activée : `.switch`, `input[type=checkbox].ios`, `.switch-input + .switch-ui`) ; il est maintenant utilisé pour **tous les réglages** on/off.

### Ce qui change
- Les 4 dernières cases de réglage deviennent des interrupteurs (`class="ios"`) : « Ne plus afficher ce message » (`MainLayout`), « Synchroniser les écrans de cette zone » (`Zones`), « Zone … » et « Aussi la zone … » du planning (`PlanningPage`).
- **Formulaire du niveau de droits** (`Members.razor`, fragments `Check` et `Tree`) : les cases `.lv-box` (pages, apps, widgets, planning, gérer les zones, tout en lecture seule, ses propres dossiers, toutes les aires) et l'arbre du périmètre passent en `button.switch.sm` (`role="switch"`).
- Nouveaux styles dans `wwwroot/app.css` : `.switch.sm` (32 × 20, pour les listes denses) et `.switch.some` (« en partie » : pastille au milieu, bleu clair), qui remplace la case à moitié cochée d'une zone ou d'une aire dont seuls certains écrans sont cochés ; son libellé accessible ajoute « (en partie) ». Les anciennes règles `.lv-box` sont supprimées.

### Ce qui reste en cases à cocher (volontairement)
- Les cases de **sélection** de plusieurs éléments (`sc-check`, `lx-check` : écrans, listes de lecture, médias, « Tout sélectionner ») : elles choisissent des lignes, ce n'est pas un réglage on/off.

### Vérifié
- Compilation sans erreur ; navigateur (instance de test) : onglets du niveau (Périmètre avec état partiel, Écrans, Listes, Médiathèque, Contenus et widgets, Vue d'ensemble) sans aucune case à cocher restante ; plus aucune case de réglage hors `ios` dans les pages.

## 56. Partage de dossier épuré et racine de la médiathèque réservée aux admins (15 octobre 2026) : codé

### Modales de partage (médiathèque et listes de lecture)
Maquette validée, même logique que la modale du niveau de droits : en-tête avec croix, corps, pied avec résumé.
- **Un seul champ d'ajout** (« Ajouter une personne ou un niveau… ») avec recherche : le menu groupe Personnes et Niveaux de droits, un clic ajoute en Lecture (plus de sélecteur de droit ni de bouton « Partager »). Une personne non ajoutable reste grisée avec sa raison.
- **Lignes d'accès** : pastille (initiales ou bouclier pour un niveau), nom, type, choix segmenté Lecture | Écriture (l'explication de chaque droit est en info-bulle), croix pour retirer. Lecture seule : pastille du droit.
- **Pied** : « N personnes y ont accès : … » (détail une à une / par niveau) et bouton « Terminé ». Dossier privé : message avec cadenas.
- Composants `FolderShareDialog` et `PlaylistFolderShareDialog` (même structure) ; styles `sh-*` dans `wwwroot/app.css`. Modale en `app-modal sh-modal` (520 px).

### Racine de la médiathèque
- Avec l'option « Dossiers dans la médiathèque » activée, **seuls les administrateurs voient les médias hors dossier** (dont tous les fichiers Drive) : `MediaLibrary.Files()` les écarte pour les autres. Sans dossiers activés, rien ne change (médiathèque à plat).
- Formulaire du niveau (onglet Médiathèque) : « Dossiers choisis » = les dossiers cochés seulement ; « Hors dossier seulement » devient « Ses dossiers seulement » ; le compteur « visibles sur » ne compte plus la racine. Les Listes de lecture gardent leur règle (hors dossier toujours visible).

### Limites
- Non essayé dans le navigateur ; un membre non admin dont les contenus utilisent un média de la racine ne le voit plus dans le sélecteur.

### Fichiers Drive rangeables dans un dossier (complément du §56, 15 octobre 2026)
La racine étant réservée aux administrateurs, les fichiers Drive (tous stockés à la racine) doivent pouvoir être rangés :
- `FolderOps.Move` accepte les fichiers Drive (plus d'exclusion) et ne refuse plus l'« autre aire » pour eux (ils sont communs à toutes les aires).
- Page Médiathèque : un fichier Drive se sélectionne, se glisse sur un dossier et a « Ranger dans un dossier… » (si les dossiers sont activés). Renommer, supprimer et « Autre aire… » restent réservés aux fichiers propres (`Own`) ; la sélection groupée les ignore pour les fichiers Drive (`pickedOwn`).
- Synchro Drive : un fichier modifié à la source est réimporté en gardant son dossier de rangement (`shelved`). Un fichier retiré à la source disparaît de la médiathèque, comme avant.
- Limite : supprimer un dossier de la médiathèque supprime aussi les fichiers Drive rangés dedans ; la synchro suivante les réimporte à la racine.

### Retouches de la médiathèque et des listes (15 octobre 2026)
- **Vignettes plus grandes** : galerie de la médiathèque en colonnes de 220 px minimum (au lieu de 170) et espacement augmenté (`.lx-mgrid`, `app.css`). Mobile inchangé (2 colonnes).
- **Dossiers toujours affichés** : une recherche ou un filtre (type, source, non utilisés) ne masque plus les dossiers du dossier courant, dans la médiathèque comme dans les listes de lecture (`Media.razor`, `Playlists.razor`). Avec « Dans : toute l'aire », ce sont toujours les dossiers du dossier courant.

## 57. Barres d'outils épurées : menu Filtres sur six pages (15 octobre 2026) : codé

Maquette validée : mêmes gestes partout, une seule ligne d'outils (recherche, Filtres, tri discret, affichage) et les filtres actifs en puces dessous.

### Composants communs
- `FilterMenu.razor` : bouton « Filtres » (badge du nombre de filtres actifs) qui ouvre un menu à critères ; chaque critère est une rangée de choix avec effectif. Un critère est actif quand sa valeur n'est pas la valeur vide (« Tous »). Paramètres : `Groups`, `OnPick`, `OnReset`, `Extra` (filtres hors critères), `Footer` (contenu libre).
- `FilterChips.razor` : filtres actifs en puces retirables d'un clic + « Tout effacer » (rien tant qu'aucun filtre n'est actif).
- `FilterModels.cs` : `FilterOption`, `FilterGroup`, `FilterExtra`.
- `app.css` : `.fm`, `.tb-btn`, `.fm-pop`, `.fc`, `.tb-seg` (commutateur à quelques choix), `.tb-sort` (tri sans cadre) ; la recherche prend la place libre.

### Pages
- **Médiathèque** : Type, Utilisation, Source (seulement s'il y en a plusieurs) ; « Chercher dans : toute l'aire / ce dossier » dans le pied du menu (puce « Dans : ce dossier » quand actif).
- **Listes de lecture** : Utilisation (Utilisées, À publier si besoin, Non utilisées), même portée de recherche.
- **Écrans** : Zone et État dans « Filtres » (« Gérer les zones… » dans le pied), bouton Planning à côté quand une zone est choisie ; « Tout sélectionner » n'apparaît plus qu'une fois une sélection commencée.
- **Intégrations** : catégorie dans « Filtres ». « Connecter un compte » n'est plus en en-tête que sur l'onglet Comptes connectés.
- **Comptes connectés** : fournisseur dans « Filtres ».
- **Widgets** : Tous | Actifs | Inactifs en commutateur direct (un seul critère à trois choix).

### Niveau de droits
- Onglet « Contenus et widgets » : l'icône de chaque app (`AppIcon`) précède son nom dans la liste des interrupteurs (`Check(..., appId)`).

### Limites
- Non essayé dans le navigateur (l'app tournait et verrouillait l'exe : compilation vérifiée seulement). Le menu reste ouvert après un choix, pour combiner les critères.

## 58. Console Linkii : tableau de bord, clients, réglages (16 octobre 2026) : codé

Refonte de la console administrateur en briques ; première brique : Clients. Revendeurs et Facturation viennent ensuite (leurs pages actuelles restent telles quelles). Maquette validée avant le code.

### Navigation et pages
- Menu : Tableau de bord (`/admin`), Clients, Revendeurs (`/admin/resellers`, avant `/admin`), Facturation, Réglages. La page Supervision (`/admin/screens`) est supprimée : les écrans se voient dans la fiche du client.
- **Tableau de bord** (`Admin/Dashboard.razor`) : clients (nouveaux du mois), écrans (en ligne), utilisateurs (actifs sur 30 j), essais en cours ; nouveaux clients sur 6 mois ; répartition par état et par licence ; « À traiter » (comptes à valider, essais finissant sous 3 jours, essais terminés, suspendus, bloqués) avec lien vers Clients filtré (`?etat=`).
- **Clients** (`Admin/Clients.razor`) : onglets par état avec effectifs, recherche, filtres licence et origine ; tableau (licence, écrans en ligne sur total, utilisateurs, état). Un clic ouvre une **fiche latérale** (Aperçu, Écrans, Utilisateurs, Activité) : licence Base/Growth immédiate, essai (+7/+14/+30 j, passer en abonné), valider le compte / renvoyer le lien, suspendre / bloquer / réactiver, suppression (nom à saisir), « Entrer dans l'espace » (journalisé). `?nouveau=1` ouvre la création.
- **Réglages** (`PlatformSettingsPage.razor`) : sections Inscription et essai, Suspension et blocage (information), E-mails (SMTP), Conditions. Pas de limite d'écrans par plan : les clients paient au nombre d'écrans.

### Suspendu et bloqué
- `Tenant.Suspended` : écrans coupés, back-office accessible (bandeau rouge « Votre espace est suspendu » dans le back-office du client).
- `Tenant.Blocked` (nouveau) : écrans coupés et connexion refusée aux membres. `Tenant.IsCut` = l'un ou l'autre ; utilisé par les écrans, les synchronisations Drive, les alertes et les QR.
- Avant, « Suspendu » fermait aussi le back-office : la connexion ne teste plus que `Blocked`.
- `ClientStates.cs` : un seul état affiché par client (Bloqué > Suspendu > À valider > Essai terminé > Essai > Actif).

### Création d'un client (`Tenancy.CreateClient`)
- Licence Growth, mode simple (`ExpertMode = false`).
- Créé par la console ou un revendeur : essai gratuit aussitôt (durée = réglage de la plateforme). En libre-service : l'essai démarre toujours à la validation de l'e-mail.

### Côté client
- Bandeau orange en haut : « N jours restants dans votre période d'essai (jusqu'au …) » ; rouge une fois l'essai terminé.
- Intégrations et Widgets sont visibles en mode simple, **en lecture seule** : bandeau « Lecture seule en mode simple » avec bouton « Passer en mode expert », actions grisées, et toute écriture refusée côté serveur (`TenantStore.SimpleModeLocked`, hors consultation par l'équipe Linkii).

### Adresses des revendeurs
- `ResellerResolver.HostFor` : séparateur configurable `Linkii:SubdomainSeparator` (« - » par défaut sur `*.duckdns.org`). Suppression d'un revendeur sans client dans Revendeurs.

### Limites
- Vérifié dans le navigateur sur une instance de test : création, validation, blocage, tableau de bord, réglages, connexion d'un client, bandeau d'essai, Intégrations en lecture seule. Non essayés : suppression, suspension côté client (bandeau), prolongation d'essai, changement de licence, page Widgets.
- Automatisation de l'hôte proxy et du certificat (API Nginx Proxy Manager, ou certificat générique DuckDNS) : proposée, non codée.

## 59. Revendeurs : fiche de gestion et mise en ligne automatique dans NPM (16 octobre 2026) : codé

Page Revendeurs refaite sur le modèle de la page Clients (§58) : mêmes onglets, tableau et fiche latérale.

### Page Revendeurs (`Admin/Resellers.razor`, `/admin/resellers`)
- Onglets : Tous, Actif, Suspendu, Bloqué, Adresse en attente (avec effectifs) ; recherche. Tableau : clients, écrans, utilisateurs, adresse, état.
- Fiche (Aperçu, Clients, Équipe, Activité) : adresse et son état (Vérifier, Relancer la mise en ligne), **rabais mensuel** 0 à 50 % (`Reseller.DiscountPct`, utilisé par la future Facturation), accès, informations, suppression (refusée tant qu'il reste des clients ; retire aussi l'hôte NPM). « Ouvrir son adresse » en pied.
- Le revendeur Linkii (par défaut) ne se suspend, ne se bloque ni ne se supprime.
- Le champ « domaine propre » n'est plus proposé (CNAME mis de côté) ; les domaines déjà saisis continuent de fonctionner.

### Suspendu et bloqué (revendeur)
- `Reseller.Suspended` (nouveau) : les écrans de tous ses clients sont coupés (`Tenancy.Blocked`), tous les back-offices restent ouverts ; bandeau « Votre espace est suspendu » chez le revendeur et ses clients.
- Bloqué = `Reseller.Active = false` (comportement existant) : la page de connexion de son adresse affiche « Service suspendu », les sessions de son équipe et de **tous ses clients** sont refusées, leurs écrans coupés.

### Adresses et NPM
- Réglages › **Adresses** : domaine de base, séparateur (point, tiret ou automatique) avec exemple, puis connexion à Nginx Proxy Manager : adresse de l'API, compte dédié (mot de passe chiffré SecretBox), destination des hôtes (schéma, adresse, port), « Enregistrer et tester la connexion ». Ces réglages (`PlatformSettings.BaseDomain`, `SubdomainSeparator`, `Npm*`) remplacent `Linkii:BaseDomain` et `Linkii:SubdomainSeparator` quand ils sont renseignés.
- `NpmProxy.cs` : API REST de NPM. `Provision` obtient un jeton, trouve le certificat générique `*.domaine`, retrouve ou crée l'hôte proxy du revendeur (forcer SSL, HTTP/2, Websockets, bloquer les exploits) et enregistre le n° d'hôte et l'état (`AddressState` : online, error ou vide) ; `Remove` retire l'hôte ; `Check` contrôle l'HTTPS ; `Test` valide la connexion.
- À la création, l'**hôte NPM est créé en premier** : NPM non configuré ou en panne, aucun revendeur n'est créé (message clair, formulaire conservé, « Retour au formulaire »). Puis revendeur et administrateur (hôte retiré de NPM si cette étape échoue), puis e-mail de bienvenue ; la progression est affichée. « Relancer la mise en ligne » ne sert plus que pour un revendeur dont l'hôte a disparu ou n'a jamais été créé (état « En attente »).
- Adresse proposée d'après le nom, contrôlée à chaque frappe (disponible, déjà prise, invalide) avec une alternative (`acme-2`).
- L'e-mail d'identifiants de l'administrateur du revendeur est envoyé au nom de « Linkii Revendeur » (marque Linkii), pas de celui du revendeur.

### Limites
- Vérifié contre un **faux NPM** (script local) : test de connexion, création de l'hôte avec le certificat générique, panne puis état « En attente », blocage et déblocage. Jamais essayé sur le vrai NPM (192.168.1.120:81) : à tester avec un compte NPM dédié.
- Non essayés : suspension (bandeau chez le revendeur et ses clients), suppression avec retrait de l'hôte, « Relancer », « Vérifier » (le contrôle HTTPS part du serveur : il peut échouer à cause du routage interne).
- Prérequis réseau : certificat `*.linkii.duckdns.org` dans NPM et séparateur « . » (un hôte `digisignit-linkii…` existant n'est pas migré).

### Mot de passe de l'administrateur d'un revendeur (16 octobre 2026)
- Fiche revendeur › Informations › « Définir un mot de passe » : mot de passe provisoire (généré ou saisi, 8 caractères minimum), à changer à la prochaine connexion, avec envoi facultatif par e-mail (au nom de « Linkii Revendeur »). Vérifié : connexion sur `acme-affichage.localhost` avec le mot de passe défini, puis demande de nouveau mot de passe.
- Un administrateur de revendeur ne se connecte que sur **l'adresse de son revendeur** (`Tenancy.HostAllows`) ; sur l'adresse principale, le message est « E-mail ou mot de passe incorrect ».
- `Mailer` : identifiant de message au domaine de l'expéditeur. Les e-mails classés en indésirables relèvent surtout de l'expéditeur (SPF, DKIM, DMARC du domaine du « From »), à régler chez le fournisseur d'envoi.

## 60. Revendeur : méthodes de connexion et d'inscription (16 octobre 2026) : codé

- Page **Marque** de l'espace revendeur : carte « Connexion et inscription » avec un interrupteur par méthode : créer un espace avec e-mail et mot de passe, Google, Microsoft, LinkedIn, GitHub (enregistré avec « Enregistrer »). Un fournisseur dont l'application OAuth n'est pas configurée sur la plateforme est grisé (« Non disponible : à activer par Linkii »).
- `Reseller.SignupMethods` (liste) ; `null` = ancien réglage `AllowSelfSignup` (Linkii : toutes les méthodes, autres revendeurs : aucune). Par défaut un revendeur n'en propose donc aucune.
- **Option A** : désactiver un fournisseur ne le retire que de l'inscription. Sur la page de connexion, son bouton reste affiché tant qu'au moins un compte de ce revendeur l'utilise ; les personnes déjà inscrites ne sont jamais enfermées dehors.
- Page de connexion : « Créer un espace » seulement si l'e-mail est proposé ; la création par un fournisseur (dernière étape) exige que ce fournisseur soit proposé ; refus aussi côté serveur (`DoSignup`, `DoSsoSignup`, `/login/sso/finish`).
- Vérifié : après avoir activé e-mail et Google chez le revendeur de test, sa page de connexion n'affiche que « Continuer avec Google » et « Créer un espace ». Non vérifié : le cas d'un fournisseur désactivé mais encore utilisé, et le parcours complet d'inscription par fournisseur (clés OAuth absentes en test).

### Compléments §59 : exploitation, diagnostics et déploiement (16 octobre 2026)
- **Diagnostic NPM** (`NpmProxy.Problem()`) : le test de connexion et la création disent précisément ce qui manque (adresse, identifiant, mot de passe, destination des hôtes) ou que le **mot de passe enregistré est illisible sur ce serveur** (réglages copiés d'une autre machine : le chiffrement SecretBox dépend des clés de protection de données du serveur, à ressaisir). Si le certificat générique manque, le message donne le domaine cherché et les certificats `*.…` trouvés dans NPM (cause typique : « Domaine de base » vide, donc `linkii.com` par défaut).
- **Identifiant NPM** : texte libre envoyé tel quel au champ `identity` de l'API ; adresse de l'API en `http://hôte:81` (le port 81 de NPM n'est en général pas en HTTPS). Éviter le remplissage automatique du navigateur sur ce champ.
- **Une instance = une base** : chaque serveur Linkii a son `data.json`. L'hôte NPM envoie vers une instance précise (destination des hôtes) ; si elle ne connaît pas le revendeur (autre base), elle sert la marque Linkii par défaut et la connexion du revendeur échoue (« E-mail ou mot de passe incorrect »). Créer les revendeurs sur l'instance vers laquelle NPM pointe.
- **Connexion** : le mot de passe est aussi essayé sans espaces de début ou de fin (copié depuis un e-mail). Un administrateur de revendeur ne se connecte que sur l'adresse de son revendeur.
- **Mise en production** : redémarrer le service après déploiement ; prérequis NPM : certificat `*.domaine` (Let's Encrypt par DNS, plugin DuckDNS), compte NPM dédié. Les anciens hôtes `digisignit-linkii…` créés à la main ne sont pas migrés.
- **Courrier indésirable** : relève de l'authentification de l'expéditeur (SPF, DKIM, DMARC du domaine de l'adresse d'expédition, réglée dans Réglages › E-mails) ; l'application ajoute seulement un `Message-ID` au domaine de l'expéditeur.

## 61. Facturation : abonnements à date anniversaire (16 octobre 2026) : codé

> **Mis à jour par le §62** : les revendeurs ne paient plus Linkii, ils en reçoivent une commission ; tous les clients paient Linkii. Ce qui concerne le « paiement d'un revendeur », son rabais et son blocage ci-dessous n'est plus d'actualité.

Page `/admin/billing` refaite sur le modèle de Clients et Revendeurs ; logique dans `Subscriptions.cs`, page dans `Admin/Billing.razor`. Prévue pour Stripe ; en attendant, le paiement d'un renouvellement s'enregistre à la main.

### Principe
- Prix **par écran et par mois** selon la licence (Réglages › Facturation : Base 15, Growth 29 CHF par défaut, remise annuelle 15 %). Pas de limite d'écrans.
- Un **client direct** (revendeur Linkii) paie **au mois ou à l'année** (`Tenant.BillingPeriod`) ; un **revendeur** paie **au mois** (écrans de ses clients actifs au tarif de leur licence, moins son rabais `Reseller.DiscountPct`) ; un client d'un revendeur est facturé avec le revendeur.
- **Date anniversaire** = date du premier paiement, qui ne bouge plus (`NextRenewalUtc` + `AnchorDay` : un 31 janvier devient 28 février puis revient au 31 mars). Chaque paiement avance la date d'un mois ou d'un an.
- **Sans paiement à la date anniversaire, le compte est bloqué automatiquement** (`Subscriptions.Enforce`, `BlockedReason = "payment"`) : pas de facture, de relance ni de suivi. Le blocage tombe le lendemain de la date anniversaire (la journée sert à payer). Un revendeur non payé est bloqué avec tous ses clients. Le paiement suivant débloque (si la nouvelle échéance n'est pas déjà dépassée) ; un blocage manuel n'a pas de motif et reste manuel.
- `SubscriptionWorker` applique le blocage au démarrage puis toutes les heures et prévient les écrans ; la page Facturation l'applique aussi à son ouverture.
- **Premier paiement** : il fixe la date anniversaire et met fin à l'essai (un client en essai n'est pas facturé). Sans paiement enregistré, un compte actif reste « Premier paiement » et n'est jamais bloqué (comptes existants).
- **Bouton « Paiement reçu »** (à la place de Stripe) : sur chaque ligne de Revendeurs et Clients, dans les fiches, et dans l'onglet Paiements. Disponible pour un premier paiement ou dans les 7 jours avant l'échéance (ou après) ; « Annuler » défait le dernier paiement d'un compte (la fin d'essai n'est pas rétablie). Chaque paiement est gardé dans `Db.Payments` (date, période couverte, écrans, montant au moment du paiement).

### Page
- Chiffre d'affaires mensuel (comptes actifs, réparti Base et Growth, rabais déduits ; un client à l'année compte pour son équivalent mensuel remisé), Encaissé du mois sélectionné, À renouveler sous 30 jours, Bloqués faute de paiement (avec le montant dû).
- Compteurs : revendeurs, clients (dont actifs), écrans facturés, utilisateurs ; rappel des tarifs.
- Onglets **Revendeurs** (écrans Base × prix, Growth × prix, brut, rabais, net, renouvellement ; fiche : détail par client, rabais, paiement, historique), **Clients** (facturé à, licence, périodicité, CHF par mois, renouvellement ; fiche : calcul, périodicité mensuelle ou annuelle appliquée au prochain renouvellement, paiement), **Paiements** (à encaisser, puis journal du mois).

### Limites
- Vérifié sur des données de test : blocage automatique d'un client dont l'échéance est dépassée, paiement qui le débloque et avance la date au 10 du mois suivant, montants (Growth 17 écrans = 493.00), compteurs. Non vérifiés : premier paiement (annuel), annulation d'un paiement, changement de périodicité, revendeur non payé, changement de mois.
- Le nombre d'écrans et la licence sont ceux du moment du calcul. Un client à l'année qui ajoute des écrans en cours d'année n'est pas refacturé avant le renouvellement.
- Pas d'export, de facture PDF, d'e-mail ni de relance. Stripe (abonnements, webhook de paiement, jour anniversaire) reste à brancher : `Subscriptions.RecordClient` et `RecordReseller` sont les points d'entrée à appeler depuis le webhook.

## 62. Facturation : commission et versement des revendeurs (16 octobre 2026) : codé

Les revendeurs **reçoivent** de l'argent de Linkii. Décisions : prix unique (tarif Linkii), taux de commission par défaut modifiable par revendeur, commission **sur le net** (montant payé moins frais de paiement), paiement annuel commissionné **d'un coup**, versement **le dernier jour du mois**.

### Circuit
- **Tous les clients paient Linkii** (directs ou d'un revendeur), au tarif unique, au mois ou à l'année, avec la date anniversaire et le **blocage automatique sans paiement** du §61. Le revendeur n'a plus d'abonnement, de date anniversaire ni de blocage de paiement (champs `NextRenewalUtc`, `AnchorDay`, `BlockedReason` retirés de `Reseller`, `DiscountPct` remplacé par `CommissionPct`).
- À chaque paiement (`Subscriptions.RecordClient`, bouton « Paiement reçu » puis Stripe) : frais estimés (`Réglages › Facturation` : 2,9 % + 0,30 CHF par défaut), taux de commission **figé sur le paiement** (`Payment.CommissionPct`), `Payment.Commission = (montant − frais) × taux`. Un client direct n'a pas de commission.
- **Taux** : `PlatformSettings.DefaultCommissionPct` (20 % par défaut) ; `Reseller.CommissionPct` (null = taux par défaut), réglé dans Revendeurs › fiche. Un changement ne touche que les paiements suivants.
- **Versement du mois** : somme des commissions des paiements reçus dans le mois (fuseau de Zurich). États : En cours jusqu'au dernier jour, À verser à partir de ce jour, Versé, Rien à verser. « Marquer versé » (`Payout`, montant figé + référence) après le virement ; « Annuler » le défait. Un paiement ne peut plus être annulé une fois le versement du mois enregistré.
- **Prévision** (`Subscriptions.ExpectedOf`) : renouvellements encore attendus d'ici fin de mois chez les clients actifs du revendeur, avec la commission qu'ils rapporteraient ; affichée à part de l'acquis.

### Espace revendeur : Facturation (`/reseller/billing`, remplace « Abonnement »)
- **En direct** (actualisé toutes les 30 secondes) : À toucher fin du mois (acquis), Encore attendu, Prévision fin de mois, Encaissé par Linkii (frais, net).
- Clients (licence, écrans, périodicité, montant, renouvellement, ma part ; un client bloqué faute de paiement est signalé), paiements du mois (payé, frais, net, ma part), versements des 6 derniers mois (statut, date, référence), **IBAN** (contrôlé, clé modulo 97) et n° de TVA.

### Console : Facturation (`/admin/billing`)
- Chiffre d'affaires mensuel (tarif Linkii, comptes actifs), Encaissé du mois (frais, net), **Part des revendeurs** et **net Linkii**, Bloqués faute de paiement.
- Onglets **Clients** (tous : paiement, renouvellement, « Paiement reçu »), **Revendeurs** (CA, taux, part acquise, IBAN ; fiche : détail du mois, versement, coordonnées), **Paiements** (à encaisser, journal avec commission), **Versements** (par revendeur : paiements, payé, frais, net, taux, à verser, IBAN, statut, « Marquer versé »).

### Limites
- Vérifié sur des données de test : premier paiement d'un client de revendeur (406.00 CHF, frais 12.07, net 393.93, commission 20 % = 78.79), net Linkii (808.14 pour 899.00 encaissés), page revendeur en direct, enregistrement d'un IBAN valide. Non vérifiés : « Marquer versé » et son annulation, taux particulier, paiement annuel d'un client de revendeur, revendeur bloqué.
- Frais estimés tant que Stripe ne donne pas les frais réels. Pas de remboursement ni de litige (un paiement annulé retire sa commission), pas de montant minimum de versement ni de report, pas de PDF ni d'e-mail (relevé, TVA : à définir avec la comptabilité). Pas de virement automatique (Stripe Connect).
- Les anciens revendeurs gardent leur date et leur blocage de paiement dans `data.json` (champs ignorés) ; un revendeur bloqué pour paiement avant ce changement reste bloqué jusqu'à action manuelle dans Revendeurs.

## 63. Look « app » du back-office mobile (17 octobre 2026) : codé
Maquette validée : `maquettes/app-mobile.html`. **Périmètre : uniquement l'app Android du back-office** (`Linkii.AndroidBackOffice`). Le site PC, le navigateur mobile et le player Android ne changent pas.
- **Détection** : l'app ajoute déjà `LinkiiBackOffice/x.y` à son identifiant ; `App.razor` pose `body.is-app` quand il le voit. Tout le CSS est dans le bloc « Look app » en fin de `app.css`, préfixé `.is-app`.
- **Barre du bas** (`MainLayout.razor`, `.app-tabs`) : Écrans, Listes, Médias, Planning (expert), Plus. Rendue pour l'espace client seulement, masquée sans `.is-app`. Menu latéral et ☰ masqués dans l'app (console admin et revendeur gardent le menu latéral).
- **Menu Plus** (`.app-more`) : feuille avec profil, raccourcis (Aires, Zones, Widgets, Intégrations, Statistiques, Membres selon droits et mode), Réglages, Mon compte, mode expert, Se déconnecter.
- **Cartes** : bordure plus nette, ombre, liseré d'état à gauche (en ligne / pause / hors ligne) ; barre du haut sombre.
- **Réglages** : liste des rubriques groupée, puis la rubrique ouverte avec « ‹ Réglages » (état `sub` dans `Settings.razor`) ; barre d'enregistrement au-dessus de la barre du bas. Interrupteurs : ceux du site, inchangés.
- **Vérifié** : navigateur en 375 px avec `is-app` ajouté à la main (barre, feuille Plus, liste Réglages et rubrique) ; PC sans la classe inchangé ; en-tête `User-Agent` testé par curl. **Pas essayé** : sur l'app Android réelle, pages Écrans/Listes/Médias avec des données, rôles limités.
- **Compléments (17 oct.)** : bascule simple / expert dans la barre du haut (`.app-mode`, retirée du menu Plus) ; boutons « Enregistrer le niveau » / « Annuler » empilés sur toute la largeur (`.lvt-modal`, qui forçait une ligne) ; `wwwroot/app-mode.js` garde `body.is-app` (pont `LinkiiAdmin` + drapeau de session) quand la page est relue après la bascule, pour que la barre du bas ne cède plus la place au menu ☰ du PC.
- **Listes et connexion dans l'app (17 oct.)** : Écrans, Listes de lecture et Médiathèque — boutons de tête sur toute la largeur (« Nouveau dossier » devient un vrai bouton à côté de « Nouvelle liste » via `.lx-appfolder`), dossiers repliés derrière un bouton « Dossiers » (`.lx-fold-toggle`, état `foldersOpen`), Filtres en icône seule en haut à droite du titre avec menu contenu dans l'écran, recherche et tri sur une ligne, sélection multiple en pastille ronde façon galerie (`input.sc-check` / `input.lx-check`), barre de sélection remontée au-dessus de la barre du bas. Connexion compactée sous `.is-app` (marges, side « Connexion rapide » sans paragraphe). Tout est préfixé `.is-app` ; le PC garde son rendu. **Pas essayé** : page Écrans avec de vrais écrans et murs, dossiers de la médiathèque avec contenu.
- **Médiathèque « photos » et barre du bas (17 oct.)** : nouvelle vue « Photos » dans la médiathèque (bouton à côté de Cartes / Liste, par défaut dans l'app) : mosaïque groupée par jour d'import (`PhotoGroups`), PC en lignes justifiées (`--r` = format de l'image, `flex-grow`), app en 3 colonnes carrées ; pastille ronde de sélection (au survol sur PC, appui long dans l'app via `contextmenu`, rond du jour pour tout cocher), point vert / orange = utilisé / non utilisé, durée des vidéos, envois en cours en tuiles. **Bouton flottant d'import** dans l'app (`.ph-fab`, le bouton « Importer » de l'en-tête y est masqué). Barre du bas (option B de `maquettes/app-barre-ecrans-centre.html`) : Médias · Listes · **Écrans** (grande tuile centrale) · Planning · Plus, hauteur 68 px ; l'aperçu d'un média passe au-dessus de la barre. Mode simple (sans Planning) : Médias · Listes · Écrans · Plus (Écrans n'est alors pas exactement au centre). Maquette : `maquettes/mediatheque-photos.html`. **Pas essayé** : vidéos, dossiers en vue photos, appui long sur un vrai téléphone.
- **Boutons flottants et barre en mode simple (17 oct.)** : en mode simple, la barre du bas devient Médias · Listes · Écrans · **Intégrations** · Plus (Intégrations n'est alors plus dans le menu Plus). Dans l'app, tous les boutons d'ajout sont remplacés par un bouton flottant rond (`wwwroot/app-fab.js`, styles `.app-fab` en fin d'`app.css`) : les boutons d'origine portent `data-fab="Libellé"`, restent dans la page, masqués par `.is-app [data-fab]`, et le bouton flottant les « clique » (droits et conditions de la page inchangés). Une seule action : le bouton la lance ; plusieurs (Listes : liste + dossier, Médias : import + dossier, édition de liste : intégration + contenu) : bouton déroulant avec fond assombri et petits boutons à libellé. Animations : entrée en rebond, onde périodique, rotation en croix, apparition échelonnée (désactivées si « réduire les animations »). Le bouton s'efface quand une fenêtre, un tiroir, un menu ou la barre de sélection est ouvert. Pages : Écrans, Listes, Médias, Membres, Planning, Zones, Aires, Intégrations (menu de comptes en feuille), édition de liste, console admin et revendeur. Remplace le bouton flottant propre à la médiathèque. **Pas essayé** : sur l'appareil, pages Membres/Planning/Zones/Aires et console admin.
- **Dossiers toujours actifs (17 oct.)** : l'interrupteur « Dossiers dans la médiathèque » (Réglages › Contenus et écrans) est retiré. `Tenant.FoldersOn` vaut toujours `true` (les anciennes valeurs enregistrées sont ignorées) : dossiers de la médiathèque et des listes, partages et section « Médiathèque » des niveaux sont disponibles pour tous. Test « option éteinte » supprimé.
- **Refonte Écrans et Médiathèque dans l'app (17 oct.)** : maquettes validées dans la conversation, tout en CSS `.is-app` (sauf mention). Rien n'est changé sur PC.
  - **Écrans** (`Screens.razor`, carte `.gc`) : titre + pastille du nombre d'écrans (`.scr-count`) ; texte d'intro, ligne appareil (OS, navigateur), logo, puces zone/synchro de la carte et boutons Aperçu/Pause retirés. Aperçu plus bas (5/2). État de l'écran (En ligne, Veille · réveil, Hors ligne) = pastille `.gc-pill` sur l'aperçu ; état de publication (`À jour`, `Pas encore reçue`, `À publier`) = pastilles `.gc-dpill` à droite du nom ; zone = petite étiquette `.gc-zapp` à droite de la liste de lecture. Aperçu en direct et Pause passent dans le menu ⋮ (entrées `.app-only`). La carte « Connecter un écran » en bas de liste est masquée (`.scr-add`, le bouton flottant suffit).
  - **Bouton flottant** (`app-fab.js`/`app.css`) : les petits boutons invisibles du bouton déroulant restaient cliquables et couvraient la carte à gauche du « + » ; fermés, ils sont maintenant `pointer-events: none; visibility: hidden`.
  - **Médiathèque** (`Media.razor`) : pastille du nombre de médias (`.lx-cntpill`), ligne de statistiques masquée, pastille « Non utilisés » retirée, titres de jour en « Dimanche 11 octobre » (`Cap`, aussi sur PC). Mosaïque aux formats : rangées de même hauteur (120 px), largeur selon le format (`PhotoRatio`, déjà borné 0,55 à 2,2) comme sur PC. Dossiers = bande de cartes qui défile (`.lx-fstrip` / `.lx-fcard`) : 3 derniers médias prêts du dossier (`RecentMedia`), nom, compteur, badge Privé / Partagé · N / Écriture / Lecture seule (`.fbadge`), carte « Nouveau dossier » en fin de bande ; appui long = menu Partager, Renommer, Supprimer (feuille en bas). Icône `eye-off` ajoutée à `Ico.razor`.
  - **Vérifié** : compilation sans erreur. **Pas essayé** : rendu à l'écran (marges de la bande, accrochage), appui long sur l'app Android, clic sur la carte écran à gauche du « + ».
- **Listes de lecture et pages d'administration dans l'app (17 oct.)** : mêmes principes que Écrans et Médiathèque (maquettes validées), tout en CSS `.is-app` sauf les ajouts de balisage ci-dessous ; rien ne change sur PC. Les textes d'introduction (`.sc-intro`) sont masqués, le nombre d'éléments passe en pastille bleue (`.lx-cntpill`), le menu ⋮ est remplacé par l'appui long (`@oncontextmenu` quand `inApp`, menu en feuille en bas).
  - **Listes de lecture** (`Playlists.razor`) : dossiers en bande de cartes (`.lx-fstrip`, aperçu des 3 dernières listes via `FolderPreview`, badge Privé / Partagé · N / Écriture / Lecture seule) ; cartes de liste à aperçu bas, rond lecture en bas à droite, état de diffusion en pastille à droite du nom, ligne « contenus · modifiée ».
  - **Planning** : cible en sélecteur pleine largeur, navigation de semaine sur une ligne, Semaine/Journée en contrôle segmenté, « Que voit l'écran ? » en icône en haut à droite (`.pn-pv`), une heure sur trois dans la grille (corrige le chevauchement 00:00 / 01:00).
  - **Zones** : le tableau devient une carte par zone (`.zn-tbl`, ligne « aire · N écrans » `.zn-sub`, pastilles type et synchronisation) ; le sélecteur d'aire reste un sélecteur pleine largeur ; appui long = Éditer, Synchroniser, Supprimer.
  - **Widgets et Intégrations** : une ligne par élément (`.cat-card.compact` en grille : icône, nom, usage, interrupteur), description sur une ligne, aperçu des widgets et chip de compte masqués ; onglets et filtre Tous/Actifs/Inactifs en contrôles segmentés.
  - **Comptes connectés** : services en pastilles (`.acc-uses`), permissions masquées, bouton **Reconnecter** (compte perdu OAuth) ou **Vérifier** visible dans l'en-tête de la carte (`.acc-reco`) ; appui long = menu du compte.
  - **Membres et Niveaux de droits** (`Members.razor`) : tableaux en cartes (`.mb-tbl`, `.lv-tbl2`), statistiques en bande qui défile (`.mb-stats`), « N membre(s) » en pastille (`.mb-unit`), résumé Intégrations / Widgets / Périmètre en pastilles ; appui long = menu.
  - **Vérifié** : compilation sans erreur. **Pas essayé** : rendu à l'écran des sept pages et appui long sur l'app Android.
- **Corrections du look app après essai au navigateur (17 oct.)** : le tableau « en cartes » générique des mobiles (étiquettes `data-label`, `td` en largeur pleine) écrasait les tableaux des Zones, Membres et Niveaux ; ces trois tableaux (`.zn-tbl`, `.mb-tbl`, `.lv-tbl2`) le contournent par des règles `!important` (largeur, affichage, `::before`). Vérifié en 360 et 390 px avec `body.is-app` ajouté à la main, sur un espace de test local.
  - **Planning** : Semaine/Journée centrés (`justify-content: center`), calendrier contenu dans la largeur (gouttière d'heures 38 px, `overflow-x: hidden`), hauteur adaptée à l'écran, légende qui passe à la ligne et laisse la place au bouton flottant.
  - **Zones** : pastille ronde à gauche du nom (`.zn-ico`), « par défaut » en pastille, sélecteur d'aire pleine largeur aligné à gauche.
  - **Widgets et Intégrations** : carte en grille (icône · nom + pastille d'usage + description sur une ligne · interrupteur) ; `.cat-top` passe en `display: contents`.
  - **Membres et Niveaux** : « administrateur » seul dans la statistique (`.mb-long`), périmètre en pastille, niveaux avec icône ronde (`.lv-ico`) et pastilles Intégrations / Widgets / Périmètre sur une ligne.
  - **Pas vu** : Comptes connectés (aucun compte connecté sur l'espace de test), appui long sur appareil.
- **Réglages dans l'app (17 oct.)** : maquette validée, vérifiée au navigateur (390 px, `is-app` ajouté à la main). Rien ne change sur PC, sauf mention.
  - **Mise en page commune** : le titre du bloc est au-dessus de la carte, en petites capitales grises (`.rg-card > header h3`) ; les lignes forment la carte blanche (premier et dernier enfant arrondis) ; texte à gauche, contrôle à droite (interrupteur, valeur grise avec chevron, bouton). Les champs de saisie (nom, mot de passe, code) gardent le libellé au-dessus, pleine largeur. Textes d'introduction et descriptions de bloc masqués. Contournement du `.sv-row { flex-direction: column }` générique des mobiles.
  - **Utilisation** : tuiles deux par ligne, cliquables en entier (`.rg-tile`), Images et Vidéos en deux tuiles (`.rg-apptiles`, le bloc détaillé `.rg-media` est masqué).
  - **Contenus et écrans** : « Nouveaux écrans » (`ScreenFormat`) en lignes libellé / valeur ; code du menu technicien sur une ligne avec Afficher et Générer.
  - **Veille et luminosité** : `AreaSleep` et `AreaDim` reçoivent `Embedded` et `OnEdited` ; dans les Réglages, la barre « Enregistrer » de la page enregistre aussi les plages de veille et les profils de luminosité (`Persist()`, appelé par `Settings.Save`), il n'y a plus de second bouton. La barre est compacte (Annuler / Enregistrer), toujours au-dessus de la barre du bas dès qu'un réglage change. Fuseau des plages en ligne (`.sl-tzrow`).
  - **Alertes et pause** : le visuel de pause (`MediaField`) tient sur une ligne ; le bloc « Appareil des écrans » est supprimé (PC et app).
  - **Compte et confidentialité** : `ConsentPrefs` passe sur la structure `rg-card` / `sv-row` ; la carte « Mode d'utilisation » est masquée dans l'app (`.rg-mode`, la bascule est dans la barre du haut).
  - **Pas vu** : rendu sur l'app Android réelle.
- **Console Linkii et espace revendeur dans l'app (17 oct.)** : maquette validée, **app mobile uniquement** (`body.is-app`) ; le PC ne change pas (le balisage ajouté y est masqué).
  - **Navigation** (`MainLayout.razor`, `AdminSpace`) : la coquille reçoit `has-tabs` aussi hors espace client ; barre du bas Accueil · Revendeurs (admin) ou Équipe (revendeur) · **Clients** au centre · Facturation · Plus. Feuille « Plus » : Réglages (admin) ou Marque (revendeur), Mon compte, Installer l'application, Se déconnecter. Pastille « Admin » / « Revendeur » dans la barre du haut (`.app-space`). Les boutons `data-fab` deviennent le bouton flottant, comme ailleurs.
  - **Listes** (clients admin et revendeur, revendeurs, équipe, tableaux de facturation) : une carte par ligne (avatar, nom, état à droite via `td.st`, puis des pastilles ; unités ajoutées par `.u`, masquées sur PC ; les tableaux sans `.adm-cards` gardent leur libellé de colonne devant la valeur). Filtres d'état en pastilles qui défilent, vues (facturation, équipe, fiche) en contrôle segmenté (`.adm-tabs.seg`), pastille du nombre à côté du titre, textes d'introduction et notes de bas de page masqués.
  - **Tableaux de bord** : chiffres clés en tuiles deux par ligne ; blocs (graphique, répartition, À traiter, derniers paiements) titrés au-dessus de la carte en petites capitales. « À traiter » reste à sa place dans la page (sous les graphiques).
  - **Fiches** (tiroirs) : plein écran au-dessus de la barre du bas, onglets segmentés, sections titrées au-dessus des cartes, action « Entrer dans l'espace » collée en bas.
  - **Facturation** : mois sur une ligne (flèches carrées), bandeau de compteurs masqué, boutons de paiement en petites pastilles dans la carte.
  - **Réglages de la console** (`PlatformSettingsPage`, `sub`) : liste des rubriques puis une rubrique par page avec « ‹ Réglages » ; lignes libellé à gauche / valeur à droite ; « Enregistrer » collé au-dessus de la barre du bas (`.adm-save`). **Marque** : les trois sections l'une sous l'autre dans l'app (`.adm-pane`, une seule visible sur PC comme avant).
  - **Vérifié** : console admin en 390 px avec `is-app` ajouté à la main (tableau de bord, clients et fiche, revendeurs, facturation, réglages, feuille Plus). **Pas vu** : l'espace revendeur (un revendeur ne se met pas en ligne en local sans Nginx Proxy Manager), rendu sur l'app Android réelle.

## 64. Alertes : un réglage global, e-mail et/ou notification (18 octobre 2026) : codé

Maquette validée : `maquettes/alertes-simple.html` (les versions `alertes-surveillance-notifications`, `alertes-notifications-globales` et `alertes-notifications-par-cas` ont été écartées). Avant, l'interrupteur « Prévenir quand un écran tombe » (un libellé « e-mail ») commandait aussi les notifications de l'app pour les écrans, les autres notifications partaient sans réglage d'organisation, et chaque personne coupait des catégories dans Compte et confidentialité.

- **Réglages › Alertes et pause** (Growth), en trois étapes numérotées : ① **Activer les alertes** (interrupteur général, couper garde les choix) ② **Comment être prévenu** : par e-mail (avec les destinataires : tous les administrateurs ou une adresse dédiée) et/ou par notification (téléphones des administrateurs où l'app est connectée, nombre affiché) ③ **De quoi être prévenu** : Écrans hors ligne (avec le délai, seul cas qui en a un), Synchronisations, Équipe, Abonnement. La carte « Pause des écrans » ne change pas.
- **Modèle** (`Tenant`) : `AlertsOn`, `AlertMail`, `AlertPush`, `AlertTypes` ; `AlertOfflineMinutes` et `AlertEmail` gardés. `Alerts.Wants(c, type)` : Growth + interrupteur + au moins un canal + type suivi.
- **Envoi** (`Alerts.cs`, `AlertService.Raise`) : un seul point d'entrée pour les écrans (`ScreenWatchWorker`, e-mail propre `SendScreenAlert`), les synchronisations (`DriveService`), l'équipe (`Members`), l'abonnement (fin d'essai proche, essai terminé, espace suspendu). Les autres types ont un e-mail simple (`Mailer.SendAlert` : titre, texte, bouton vers la page). Le rappel de fin d'essai envoyé au créateur de l'espace reste un e-mail de compte, envoyé quels que soient les réglages ; l'alerte « Abonnement » ne lui renvoie pas d'e-mail en double.
- **Choix personnel retiré** : la carte « Notifications » de Compte et confidentialité disparaît (`User.PushMuted` gardé mais ignoré). Il ne reste dans `AppDevicePrefs` que le verrouillage biométrique.
- **Migration** (`Alerts.Migrate`, une fois, `AlertsVersion`) : une organisation qui avait l'ancien interrupteur activé garde e-mail et notifications, pour les quatre types ; les autres restent tout coupé, comme une nouvelle organisation. Changement de comportement assumé : les notifications Synchronisations / Équipe / Abonnement ne partent plus pour une organisation qui n'a pas activé les alertes, ni en licence de base.
- **Correction au passage** : sur écran étroit (≤ 700 px), les lignes des Réglages se centraient (`.rg-card .sv-row` l'emportait sur la règle mobile) ; elles s'alignent à gauche comme prévu.
- **Vérifié** : page dans le navigateur (PC et 375 px), enregistrement et relecture, alerte « Équipe » partie par e-mail (journal, SMTP local absent), tests `ControlTests` (migration, conditions). **Pas vu** : rendu dans l'app Android réelle, notification reçue après ce changement, e-mail réel.

## 65. Médiathèque, listes et écrans : blocs séparés, partage de médias, tiroir immersif (18 octobre 2026) : codé

Maquettes montrées dans la conversation (pas de fichier `maquettes/`) : blocs Dossiers / Médias, état des écrans (option A), partage de médias, tiroir « 1 · Immersif ».

- **Bouton unique** (Médiathèque) : « Importer » + menu ▾ (Importer des médias, Nouveau dossier), comme « Nouvelle liste ▾ » des Listes (`.lx-split`). Sans droit d'import, « Nouveau dossier » reste seul.
- **Créer ses dossiers n'est plus un réglage** : les interrupteurs « Créer des dossiers de listes / de médias » disparaissent des niveaux de droits ; `AccessLevel.CreatesFolders` vaut toujours vrai (le droit d'écriture sur le domaine reste exigé, `FolderAccess.CanCreateAtRoot`) et `Normalize` force les champs à vrai. Partager ses dossiers reste un réglage.
- **Fichier importé à la racine** : `MediaItem.OwnerId` (membre qui a importé, renseigné par `MediaImporter` / `MediaUploader`). Avec les dossiers activés, la racine n'est vue que par les administrateurs **et par le propriétaire du fichier** (`MediaLibrary.Normal`). Les fichiers importés avant n'ont pas de propriétaire.
- **Dossiers en cartes carrées (PC)** : Médiathèque et Listes réutilisent les cartes `.lx-fcard` de l'app (aperçu des derniers médias, badge Privé / Partagé / Lecture seule, nom, nombre), en grille de 168 px (`.lx-fgrid`) ; menu ⋮ au survol, glisser-déposer conservé ; les anciennes puces `.lx-fchip` sont retirées de ces deux pages.
- **Deux blocs** (Médiathèque et Listes, PC et app) : `section.lx-blk-folders` « Dossiers » (fond gris, masqué sans dossier sur PC) puis `section.lx-blk-main` « Médias » / « Listes » (fond blanc) qui contient recherche, filtres, tri, affichage et contenu. Dans l'app les blocs n'ont pas de cadre.
- **Écrans en ligne / hors ligne (PC, option A)** : carte `.gc` en ligne = liseré vert (`is-on`) ; hors ligne = aperçu gris, bandeau rouge en bas « Hors ligne depuis 3 h / 25 min / 2 j » (« Jamais connecté »), sans pastilles de diffusion ni « À publier », sans étiquettes ni boutons Aperçu / Pause, corps atténué (`Screens.OfflineSince`). En pause et en veille inchangés ; l'app garde son liseré rouge ; murs inchangés.
- **Partage de médias** : d'abord par personne (lecture seule), **remplacé le jour même par le partage entre aires, voir §66**.
- **Interface du partage** : « Partager… » dans la barre de sélection, le menu ⋮ et le tiroir ; `MediaShareDialog` (recherche d'un membre, choix multiple, rappel « lecture seule »). Côté destinataire : « Partagé par … », « Ne plus voir » avec confirmation (prévient s'il est dans ses listes).
- **Tiroir d'un média immersif** (`MediaPreviewPanel`) : aperçu en grand avec flèches et croix dessus, légende sombre (nom, type · définition · poids, emplacement · date), blocs « Partagé avec » (personnes, date, ✕ pour retirer, « Ajouter une personne ») ou « Partagé par », « Utilisé dans » en pastilles, actions centrées (Partager, Télécharger, Renommer, Ranger, corbeille). (le partage y est par aire, §66)
- **Tests** : `AreaShareTests` (§66). 9 tests déjà en échec avant ces changements (Signup, Apps, Pwa) ; `LevelDomainTests` : assertion sur l'ancien interrupteur retirée.
- **Pas fait** : cartes de listes 16/9 de la maquette (les cartes actuelles restent). **Pas vu** : aucun de ces écrans dans le navigateur (pas de session ouverte), ni sur l'app Android.

## 66. Partage entre aires de gestion en lien vivant, et sélecteur d'aire dans l'app (18 octobre 2026) : codé

Maquette montrée dans la conversation (fenêtre unique « Partager », éléments reçus à la racine avec une pastille « lien » et l'aire d'origine). Décisions : **lien vivant, jamais une copie** ; **modifiable par l'aire qui reçoit** (écriture comme dans ses propres éléments) ; partager une liste rend ses médias lisibles dans l'aire qui la reçoit ; retirer le partage **laisse aux écrans leur contenu publié** jusqu'à la prochaine publication ; **le partage par aire remplace le partage par personne** des médias (§65).

- **Modèle** (`AreaShares.cs`) : `AreaShare` (`Db.AreaShares`) = type (`media`, `mediafolder`, `playlist`, `playlistfolder`), élément, aire d'origine, aire destinataire. `SharedView.Build` calcule ce que l'aire ouverte reçoit : médias directs, dossiers partagés **et leurs sous-dossiers**, leur contenu (ajouts futurs compris), listes (directes ou dans un dossier partagé) et les médias qu'elles utilisent (contenus d'apps compris). Il est porté par `AreaAccess.Shared`.
- **Lien vivant** : `ScopedList` ajoute ces éléments à la vue de l'aire (`sharedIn`), donc toutes les pages, les choix de liste des écrans et la publication les traitent comme les siens, sans copie. Les médias qu'une liste partagée utilise sont lisibles (aperçus, lecture) mais **pas listés** dans la médiathèque ; seuls les médias partagés directement (à la racine) et ceux d'un dossier partagé le sont.
- **Droits** : dossiers partagés en **Écriture** (`FolderAccess.For`, plafonnés par l'accès au domaine), listes en écriture (`PlaylistOps.RightOf`), médias modifiables (`FolderOps.CanEditFile`). **Renommer / supprimer un dossier**, **supprimer un média ou une liste** et **repartager** restent à l'aire d'origine (`ScopedList.RemoveAll` ne touche que l'aire ; `Delete` et `PlaylistOps.Remove` écartent les éléments reçus).
- **Partager** (`AreaShareOps`, `AreaSharing`) : propriétaire (ou administrateur) d'un dossier, droit d'écriture sur le média ou la liste ; vers **ses propres aires** seulement ; pas vers l'aire d'origine. `SetTargets` (cocher / décocher les aires d'un élément, ou ajouter pour plusieurs), `Unshare`, `Leave` (l'aire qui reçoit : administrateur ou écriture sur le domaine), `Revoke` (retire aux écrans de l'aire destinataire la liste qui n'est plus visible, retire des listes de cette aire les médias qui ne sont plus lisibles), `Forget` (suppression ou **déplacement vers une autre aire** d'un élément).
- **Interface** : `AreaShareDialog` (liste des aires avec leurs écrans, rappel « lien vivant, pas une copie ») ouverte par « Partager avec d'autres aires… » dans le menu des médias, des dossiers de médias, des listes et des dossiers de listes, et dans la barre de sélection des médias. Côté destinataire : pastille « lien » + nom de l'aire sur les médias partagés, les dossiers (carte) et les listes ; « Ne plus voir » ; tiroir d'un média : « Partagé par l'aire … » ou « Partagé avec » (aires, ✕ pour retirer, « Ajouter une aire »).
- **App Android / mobile** : en haut à gauche, une **liste déroulante des aires** remplace le nom de l'organisation (`MainLayout.razor`, `.app-area`), avec les mêmes aires que le sélecteur du menu latéral du PC ; le nom de l'organisation reste s'il n'y a qu'une aire.
- **Tests** : `AreaShareTests` (visibilité et modification, suppression refusée, liste partagée et médias lisibles, retrait côté écrans, dossier et sous-dossiers, partage seulement depuis l'aire d'origine, « Ne plus voir »). 9 tests déjà en échec avant ces travaux.
- **Pas fait / limites** : les contenus (apps) qu'une liste partagée utilise ne sont pas détachés des autres aires au retrait du partage (seuls les médias de leurs listes le sont) ; supprimer un élément partagé ne prévient pas l'aire qui le reçoit ; filtres « Partagés » / « Reçus » non codés. **Pas vu** : aucun écran dans le navigateur, ni l'app Android réelle ; diffusion d'une liste partagée sur un écran réel à vérifier.

## 67. Écrans par blocs aire › zone et glisser-déposer (18 octobre 2026) : retiré (voir §70)

Maquettes montrées dans la conversation. Première version (bouton « Par aire » et tuiles simplifiées) **remplacée** : l'affichage est automatique, avec les cartes habituelles. Décisions : plus de bouton ni de tri (les écrans sont classés par nom) ; le planning est conservé quand l'écran change de zone dans la même aire, retiré quand il change d'aire.

- **Affichage** (page Écrans) : dès qu'on voit **plusieurs aires ou plusieurs zones**, les écrans sont rangés en blocs : un titre par aire (nombre d'écrans et en ligne, « aire ouverte » ou « Ouvrir cette aire ») puis un sous-titre par zone. Une aire et une zone : la grille d'avant, sans titre. Une aire, plusieurs zones : titres de zones seulement. Un **administrateur voit toutes les aires et toutes les zones de l'organisation, même vides** (« Glissez un écran ici »), pour y placer un écran ; un membre, celles de son périmètre. `Screens.razor` : `Grouped`, `LoadGroups` (appelé par `Reload`), `Card(entry)` (le balisage d'origine d'une carte, sorti de la boucle pour servir à la grille à plat et aux blocs), `LightCard` (carte de consultation des écrans d'une autre aire : état, appareil, liste ; un clic ouvre cette aire). Le filtre « Zone » disparaît quand les blocs sont affichés ; le menu « Tri » est retiré. App mobile : mêmes blocs, sans glisser-déposer.
- **Glisser-déposer** (droit « déplacer les écrans » ou administrateur) : une carte se glisse sur une **zone** ou sur l'**en-tête d'une aire** (zone par défaut) ; la cible s'illumine ; une carte cochée entraîne toute la sélection. Les murs et leurs écrans ne se déplacent pas (on dissout le mur). Bandeau « … déplacé vers Magasin › Défaut » avec **Annuler** ; en cas de changement d'aire, avertissement (liste de l'ancienne aire plus choisie, contenu publié conservé, planning propre retiré). Les écrans sont notifiés.
- **Nouveau droit de niveau** : `AccessLevel.MoveScreens` — « Permettre le déplacement des écrans dans d'autres aires de gestion / zones » (Membres › niveaux › Accès aux écrans › En plus ; demande « Voir et modifier » ; pris en compte dans `Normalize`, le résumé du niveau et `AreaAccess.CanMoveScreens` = administrateur ou ce droit). Il couvre zones **et** aires, dans les aires du périmètre ; la gestion des zones seule permet toujours de changer un écran de zone dans une aire qu'on gère.
- **Serveur** (`AreaOps`) : `PlaceScreen` (zone ou aire cible, droits, murs), `MoveScreen(…, adminOnly: false)` pour les membres qui ont le droit (la boîte « Autre aire… » reste réservée aux administrateurs), `PlaceOf` / `Restore` (annulation).
- **Tests** : `ScreenPlaceTests` (déplacement complet et annulation, droit de déplacement, gestion des zones seule, planning conservé, zone de mur refusée). 9 tests déjà en échec avant ces travaux.
- **Pas vu** : la page dans le navigateur (le glisser-déposer Blazor et la mise en page des blocs n'ont pas été essayés en vrai). Limites : les cartes des autres aires n'ont ni réglages ni aperçu (on les modifie en ouvrant leur aire) ; le filtre « À publier » ne s'applique qu'à l'aire ouverte.

## 68. Écrans : retouches des blocs aire › zone (18 octobre 2026) : retiré (voir §70)

- **Vue liste** : les écrans des autres aires reprennent la rangée habituelle (`LightRow`, même balisage que les rangées de l'aire ouverte) au lieu d'une carte ; toutes les aires et zones ont le même aspect en liste.
- **Connecter un écran** : un **tiroir** à droite (`.lx-drawer`, comme l'aperçu d'un média) remplace la fenêtre modale ; un champ par ligne, boutons Annuler / Connecter en bas.
- **Édition d'un écran d'une autre aire** : un clic sur sa carte change d'aire puis **ouvre directement son édition** (paramètre `?edit=<id>` lu à l'ouverture de la page puis retiré de l'adresse) ; avant, il fallait cliquer une seconde fois après le rechargement.
- **Curseur** : la règle globale `.pl-card[draggable]` donnait une main « déplacer » à toutes les cartes d'écran ; elles gardent un curseur normal (cliquer = éditer), le glisser-déposer fonctionne toujours.
- **Aperçu des autres aires** : leurs cartes et rangées montrent l'aperçu de la liste de l'écran (une vignette par zone de découpage, `LightPreview`), lu dans les listes et médias de toute l'organisation, comme l'aire ouverte.
- **Pas vu** : ces écrans dans le navigateur.

## 69. Niveaux de droits : le périmètre passe sur le membre (19 octobre 2026) : codé

Maquette montrée dans la conversation. Décisions : **un seul niveau par membre** (pas de rôle par aire) ; le **niveau dit ce qu'on peut faire**, le **périmètre dit ce qu'on voit** ; le périmètre garde la granularité aire › zone › **écran** ; il **suit la personne** quand elle change de niveau.

- **Modèle** : `Perimeter` (`User.Perimeter`) = aires, zones, écrans (un élément coché donne tout ce qu'il contient, ajouts futurs compris) + visibilité des dossiers des autres par domaine (tous, tous en lecture, ou ceux qu'on lui partage). `IPerimeter` est implémenté par `Perimeter` et par `AccessLevel` (dont les champs aires / zones / écrans / visibilité ne servent plus qu'à la migration).
- **Migration** (`AccessLevels.Migrate`) : chaque membre sans périmètre reprend celui de son niveau (`Perimeter.From`) : personne ne voit autre chose qu'avant. Tant qu'un membre n'en a pas, `AreaAccess.For` lit celui de son niveau.
- **Serveur** : `AreaAccess.For` construit le `LevelScope` depuis le périmètre du membre ; `AreaAccess.Perimeter` et `AreaAccess.VisibilityOf` (périmètre, sinon niveau) remplacent la lecture directe du niveau dans `FolderAccess.For` ; `AccessLevels.Save` n'exige plus de périmètre pour un niveau ; `AccessLevels.Forget` retire aussi une aire ou une zone supprimée des périmètres des membres ; page Aires de gestion (membres d'une aire) : via le périmètre.
- **Niveaux** (Membres › Niveaux de droits) : plus d'onglet « Périmètre », plus de colonne « Périmètre » ; l'onglet Listes de lecture / Médiathèque ne règle plus que l'accès et les **dossiers partagés avec ce niveau** (dossier par dossier) ; le choix « tous / les siens » est sur le membre.
- **Membres** (fenêtre d'invitation et de modification) : niveau, puis **Périmètre** (arbre aires › zones › écrans à trois états, comme avant mais ici), visibilité des dossiers (si l'organisation a activé les dossiers) et un résumé « pourra / dans seulement / soit n aires · n zones · n écrans ». Un administrateur n'a pas de périmètre. La liste des membres affiche le périmètre de chacun.
- **Tests** : `PerimeterTests` (un niveau, plusieurs équipes ; changer de niveau garde le périmètre ; migration ; visibilité des dossiers ; suppression d'une zone) ; `AccessLevelTests` adapté (un niveau n'a plus besoin de périmètre). 9 tests déjà en échec avant ces travaux.
- **Pas vu** : la fenêtre membre et l'onglet niveau dans le navigateur. Les anciens champs de périmètre restent dans les niveaux enregistrés (relecture en cas de retour en arrière) mais ne sont plus modifiables.

## 70. Déplacer un dossier dans un autre dossier (19 octobre 2026) : codé

Médiathèque et listes de lecture, **PC, mobile et app Android**. Décision : un dossier déplacé **à la racine est dissous** (après confirmation) : ses fichiers (ou listes) et ses sous-dossiers vont à la racine de l'aire, le dossier disparaît, rien n'est supprimé.

- **Interface** : menu du dossier (⋮ au survol sur PC, appui long dans l'app) › **Déplacer…** (propriétaire du dossier) ouvre `FolderReparentDialog` : liste de destinations (la racine, puis les dossiers de l'aire en arborescence, chacun avec la raison de son refus : dans lui-même, lecture seule, nom déjà pris, 4 niveaux au plus, autre aire). Choisir la racine affiche l'avertissement « Ce dossier sera dissous… » avec le nombre de fichiers et de sous-dossiers, et le bouton devient « Dissoudre et envoyer à la racine ». Lignes tactiles (44 px), pleine largeur dans l'app. **PC** : on peut aussi glisser un dossier sur un autre (Médiathèque et Listes) ; le glisser vers la racine n'existe pas (la dissolution passe toujours par la confirmation).
- **Serveur** : `FolderMove` (descendants, hauteur, règles de destination), `FolderOps.MoveFolder` / `MoveTargets` / `ContentsOf` et leurs équivalents `PlaylistFolderOps`, `MediaLibrary` et `PlaylistLibrary`. Règles : propriétaire seulement ; écriture sur le dossier d'arrivée ; même aire ; pas dans lui-même ni un descendant ; pas deux dossiers du même nom au même endroit ; `FolderAccess.MaxDepth` niveaux. Dissolution : un sous-dossier dont le nom existe déjà à la racine bloque (message). Les fichiers dissous gardent un propriétaire (celui du dossier à défaut) pour rester visibles de lui à la racine.
- **Tests** : `FolderMoveTests` (déplacement, refus, profondeur, dissolution médias et listes, destinations).
- **Pas vu** : la fenêtre et le glisser dans le navigateur et sur Android.

## 70. Écrans : retour à la grille de base, filtre « Aire de gestion » (19 octobre 2026) : codé

- **Retiré** : l'affichage par blocs aire › zone (§67-68), le glisser-déposer des cartes et son bandeau « Annuler », le droit de niveau « déplacer les écrans » (`MoveScreens`, `CanMoveScreens`), `AreaOps.PlaceScreen` / `PlaceOf` / `Restore`, le paramètre `adminOnly` de `MoveScreen` et les tests `ScreenPlaceTests`. La boîte « Autre aire… » (administrateurs) reste.
- **Affichage de base** : une seule grille à plat (cartes ou liste), puis « Connecter un écran ».
- **Filtre « Aire de gestion »** (menu Filtres, seulement si la personne voit plusieurs aires) : **Tous** par défaut (écrans de l'aire ouverte avec leurs cartes habituelles, puis ceux des autres aires en cartes de consultation avec le nom de leur aire ; un clic ouvre leur édition) ; choisir une aire ne garde que ses écrans. Les filtres Zone et « À publier » sont propres à l'aire ouverte : ils masquent les autres aires. Le filtre est mémorisé le temps de la page.
- **Vu** : filtre Tous / une aire dans le navigateur (3 aires, 4 écrans, compte neuf). **Pas vu** : vue liste et mode simple.

## 71. Dossiers en forme de dossier, hors du bloc (19 octobre 2026) : codé

Maquette validée : `maquettes/listes-dossiers-apercu.html` (couleur Nuit retenue ; variantes Signal et Clair écartées). Trois propositions de navigation précédentes (arbre, colonnes, tout à plat) refusées : trop chargées.

- **Médiathèque et Listes de lecture** : les dossiers sortent du bloc « Dossiers » (plus de cadre ni de titre) et s'affichent sous l'en-tête, avant le bloc Médias / Listes ; rien n'apparaît s'il n'y a pas de dossier (PC).
- **Forme** (`FolderShape.razor`, styles `.lx-fcard` / `.lx-fzone`) : dos et onglet Nuit `#0B1F3A`, rabat `#1D3B63` avec le carré Signal du logo ; jusqu'à trois vignettes du contenu (derniers médias, ou première vignette des dernières listes) qui dépassent, inclinées, et se soulèvent au survol ; sur le rabat le nombre d'éléments (« 3 listes », « 2 médias », « Vide », « éléments » si dossiers et contenus sont mélangés) et la pastille d'accès (partagé, écriture, lecture seule, aire d'origine ; plus de pastille « Privé »). Sous le dossier : son nom et « modifié le … » (dernier média ajouté, dernière liste modifiée) ou « créé le … ».
- **Glisser-déposer** : un dossier cible prend le dos Signal et ses vignettes se soulèvent. ⋮ au survol sur l'onglet (PC), toujours visible au téléphone.
- **Téléphone (navigateur)** : deux colonnes. **App Android** : bande qui défile (dossiers de 140 px), « Nouveau dossier » en pointillés au bout ; la bande garde sa marge à gauche (`scroll-padding`).
- **Vu** : PC, navigateur mobile et look app, médiathèque et listes (instance de test, 3 dossiers de médias, 3 dossiers de listes).

## 72. Boutons « Partager » en bleu vif (19 octobre 2026) : codé

- Classe `.btn-share` (`#1F6FD1`, texte blanc, survol `#1A62BB`) sur les boutons Partager : en-tête d'un dossier (Médiathèque, Listes de lecture), aperçu d'un média, « Partager avec N aires ». Même taille que les autres boutons (`.btn`), sauf dans l'aperçu d'un média où tous les boutons du pied sont petits.
- Pourquoi : à côté d'un bouton Nuit (« Nouvelle liste », « Importer »), un second bouton Nuit se confondait ; le bleu vif les distingue. Hors charte (le Signal `#3B82C4` reste l'accent du logo).
- Dans l'app Android, le bouton garde son rang (après le bouton principal).

## 73. Fil de la liste de lecture : colonne des apps et glisser-déposer (19 octobre 2026) : codé

Maquette validée : `maquettes/playlist-fil-glisser-deposer.html` (PC seulement ; l'app Android viendra plus tard).

- **Colonne « Ajouter au fil »** entre le menu et le fil : apps activées en liste, par catégorie (Médiathèque, Drive, Web, puis les autres), recherche. Les widgets d'écran n'y sont pas (ce ne sont pas des apps). On glisse une app dans le fil, ou un clic l'ajoute à la fin, à vide (« À configurer »). Remplace le menu « Ajouter un contenu », la galerie des apps et les boutons « Insérer ici ».
- **Frise de la boucle** : chaque contenu diffusé y prend une place proportionnelle à sa durée, à la couleur de son app ; un clic amène le contenu à l'écran. Graduations 0 / ¼ / ½ / ¾ / fin.
- **Vues Lignes et Cartes** (mémorisées dans le navigateur, `pl-flow`). Lignes : fil vertical numéroté, heure de départ sous le numéro, aperçu de 208 px (160 px sous 1 440 px de large), nom en évidence, app et « Régler » / « Configurer », scans du QR code, durée en grand, menu ⋮. Cartes : grille, numéro, durée et scans sur l'aperçu, « démarre à … ». Fin du fil : zone de dépôt « ↺ » (la boucle recommence au contenu 1).
- **Glisser-déposer** (`playlist-flow.js`, glisser natif) : un trait bleu montre l'endroit (au-dessus / au-dessous en Lignes, à gauche / à droite en Cartes) ; réordonner les contenus de la même façon. `sort-list.js` n'est plus utilisé sur cette page.
- **Menu ⋮** : Configurer…, Renommer (sur place, contenus d'app), Changer la durée (sur place : `m:ss` ou secondes ; un clic sur la durée fait de même ; une app « jusqu'à la fin » passe en durée fixe), Dupliquer (le même contenu repasse juste après ; nouvelle adresse courte pour le QR), Désactiver (sans retirer) / Réactiver, QR code et scans (Growth), Retirer de la liste.
- **Désactivé** : nouveau champ `PlaylistItem.Off` ; le contenu garde sa place mais n'est pas publié (`Notifier.Items`), ni compté dans la boucle ; numéro « – » et « ignoré ». Copié avec la liste (`PlaylistFolders`).
- **Pas fait** : déposer des fichiers de l'ordinateur directement dans le fil (la phrase de la maquette n'a pas été reprise).
- **Vu** : PC dans le navigateur (instance de test) : ajout par clic et par glisser, déplacement en Lignes et en Cartes, durée, renommer, dupliquer, désactiver. Glisser simulé par script, pas à la souris. **Pas vu** : licence Growth avec scans réels, lecture seule, écran étroit.

## 74. États vides : texte et bouton d'action (19 octobre 2026) : codé

Maquette validée : `maquettes/etats-vides.html` (style de l'état vide existant : titre, une phrase, bouton, sans cadre ni icône).

- **Écrans** : le bouton d'en-tête « Ajouter » devient « Connecter un écran ». Aucun écran : « Aucun écran connecté », comment relier un écran par son code, bouton « Connecter un écran ». Aire sans écran alors que d'autres aires en ont : « Aucun écran dans « aire » ». Dans ces deux cas, la carte « Connecter un écran » de la grille n'est plus affichée (doublon). « Aucun écran trouvé » reste pour une recherche ou un filtre sans résultat.
- **Médiathèque** : vide → bouton « Importer » ; rien à la racine (tout est dans des dossiers) → « Aucun média à la racine » dans le bloc Médias, avec « Importer » ; dossier vide → « Importer dans « dossier » ».
- **Listes de lecture** : vide → « Nouvelle liste » ; rien à la racine → « Aucune liste à la racine » ; dossier vide → texte et bouton « Nouvelle liste ».
- Styles : `.empty p` (largeur et marge sous le texte), `.empty.compact` (dans un bloc).
- **Vu** (instance de test) : médiathèque vide / racine vide / dossier vide, listes racine vide / dossier vide, écrans sans écran. **Pas vu** : aire sans écran avec d'autres aires, droits en lecture seule (le bouton disparaît, le texte reste).

## 75. Carte écran allégée (19 octobre 2026) : codé

Maquette validée : `maquettes/carte-ecran-legere.html`. But : une carte plus basse, avec seulement l'information utile.

- **Vue cartes, PC** : la liste de lecture déroulante, Aperçu et Pause passent **dans l'aperçu (rectangle noir), au survol** ou au clavier (`.gc-ctl`, bande dégradée en bas de l'aperçu). Elle reste affichée tant qu'un menu qu'elle a ouvert l'est (choix par zone d'un écran découpé).
- **Mur** : « 2 sur 2 en ligne » est remplacé dans l'aperçu par la pastille dépliable (points d'état + « 2 écrans », liste des écrans au clic) ; elle ne figure plus sous le nom. L'état complet reste dans l'info-bulle.
- **Sous le nom** : une ligne « ▷ liste de lecture » rappelle ce qui passe (pour un écran découpé : le résumé des zones).
- **Au survol** : la pastille d'état (ou celle du mur) remonte au-dessus des commandes ; les pastilles de publication (« Affichée à… », « À publier ») s'effacent le temps du survol ; le bandeau rouge « Hors ligne depuis… » s'efface et Aperçu / Pause apparaissent grisés.
- **Au doigt (navigateur du téléphone)** : pas de survol, les commandes restent visibles et les pastilles au-dessus.
- **App Android** : inchangée (rangée sous le nom et état du mur en texte, classes `.gc-apponly`). **Vue liste** : inchangée.
- Code : fragments `WallPlRow` / `ScreenPlRow` (une seule rangée, rendue dans l'aperçu et, pour l'app, sous le nom).
- **Vu** (instance de test, 2 lecteurs web, mur 2 × 1) : carte au repos, survol, choix de la liste depuis la bande, liste des écrans du mur au-dessus des commandes, écran hors ligne. **Pas vu** : app Android, écran découpé en zones, licence de base (boutons verrouillés).

## 76. Intégrations : Mes intégrations et Store (20 octobre 2026) : codé

Maquette validée : `maquettes/integrations-store.html`. La page `/integrations` a trois onglets : **Mes intégrations**, **Intégrations Store** et **Comptes connectés** (inchangé).

- **De base, activées d'office** : Image, Diaporama d'images, Vidéo, Diaporama de vidéos, PDF, RSS, Page web, YouTube (`defaultOn` des manifestes, appliqué une fois par client par `AppService.ApplyDefaults`, donc dès la création du compte, plans Base et Growth). Le client peut les désactiver (interrupteur) ; elles ne quittent jamais Mes intégrations.
- **Mes intégrations** = intégrations de base + intégrations prises dans le Store (`Tenant.IntegrationsAdded`, même désactivées) + toute intégration active. Les autres (Canva, Google Slides / Docs, PowerPoint, Facebook, calendriers, Drives, Home Assistant) sont dans le **Store**, chacune avec un bouton orange **Utiliser** (`.btn-use`).
- **Utiliser** : l'intégration est activée et rangée dans Mes intégrations (`AppService.Mark`), son tiroir de configuration s'ouvre ; à sa fermeture on revient sur Mes intégrations, la carte clignote. Une plateforme sans connexion au fournisseur (Google, Microsoft, Canva, Dropbox non configurés) refuse avec un message, sans rien ajouter.
- **En rouge** : une intégration dont le compte n'est pas connecté, l'accès pas autorisé, la connexion pas configurée ou en échec (`hors-ligne` et `mise-a-jour` deviennent rouges dans `MineEntry`), avec son message et un bouton **Corriger** qui rouvre le tiroir. Google Slides / Docs et PowerPoint sont maintenant contrôlés sur le compte (accès Drive).
- **Tiroirs** : les fenêtres existantes (calendriers, Drives, Home Assistant, Canva) sont devenues des tiroirs à droite (`.as-drawer`) ; les apps simples (base, Facebook, Slides…) ont un tiroir commun : description, compte demandé (se connecter, voir le compte), usage, « Retirer de Mes intégrations » (hors intégrations de base).
- **Catégories** : boutons de filtre avec compteurs dans les deux onglets (Médias, Web, Documents et design, Calendriers, Fichiers et Drives, Réseaux sociaux, Maison connectée), groupes par catégorie, recherche, vue cartes ou liste.
- Code : `Integrations.razor` (lignes `Use`, `OpenFor`, `EndUse`, `GroupsOf`), `CatalogView` (`err`, `FlashId`, `id="cat-…"`), `app.css` (fin du fichier).
- **Vu** (compte neuf, serveur de test) : 8 intégrations de base actives, Store à 16, « Utiliser » sur Nextcloud (tiroir, puis carte rouge « À connecter »), Facebook (tiroir commun, désactivation, Retirer), CalDAV. **Pas vu** : un test réussi de bout en bout (aucun fournisseur configuré), Google / Microsoft / Canva, l'app mobile (`body.is-app`), un membre non administrateur. Les 19 tests en échec le sont aussi sans ces changements (lancés depuis un autre dossier de sortie).

## 77. Intégrations : refonte de la carte et de la liste (20 octobre 2026) : codé

Maquette validée : `maquettes/integrations-carte-liste.html` (variante A, tuile teintée). Remplace `CatalogView` sur la page Intégrations seulement (Widgets l'utilise toujours).

- **Carte** (`IntCard`, compacte : grille dès 176 px, aperçu de 88 px, tuile de 58 px, une seule ligne de pied) : case à cocher en haut à gauche et menu ⋯ (Mes intégrations) ou pastille du prérequis (Store) ; aperçu encadré avec l'icône de l'intégration sur une tuile arrondie teintée de sa couleur (`TileOf`, couleur et pictogramme d'`AppIcon.LookOf`) ; nom ; ligne d'usage suivie du compte connecté (Mes intégrations) ou **description sur 2 lignes (Store seulement)** ; pied : état + Gérer + interrupteur, ou **Utiliser**. En erreur : cadre rouge, message, **Corriger**.
- **Liste** (`IntRow`) : case, vignette encadrée, nom (description dans le Store ; message d'erreur sinon), état, utilisation ou prérequis, catégorie, actions + ⋯. Case d'en-tête = tout sélectionner. Colonnes d'usage et de catégorie masquées sous 1100 px.
- **Actions groupées** (barre `.lx-selbar`, Mes intégrations, administrateurs) : Activer, Désactiver, Retirer. Retirer ignore les intégrations de base et Home Assistant ; Activer refuse celles dont la connexion du fournisseur n'est pas configurée (message). La sélection est vidée au changement d'onglet.
- **Menu ⋯** : Gérer / Corriger, et « Retirer de Mes intégrations » (hors intégrations de base).
- **Vu** (compte neuf, serveur de test) : cartes et liste des deux onglets, cases, désactivation groupée, Retirer par le menu (Nextcloud revient dans le Store). **Pas vu** : Activer / Retirer groupés sur des sources de calendriers ou de Drives, l'app mobile (`body.is-app`), un membre non administrateur.

## 78. Intégrations : point d'état, détails et interrupteur sur la ligne du nom (20 octobre 2026) : codé

Maquette validée : `maquettes/integrations-carte-liste-v2.html` (point devant le nom, « Corriger » visible).

- **Mes intégrations** : le nom n'est plus un lien. Le menu ⋯ propose **Détails** (page descriptive : `/apps/{id}`, `/apps/agenda` pour les calendriers, `/apps/drive` pour les Drives ; rien pour Home Assistant), **Gérer** (tiroir de configuration) et **Retirer** (hors intégrations de base). Le bouton Gérer quitte les cartes et les lignes.
- **Store** : le nom est un lien vers les détails (curseur main, bleu et souligné au survol).
- **État** : la pastille « Active » est remplacée par un petit point devant le nom (`StateDot`) : vert actif, gris désactivé, rouge en erreur. La colonne État de la liste disparaît.
- **Carte** : l'interrupteur est sur la ligne du nom ; plus de pied de carte, sauf **Corriger** (en erreur) et **Utiliser** (Store). **Liste** : Corriger, interrupteur et ⋯ à droite.
- **Vu** (mesures dans la page, la capture d'écran n'a pas marché ce jour-là) : interrupteur et nom sur la même ligne, point à gauche du nom, aucune pastille, menu « Détails / Gérer / Retirer », 14 liens dans le Store (pas Home Assistant), ligne Nextcloud en erreur (point rouge, message, Corriger). **Pas vu** : le rendu en capture d'écran, la liste en largeur de bureau.

## 79. Intégration « Météo » : un contenu de liste de lecture (21 octobre 2026) : codé

Maquette validée : `maquettes/meteo-integration.html`. Nouvelle app `weather-slide` (nom : « Météo », catégorie Web dans les Intégrations, de base : `defaultOn`). Le widget d'écran `weather` est conservé ; les deux partagent le même service et le même cache.

- **Fournisseur** : **WeatherAPI.com** (offre gratuite, usage commercial permis avec mention de la source, d'où « WeatherAPI.com » discret en bas de la carte). Remplace Open-Meteo, dont la licence gratuite exclut le commercial. Clé : `Linkii:Weather:ApiKey` (secrets utilisateur ou variable `Linkii__Weather__ApiKey`) ; sans clé, le widget et le contenu affichent « La météo n'est pas configurée sur ce serveur ». Un seul appel par ville (`forecast.json?days=3&aqi=yes&alerts=yes&lang=fr`), cache serveur par ville (30 min ou 1 h selon l'option ; le widget : 10 min), dernière valeur connue si le service ne répond pas.
- **Une ville par contenu**, choisie dans le contenu (champ `city`, type `city`, zone « source » : « + Choisir une ville », « Rechercher », aperçu aussitôt). Deux villes = deux contenus. Aucun réglage à l'installation.
- **Position** : pleine page ou angle (haut/bas, gauche/droite), par le réglage générique « Position à l'écran » de la liste de lecture. **Comme tout contenu en angle**, la météo reste affichée pendant toute la boucle et réserve sa place (le contenu se réduit, rien ne se superpose) : c'est le comportement existant du player, la maquette montrait une superposition.
- **Réglages** : unité °C/°F, fond sombre/clair. **Options avancées** (⚙) : prévisions 3 jours, qualité de l'air, alertes météo (Europe), ressenti, vent, humidité, UV, lever/coucher du soleil, fréquence d'actualisation. Le serveur envoie toujours tout ; les interrupteurs sont appliqués par l'écran (`host.settings`) et par l'aperçu, qui les suit en direct.
- **Qualité de l'air** : indice américain 1 à 6 de WeatherAPI, en mots (Bonne, Modérée, Médiocre, Mauvaise, Très mauvaise, Dangereuse) avec pastille de couleur.
- **Alertes** : une seule bannière (la plus grave non expirée), orange, rouge pour Severe/Extreme, jaune pour Minor ; absente quand rien n'est en cours. **Europe seulement** (`WeatherService.IsEurope`, liste de pays) : hors Europe aucune alerte n'est envoyée et « Rechercher » affiche « ⚠ … Les alertes météo ne sont disponibles qu'en Europe ». La maquette grisait l'interrupteur selon la ville ; le formulaire générique ne le permet pas, l'aide du champ le précise à la place.
- **Pictogrammes** : le code de condition de WeatherAPI est converti en code WMO côté serveur (`WeatherService.Wmo`), ce qui laisse `wmoIcon` du player inchangé.
- Code : `WeatherService.cs` (réponse, cache, conversions), `Apps/Providers.cs` (`WeatherSlideProvider`), `Apps/weather-slide.json`, `Components/WeatherPreview.razor` + `ContentPreview.razor` (aperçu), `wwwroot/player/apps.js` (`weather-slide`) et `index.html` (`.wx-*`), `app.css` (`.wp-*`). Tests : `WeatherTests` (21).
- **Vu** : rendu du player (plein écran sombre et clair, angles bas droit et haut gauche, interrupteurs) dans une page de test avec des données d'exemple ; 21 tests. **Pas vu** : appel réel à WeatherAPI.com (pas de clé sur cette machine), fenêtre de configuration de bout en bout, présence des alertes et de la qualité de l'air dans l'offre gratuite (à confirmer à la première lecture), app Android.

**Mise à jour (21 octobre 2026) : aperçu fidèle et onglets sous l'aperçu.** Dans la fenêtre d'un contenu de liste de lecture, les onglets « Contenu / QR code » passent **sous l'aperçu** (colonne de gauche, `.ci-left`) pour toutes les apps qui ont un aperçu ; la colonne de droite commence par « Configuration ». Sans aperçu (horloge, texte…), les onglets restent en tête de la colonne de droite. L'aperçu de la Météo reproduit l'écran : **position choisie** (pleine page ou angle, via `PreviewPlace` → `Place`), **format portrait** (9:16, pleine page empilée, classe `wx-tall` aussi posée par le player quand l'écran est en portrait) et mêmes règles CSS que le player (`.wp-inner`, 1em = 1 % de la largeur).

**Configurer la clé WeatherAPI.com (§79).** Compte gratuit sur weatherapi.com, puis la clé « API Key » du tableau de bord. Elle se donne au serveur sous le nom `Linkii:Weather:ApiKey`, jamais dans un fichier suivi par git (pas dans `.claude/launch.json`) :
- **Poste de développement** : variable d'environnement utilisateur `Linkii__Weather__ApiKey` (le double `__` remplace `:`), valable quel que soit le mode de lancement ; le serveur doit être relancé depuis un nouveau terminal. PowerShell : `[Environment]::SetEnvironmentVariable("Linkii__Weather__ApiKey", "…", "User")`.
- **Secrets utilisateur .NET** (le projet a un `UserSecretsId`) : `dotnet user-secrets set "Linkii:Weather:ApiKey" "…"` depuis le dossier `Linkii.Poc`. Lus **seulement** avec `--environment Development`, ce que `dotnet run` ne fait pas par défaut.
- **Production** : la même variable `Linkii__Weather__ApiKey` sur le serveur.
- **Vérifier** : Intégrations › Météo › « Choisir une ville », saisir une ville, « Rechercher » : la carte s'affiche, sinon le bandeau d'erreur dit pourquoi (« La météo n'est pas configurée… » = clé absente). Le widget d'écran `weather` utilise la même clé.
- **Quotas** : l'offre gratuite autorise environ 1 million d'appels par mois ; le cache par ville (30 min, 1 h ou 10 min pour le widget) en consomme très peu, quel que soit le nombre d'écrans.

**Correction (21 octobre 2026).** Les onglets « Contenu / QR code » se déplacent **avec leur contenu** : pour une app à aperçu, la fenêtre devient une seule colonne (`.ci-cols.stack`, 680 px au plus, centrée) : aperçu, onglets, puis la configuration de l'onglet choisi. Toute la colonne défile. Remplace la mise à jour précédente (onglets seuls sous l'aperçu, configuration restée à droite).

## 80. Éditeur de compositions, version minimale (21 octobre 2026) : codé

Maquette validée : `maquettes/editeur-compositions.html` (référence : l'éditeur de modèles de Yodeck). Il **s'ajoute** aux contenus existants. Nouvelle app `composition` (voir §81 : nom « Compositions », à prendre dans le Store, pleine page seulement, durée fixe).

- **Document** : paramètre caché `doc` du contenu, JSON `{ v, w, h, bg, els }` en pixels de la page (1920 × 1080 par défaut ; portrait, 4K et taille libre de 320 à 7680 px). Calques : `text`, `shape` (rect, circle, line, arrow), `image` (`media` = identifiant dans la médiathèque), `qr` (`url`), `widget` (`clock`, `date`, `ticker`, `countdown`), avec `x, y, w, h, opacity, rot, hidden, locked`. Le paramètre caché `images` liste les images utilisées : elles comptent comme « utilisées » dans la médiathèque et apparaissent dans « Où ce média sert » (`MediaLibrary.MediaFields` : champ `hidden` + `accept`).
- **Serveur** (`Compositions.cs`) : `Clean` vérifie le document à l'enregistrement (400 000 caractères, 400 calques au plus, types connus, couleur de fond) et retire les adresses ; `Publish` (appelé par `Notifier.Snapshot`) remplace chaque image par son adresse `/media/…` (fichier prêt **du même client**, sinon le calque disparaît), calcule le tracé de chaque QR code (`QrCodes.Encode`, celui du document est ignoré) et retire les calques masqués. Les images partent dans `Urls` : l'écran les précharge, la composition s'affiche hors ligne.
- **Rendu commun** : `wwwroot/player/composition.js` (`LinkiiCompo`), utilisé par l'éditeur, son aperçu, le simulateur « Essayer sur… » et le player (app `composition`, mise à l'échelle sans déformation, bandes de la couleur du fond si le format de l'écran diffère). Couleurs, polices (liste fermée), nombres et tracés de QR sont filtrés ; tout le texte est échappé. Polices : IBM Plex Sans, Montserrat, Poppins, Playfair Display, Oswald (Google Fonts, chargées par le script), Arial.
- **Éditeur** : page `/compositions/{id}` (`CompositionEditor.razor`), interface en JS plein écran (`wwwroot/compo-editor.js` + `compo-editor.css`, classes `ce-*`). Comme la maquette : barre du haut (nom, format, annuler/rétablir, zoom, fond, calques, aperçu, annuler les modifications, Enregistrer ▾), rail Modèles / Médias / Texte / Éléments / Widgets, barre flottante selon l'objet, poignées, guides de centrage, double-clic pour écrire, panneau Calques (ordre par glisser, masquer, verrouiller), raccourcis (Ctrl+Z/Y/S/D, Suppr, flèches). « Image de fond… » ajoute une image pleine page verrouillée tout en bas. Appels serveur : `Save`, `Qr`, `Images`, `Leave`.
- **Modèles** : 6, génériques et **sans photo** (Horaires d'ouverture, Bienvenue, Orientation, Annonce, Chiffres clés, Journée portes ouvertes en portrait) ; les emplacements d'image (hachurés « Image à choisir ») se remplissent depuis la médiathèque, un emplacement vide n'est pas diffusé.
- **Entrées** : dans la liste de lecture, un clic (ou un glisser) sur « Composition » l'ajoute puis ouvre l'éditeur (une nouvelle composition demande le format et propose un modèle) ; « Modifier » / « Configurer… » rouvrent l'éditeur, la durée et le QR code de comptage restent dans la ligne et sa fenêtre. `/apps/composition/new` et `/apps/instance/{id}` mènent aussi à l'éditeur ; la fenêtre de réglages porte un lien « Ouvrir l'éditeur ».
- **Pas dans cette version** (écarté de la maquette) : « Mes modèles », compteur de scans par QR de la composition, météo, champs reliés à une source, rotation libre, import de fichiers depuis l'éditeur (lien vers la Médiathèque + « Actualiser »).
- **Vu** : dans une instance de test, ajout depuis la liste, choix du format et du modèle « Horaires d'ouverture », déplacement, enregistrement, rechargement (28 calques, QR réel), retour à la liste (« Modifier »), simulateur en paysage et en portrait. Tests : `CompositionsTests` (4), `AppsTests` (liste du catalogue). **Pas vu** : une image de la médiathèque dans l'éditeur et sur un écran (couvert par les tests seulement), écran réel et app Android (polices Google hors ligne : repli sur les polices du système), affichage sur un petit écran de PC (la barre du haut se tasse sous ~1100 px).

## 81. Menu et page Compositions, intégration « Compositions » (21 octobre 2026) : codé

Demande : la création et la gestion des compositions deviennent une **intégration à marquer « Utiliser »** ; dès qu'elle est dans Mes intégrations, un menu **Compositions** apparaît dans le menu latéral et ses compositions se placent dans les listes de lecture comme les autres contenus. Remplace l'entrée par la colonne des apps du §80 (qui créait une composition vide).

- **Intégration** : `Apps/composition.json`, nom « Compositions », **plus activée d'office** (pas de `defaultOn`) ; dans le Store, section « Médias », bouton « Utiliser ». Le tiroir propose « Ouvrir Compositions » au lieu de « Voir les contenus ».
- **Menu** : nouvelle page de niveau `LevelPages.Compositions` (`/compositions`, politique `Page:compositions`), visible si l'intégration est installée **et** que le niveau de droits peut l'utiliser (`AreaAccess.CanSee`, comme Intégrations et Widgets) ; placée sous Médiathèque, aussi dans la feuille « Plus » de l'app. Le menu se met à jour **sans recharger** : `AppService.InstallsChanged` (levé par Install, Disable et Uninstall) est écouté par `MainLayout`. Retirer l'intégration masque le menu ; les compositions existantes sont conservées et la page l'explique.
- **Page Compositions** (`CompositionsPage.razor`) : bouton « + Créer une composition » en haut à droite ; onglet **Mes compositions** (cartes avec aperçu fidèle `CompositionThumb`, format, listes qui l'utilisent ; Modifier, Dupliquer, Supprimer avec les listes touchées) ; onglet **Modèles** (galerie des 6 modèles, `LinkiiCompoEditor.gallery` ; un clic ouvre l'éditeur sur le modèle, `?tpl=n`).
- **Éditeur** : `/compositions/new` (rien n'est créé avant le premier « Enregistrer », donc plus de composition vide laissée derrière) et `/compositions/{id}` ; `?back=` pour le retour, `?addTo=<liste>&at=<n>` pour ajouter la composition à une liste dès son premier enregistrement.
- **Listes de lecture** : « Compositions » dans la colonne des apps (clic ou glisser) ouvre un choix : les compositions de Mes compositions (aperçu, format) ou « Créer une composition ». Une même composition peut servir dans plusieurs listes ; la retirer d'une liste ne la supprime plus (`RemoveItem`). La ligne de la liste montre l'aperçu de la composition (`CompositionThumb Contain`) et le lien « Modifier » ouvre l'éditeur.
- **Vu** : dans une instance de test (client sans l'intégration → menu absent ; Store › « Utiliser » → menu présent aussitôt), page vide, galerie, modèle ouvert dans l'éditeur, enregistrement avec retour (la composition apparaît avec son aperçu), choix dans une liste, création depuis la liste (ajoutée en position 2, aperçu dans la ligne). Tests : `CompositionsTests` (6, dont droits du niveau et événement d'installation) ; la suite complète garde les mêmes 19 échecs qu'avant ce travail. **Pas vu** : suppression et duplication depuis la page, retrait de l'intégration dans l'interface, membre avec niveau restreint, mode app Android.

## 82. Composition : fenêtre « Modifier » avec aperçu, positions et comptage des scans (21 octobre 2026) : codé

Maquette validée : `maquettes/composition-fenetre-modifier.html`. Remplace le renvoi direct vers l'éditeur du §80-81 : « Modifier » (et « Configurer… ») sur une composition de la liste ouvre **la fenêtre de configuration habituelle**, d'où l'on va dans l'éditeur.

- **Aperçu à gauche dans les deux onglets** (Contenu et QR code), onglets à droite : `ContentPreview` sait dessiner une composition (`kind = composition`, `CompositionThumb`, mêmes proportions que l'écran) ; `AppInstanceForm` garde les onglets en tête de la colonne de droite pour cette app (au lieu de la disposition « aperçu, onglets, configuration » en une colonne des autres apps). Rien sous l'aperçu.
- **Éditeur depuis l'onglet Contenu** : un clic sur l'aperçu (« ✎ Modifier dans l'éditeur » au survol) ou le bouton « Ouvrir l'éditeur » de l'encadré bleu, à droite. Le retour (`?back=`) ramène à la fenêtre de la liste ; les réglages non enregistrés de la fenêtre (position, durée, QR) ne sont pas conservés en partant dans l'éditeur.
- **Position à l'écran** : les mêmes que l'Image (pleine page et quatre angles). En angle, le player affiche la composition dans une fenêtre de 32 % de la largeur de la scène (18 % de haut au plus), au format de la composition (`composition.js`, `.widget.k-composition`).
- **Comptage des scans** : pas de mécanisme à part. On réutilise celui des listes de lecture (licence Growth, pleine page) : onglet « QR code » de la fenêtre, QR de comptage au premier plan, à placer à la souris. Son aperçu montre maintenant la composition (`QrPlacer`), donc on le place où l'on veut sur la page. Les QR codes dessinés dans la composition (éditeur) ne sont pas comptés ; le panneau Widgets de l'éditeur le dit et renvoie à l'onglet « QR code » du contenu. Raison : un seul compteur et des statistiques uniformes, sans lier un QR de l'éditeur à une liste précise (une composition peut servir dans plusieurs listes).
- **Vu** : fenêtre ouverte depuis la ligne (aperçu à gauche, onglets à droite, 5 positions), onglet QR code avec la composition en fond et le QR de comptage à placer. Test `CompositionsTests` (positions identiques à l'Image, angle conservé à la publication). **Pas vu** : composition en angle sur un écran réel, retour de l'éditeur vers la fenêtre, enregistrement d'un QR de comptage de bout en bout.

**Correction (21 octobre 2026) : QR de comptage et aperçu d'une composition.** (1) Le QR de comptage d'une composition se place et se mesure en **% de sa page**, pas de l'écran : l'aperçu de l'onglet « QR code » et celui de l'onglet Contenu ont le format de la page (`Compositions.StageStyle`, 16:9, portrait ou autre) ; le player ne dessine plus ce QR par-dessus la zone (`addQr` ignoré pour l'app composition) mais **dans la page mise à l'échelle** (`composition.js`, `addQr`), donc à la même place sur tous les formats d'écran (avant, un écran portrait le déplaçait). (2) L'aperçu de l'onglet Contenu s'affichait vide quand les écrans de la liste sont en portrait (cadre sans largeur) : il a maintenant la taille de la page. (3) Taille minimale du QR de comptage **3 %** (au lieu de 8 %), pour toutes les apps (`QrTracking.MinSize`, curseur de la fenêtre et player).

## 83. Même mise en page pour l'édition de tous les contenus à aperçu (21 octobre 2026) : codé

Maquette validée : `maquettes/fenetre-media-layout-unique.html`. Remplace la disposition « aperçu, onglets, configuration » en une colonne (correction du §79) : toute fenêtre de contenu qui a un aperçu (Image, Vidéo, Diaporamas, PDF, Canva, documents Google et PowerPoint, Drive, Page web, YouTube, flux, calendrier, Météo, Compositions) s'ouvre ainsi :

- **Tiroir élargi** : jusqu'à 1 480 px (`.modal.app-modal.ci-modal`, avant 1 120 px).
- **Aperçu à gauche**, grand et identique dans les onglets Contenu et QR code (aperçu de l'écran où le QR se place).
- **À droite** (340 à 460 px) : onglets « Contenu / QR code » en tête, puis « Configuration » (champs de l'app), puis « Dans cette liste de lecture » (position, durée). Pour une composition : pas de « Configuration », l'encadré bleu vers l'éditeur puis la liste.
- La disposition `ci-cols.stack` et le paramètre `tabsUnder` d'`AppInstanceForm` sont supprimés. Les apps sans aperçu (texte, horloge, compte à rebours…) gardent leur colonne unique.
- Aperçu d'une composition : page dans un cadre gris, informations en dessous (format, orientation, nombre d'éléments : `Compositions.ElementCount`).
- **Vu** : composition (aperçu, onglets à droite, encadré bleu, position et durée) et Diaporama images vide (« + Ajouter des images », configuration, liste de lecture, ⚙ en en-tête). **Pas vu** : les autres apps (Vidéo, PDF, YouTube, Météo…), le portrait d'un autre contenu que la composition, les écrans de moins de 1 100 px.

**Complément (21 octobre 2026) : cadre gris de l'aperçu.** Comme la maquette (`.pvbox`), l'aperçu de **tous** les contenus est dans un cadre gris (`.pv-box` : fond gris clair, bordure, 16 px de marge, ombre sur la page) dans les deux onglets : `ContentPreview` (onglet Contenu, y compris la zone vide « + Ajouter » à pointillés blancs) et `QrPlacer` (onglet QR code). Les informations d'une composition (format, éléments) restent sous le cadre.

**Correction (21 octobre 2026) : composition trop grosse à l'apparition dans le player.** La scène d'une composition fait la taille de sa page (ex. 2160 × 3840) et n'est mise à l'échelle qu'une fois la taille de la zone connue ; à l'insertion l'élément n'a pas encore de taille (le calcul s'arrêtait), si bien que la page s'affichait en grand jusqu'au tick suivant (1 s), puis se réduisait. `composition.js` : la scène reste **cachée jusqu'au premier calcul réussi**, et l'échelle suit la zone par un `ResizeObserver` (plus un recalcul à l'image suivante) en plus du tick. Vu : portrait simulé, échelle 0,5625 appliquée et scène visible. À confirmer sur l'écran réel (le Service Worker du player peut garder l'ancien script : recharger deux fois).

## 84. Compositions : polices embarquées, Mes modèles, import d'images, rotation, aimantation, renommer (21 octobre 2026) : codé

- **Polices embarquées** (hors ligne) : les 5 polices des compositions (IBM Plex Sans, Montserrat, Poppins, Playfair Display, Oswald ; licence SIL OFL, `player/fonts/LICENCE.txt`) sont servies par Linkii depuis `wwwroot/player/fonts/` (`compo-fonts.css` + 26 fichiers woff2, sous-ensembles latin et latin-ext, 616 Ko) au lieu de Google Fonts. Sous `/player/`, le Service Worker du player les garde ; le player les télécharge aussi en arrière-plan 8 s après son démarrage, pour qu'une composition ait ses polices même si l'écran perd internet avant de l'avoir jouée. Pour les régénérer : demander la CSS Google Fonts (mêmes familles et graisses que `composition.js`) avec un navigateur récent, garder les blocs `latin` et `latin-ext`, télécharger chaque woff2 et remplacer les adresses par les noms de fichiers.
- **Mes modèles** : nouvelle donnée `CompositionTemplate` (`Db.CompositionTemplates`, rangée dans l'aire comme les compositions, `ClientDb.CompositionTemplates`), opérations `CompositionTemplates.Save / Rename / Delete` (nom obligatoire, 80 caractères, document vérifié par `Compositions.Clean`, 200 modèles par aire au plus). Ses images comptent comme utilisées (`MediaLibrary.UnusedIds`). Entrées : éditeur › Enregistrer ▾ › « Enregistrer comme modèle… » (fenêtre avec le nom) ; page Compositions › menu ⋮ d'une composition › « Enregistrer comme modèle ». Utilisation : page Compositions › Modèles › **Galerie / Mes modèles** (cartes avec aperçu, « Créer une composition », Renommer, Supprimer) → `/compositions/new?mytpl=…` (rien n'est créé avant « Enregistrer ») ; éditeur › panneau Modèles › onglets Galerie / Mes modèles (remplace la page en cours, × pour supprimer un modèle).
- **Import d'images depuis l'éditeur** : panneau Médias › « Importer… » ouvre un sélecteur de fichiers (`InputFile` de `CompositionEditor.razor`, images seulement, 10 à la fois) ; les images entrent dans la médiathèque de l'aire (`MediaImporter`, comme la page Médiathèque) et la première se place sur la page (ou remplace l'image choisie, ou devient le fond, selon le mode du panneau). Bouton grisé si le niveau de droits n'écrit pas dans la médiathèque.
- **Rotation libre** : poignée ronde au-dessus de l'élément sélectionné (Maj : pas de 15° ; aimantée à ±4° des multiples de 45°) et champ « ↻ » (degrés) dans la barre contextuelle, pour tous les types de calques. Le cadre de sélection et ses poignées tournent avec l'élément ; le redimensionnement d'un élément tourné se fait dans ses propres axes, le côté opposé restant fixe.
- **Aimantation entre éléments** : en déplaçant, les bords gauche/centre/droite et haut/milieu/bas s'aimantent (8 px à l'écran) à ceux de la page et des autres éléments visibles, avec un guide rose ; Alt désactive l'aimantation.
- **Renommer** une composition depuis la page Compositions (menu ⋮ › Renommer…, `AppService.RenameInstance`) ; aussi pour les modèles.
- **Réglages gardés en ouvrant l'éditeur** : depuis la fenêtre d'un contenu d'une liste, « Ouvrir l'éditeur » (encadré bleu) et le clic sur l'aperçu **enregistrent d'abord** les réglages de la fenêtre (position, durée, QR code : `PlaylistEditor.OpenCompositionEditor` → `SaveAll`) ; s'ils sont refusés, la fenêtre reste ouverte avec le message.
- Correction : le menu ⋮ des cartes de la page Compositions était rogné par la carte.
- **Vu** (instance de test) : polices chargées depuis `/player/fonts`, modèle enregistré depuis la page et depuis l'éditeur (fenêtre), composition créée à partir de Mes modèles (« Modifications non enregistrées »), rotation à 20° par la poignée, aimantation du bord gauche d'un QR sur un texte voisin (guides), import d'une image PNG (ajoutée à la médiathèque et placée sur la page), durée changée puis « Ouvrir l'éditeur » (durée 0:12 gardée), renommage depuis la page. Tests `CompositionsTests` (modèles : création, refus, renommage, suppression, isolation entre clients ; renommage d'une composition). **Pas vu** : polices sur un écran réel sans internet, import de plusieurs images ou d'un fichier refusé, rotation d'une image ou d'une forme redimensionnée.

## 85. Compositions, étape 3 : blocs d'intégration et champs reliés dans les textes (21 octobre 2026) : codé (scènes 1, 2 et 4 de la maquette)

Maquette validée : `maquettes/editeur-compositions-etape3.html` (6 scènes). Codées ici : 1 (intégrations dans l'éditeur), 2 (champ dans un texte), 4 (panneau Données, « Noms des champs »). Restent : 3 (liste reliée), 5 (« Afficher si… »), 6 (source « Horaires d'ouverture ») et la source « Tableau ».

- **Sources** : un contenu configuré de Mes intégrations à données — Météo (`weather-slide`), Calendrier partagé (`agenda`), Flux RSS (`rss`), Capteurs Home Assistant (`ha-sensors`) : `Compositions.SourceApps`. L'éditeur les liste (`AppService.CompositionSources`, contenus complets visibles de l'aire) et lit leurs données actuelles (`AppService.SourceData`, le même `provider.Fetch` que l'écran) par `CompositionEditor.Sources()` / `SourceData(id)`. Une source garde ses propres réglages (ville, calendrier…) : la modifier change toutes les compositions qui l'utilisent ; bouton « ⚙ Réglages » de la barre (nouvel onglet, `/apps/instance/{id}`).
- **Bloc d'intégration** : nouveau calque `{ type: "app", app, source, font, size, weight, color, bg: none|light|dark, view: days|now (météo), rows (listes) }`, posé depuis le panneau Widgets › « Mes intégrations » (cliquer ou glisser). Rendu commun éditeur / écran dans `player/composition.js` (`block`) : météo (pictogramme, température, ville, conditions, 3 jours), calendrier (événements à venir : heure, titre, lieu), RSS (titres), capteurs (nom, valeur). Tout est en `em` de la taille de police du bloc : la police et la couleur de la composition s'appliquent. Les intégrations plein écran (page web, Facebook) sont grisées dans le panneau.
- **Champ relié dans un texte** : jeton `{{source:clé}}` dans le texte (`Compositions.FieldToken`, source sur 32 chiffres hexadécimaux). Champs (`LinkiiCompo.fields`) : météo (ville, température, avec unité, ressenti, conditions, pictogramme, min., max., vent, humidité), calendrier (prochain événement : titre, heure, jour, lieu ; nombre d'événements à venir aujourd'hui ; titre), RSS (nom du flux, titres des articles 1 à 3), capteurs (une entrée par entité). Insertion : bouton « { } Champ » de la barre d'un texte, ou « { » pendant l'écriture ; la liste montre la valeur actuelle de chaque champ ; le champ entre au curseur comme une pastille insécable (`contenteditable=false`), relue en jeton à la fin de l'écriture. « Valeur de secours… » (par élément, 40 caractères, « — » par défaut) quand la source n'a pas de valeur.
- **Panneau Données** (rail) : sources utilisées (nombre d'éléments, état de la dernière lecture, ↻ relire), sources introuvables, autres sources disponibles (« + Poser un bloc »), liens « + Ajouter une source » (Intégrations, nouvel onglet) et « ↻ Actualiser ». Bascule « Données réelles / Noms des champs » dans la barre du haut : en mode noms, chaque champ s'affiche « {Température} ».
- **Publication** : `Compositions.Publish(settings, fichier, source)` fige chaque source utilisée (même client, intégration de `SourceApps`) : `Settings["src:{id}"] = { App, Settings }` et ses secrets chiffrés dans `PublishedItem.Secrets["src:{id}:{clé}"]` (jamais envoyés à l'écran). Une source introuvable retire ses blocs ; ses champs restent et affichent la valeur de secours. `Clean` refuse un bloc sans source ou d'une intégration hors `SourceApps`.
- **Écran** : nouvelles routes `GET /api/data/{élément}/{source}` (jeton de l'écran ; la composition doit être dans ses éléments publiés et utiliser cette source : `Compositions.SourceItem`) et `/api/preview/data/{élément}/{source}` (simulateur). Côté player, `host.source(src, draw, ms)` (même cache hors ligne `lk_d_…` que `host.data`, qui partage maintenant la fonction `feed`) ; l'app « composition » redessine seulement les calques reliés (`LinkiiCompo.refresh`) à chaque lecture. Rythme : météo 10 min, calendrier 1 min, RSS 5 min, capteurs 30 s.
- Scripts `composition.js` et `compo-editor.js` versionnés (`?v=85`) dans `App.razor` : le navigateur reprend les nouvelles versions.
- **Vu** (instance de test) : contenu Flux RSS (BBC News) posé comme bloc (titres réels), champ « Article 1 · titre » inséré au curseur par la liste des champs puis gardé après l'écriture, mode « Noms des champs », panneau Données, enregistrement, puis simulateur « Essayer » de la liste : bloc en fond sombre et champ rempli par le player. Test `Blocs_et_champs_relies_figent_leurs_sources_a_la_publication`. **Non vu** : météo (clé WeatherAPI à fournir), calendrier et capteurs réels, un vrai écran (route `/api/data/{élément}/{source}`, couverte par le test de `SourceItem`), la WebView Android.
- Correction (page Compositions) : les vignettes des cartes « Mes compositions » et « Mes modèles » sont dans une case 16:9 fixe, page entière centrée (`CompositionThumb Contain`). Une page en portrait, dont la hauteur suivait la largeur, faisait apparaître puis disparaître la barre de défilement et la vignette sautait en boucle.

## 86. Médiathèque : origine en vue Photos, retrait d'une source Drive (21 octobre 2026) : codé

- **Vue Photos** : la pastille d'origine (OneDrive, GDrive, Canva, PDF) s'affiche en haut à droite de chaque vignette, comme sur les cartes. Google Drive s'écrit « GDrive » partout (cartes, liste, Photos).
- **Retrait d'une source Drive** (Intégrations › Retirer, une ou plusieurs à la fois) : les dossiers de cette source sont retirés (`DriveService.RemoveFolder`) et les fichiers qu'ils avaient copiés disparaissent de la médiathèque et du disque. Avant, ils restaient sans pouvoir être supprimés, sauf à rouvrir la fenêtre de la source et retirer chaque dossier. Règle : le retrait d'une intégration qui crée des médias emporte ces médias. Cas traité pour l'instant : les sources Drive (Google Drive, OneDrive, Nextcloud, SFTP, Dropbox) ; Canva et PDF gardent leurs exports tant qu'une liste les utilise.
- Compilation : OK ; rendu non vérifié dans le navigateur.

## 87. Zones : confirmation avant suppression (21 octobre 2026) : codé

- « Supprimer la zone » (page Zones) ouvre d'abord une fenêtre de confirmation. Elle liste les écrans de la zone, qui passeront dans la zone par défaut de l'aire (comportement inchangé de `AreaOps.DeleteZone`), ou indique qu'aucun écran n'y est.
- Compilation : OK ; rendu non vérifié dans le navigateur.

## 88. Canva : dossiers synchronisés vers la médiathèque (21 octobre 2026) : codé, non testé avec un vrai compte Canva

- **Intégrations › Canva › « Gérer les dossiers… »** (`CanvaFoldersDialog`) : liste des dossiers Canva choisis, sélecteur par navigation (`GET /folders/{id}/items`, racine « root »), case « Inclure les sous-dossiers ». Même principe que les Drives : le dossier est synchronisé à l'ajout, puis à la demande (menu du dossier ou « Synchroniser maintenant ») ; pas de synchronisation périodique.
- **Médiathèque** : chaque design est exporté en images (`CanvaService.Export`) et rangé dans le dossier « Canva_médias » de l'aire (`Tenant.CanvaMediaFolderId`, créé à la première synchronisation, renommable et déplaçable comme tout dossier). Les fichiers portent `Info["canvaFolder"]` (dossier Linkii), `canvaDesign` et `canvaUpdated` (date de modification Canva) ; ils n'ont pas `Info["canva"]` : le nettoyage des exports orphelins ne les touche pas. La pastille d'origine reste « Canva ».
- **Mises à jour** : à la synchronisation, seuls les designs nouveaux ou modifiés sont exportés de nouveau (l'ancienne version est remplacée, dans le même dossier de la médiathèque) ; un design sorti du dossier Canva quitte la médiathèque. Limite : `CanvaService.MaxDesigns` = 60 designs par dossier (les autres sont comptés et signalés). Une synchronisation tourne en arrière-plan, avec son avancement dans la liste.
- **Retrait** : retirer un dossier, ou retirer l'intégration Canva (règle du §86), supprime les fichiers synchronisés de la médiathèque. Déconnecter le compte Canva ne les supprime pas.
- **Droits Canva** : ajout de la permission `folder:read` à la connexion. Un compte connecté avant doit être reconnecté (message explicite si Canva répond 403) ; la permission doit aussi être cochée dans la configuration de l'app chez Canva.
- **Limites connues** : les images téléversées dans Canva ne sont pas copiées (seulement les designs) ; un fichier supprimé à la main dans « Canva_médias » revient à la synchronisation suivante.
- Compilation : OK ; rendu et échanges avec Canva non vérifiés.

## 89. Canva : assistant d'ajout en tiroir du haut, retrait complet, compte dans la carte (21 octobre 2026) : codé, non vérifié dans le navigateur

Maquette validée : `maquettes/canva-assistant-ajout.html`. Première intégration avec compte à passer au nouveau flux ; les autres (Google, Microsoft, Dropbox, Facebook, Nextcloud, SFTP) suivront après validation du rendu.

- **Assistant** (`CanvaWizard`, remplace le tiroir de droite et la fenêtre « Dossiers Canva ») : tiroir qui descend du haut, 3 étapes avec Précédent / Suivant. 1. Connexion (carte du compte : avatar, nom, état, « Se connecter avec Canva » ou « Changer de compte » ; Suivant grisé tant que le compte n'est pas connecté). 2. Dossiers : arbre des dossiers Canva (cases à cocher, nombre de designs, chargement à l'ouverture) et aperçu des designs du dossier sélectionné (vignettes, pages) avec ses sous-dossiers ; case « Inclure les sous-dossiers ». 3. Validation (dossiers, designs, images, dossiers retirés, destination « Canva_médias ») puis import avec barre de progression et vignettes des images qui arrivent ; l'import continue si l'on ferme. Retour de Canva après la connexion : l'assistant se rouvre à l'étape 2.
- **Carte Canva de Mes intégrations** : menu ⋮ = Gérer les dossiers (étape 2), Reconnecter ou changer de compte (étape 1), Retirer. Le compte Canva n'apparaît plus dans l'onglet Comptes connectés (ni dans « Connecter un compte ») ; il se gère dans sa carte. L'onglet disparaîtra quand les autres intégrations auront suivi.
- **Retrait** : une confirmation liste ce qui part (images de la médiathèque, listes de lecture touchées, compte déconnecté), puis `RetireCore("canva")` supprime les dossiers synchronisés et leurs fichiers, désinstalle l'app (contenus Canva et exports orphelins compris, listes passées en brouillon), déconnecte le compte et remet l'intégration dans le Store : elle quitte Mes intégrations. Le retrait groupé fait la même chose sans confirmation propre.
- Service : `CanvaService.Impact` (ce que le retrait emporte), `CollectDesigns` public (aperçu de l'étape 3), `ListFolder` renvoie aussi les designs du dossier.
