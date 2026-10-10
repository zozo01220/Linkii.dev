# Linkii — Dev POC (jetable)

> **Statut : test / prototype voué à mourir.** But : prouver la chaîne *back-office → serveur → SignalR → player* et la promesse produit **« écran en service en moins de 2 minutes »**. Le code n'est pas destiné à être réutilisé : on privilégie la vitesse.
> Source : *Linkii — Spécification produit (v1)*. Dernière mise à jour : 10 octobre 2026.

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
| **Intégrations** (`/integrations`, menu latéral en bas) et **Widgets** (`/widgets`) | Catalogue en cartes ou en liste (`CatalogView`, affichage mémorisé par page via `ViewToggle`). Intégrations regroupées par `category` du manifeste (Médiathèque · Service en ligne · Web), onglet **Comptes connectés** (voir §16) : interrupteur de disponibilité, nombre de contenus, Canva › **Gérer** (compte et intégration de l’organisation). Widgets : interrupteur (proposé ou non dans l’édition des écrans), nombre d’écrans qui l’utilisent. « Playlist » s’affiche « Liste de lecture » dans l’interface (le code garde `Playlist`). **Calendriers partagés** : famille de sources (Microsoft 365, Google Calendar, CalDAV, liens ICS, Exchange) dans Intégrations, une connexion par source (`Tenant.CalendarAccounts`, secrets chiffrés ; Microsoft 365 garde `Ms*`) et ses calendriers (`Tenant.SharedCalendars`, fenêtre `CalendarSourceDialog`). L’app `agenda` (« Calendrier partagé ») n’a plus qu’un champ `calendar` (Id du calendrier), résolu côté serveur à chaque lecture (`SharedCalendarSources.Resolve`) ; elle est proposée dès qu’une source est active. Les anciens contenus Agenda (connexion dans le contenu) sont supprimés au démarrage (`Seed.PurgeOldCalendars`, sauvegarde `data.json.pre-calendriers.bak`). **Drives** : Google Drive et OneDrive / SharePoint (`Apps/Drives.cs`, fenêtre `DriveSourceDialog`), Google Drive : compte Google connecté par l’organisation (voir Compte Google) ; OneDrive : connexion Microsoft 365 partagée avec les calendriers (autorisation `Files.Read.All` ou `Sites.Selected`). Dossiers choisis en parcourant le compte connecté ou par lien de partage (`Tenant.DriveFolders`, sous-dossiers inclus sur option : voir §17), images et vidéos copiées dans la médiathèque (`Info["drive"]`, affichées avec un badge Drive, ni renommées ni supprimées depuis la Médiathèque), synchronisées toutes les 15 min (`DriveSyncWorker`) ou à la demande. L’app `drive` (« Dossier Drive ») résout ses fichiers à la publication ; après chaque synchronisation, `Notifier.RefreshDrive` met à jour les éléments publiés et prévient les écrans, sans republier. |

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
| Planning (vue semaine, créneaux) | Pas nécessaire pour valider la chaîne |
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
| `POST` | `/api/player/ack` (`X-Token`) | Confirme la version reçue + résolution détectée |
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
- [x] Application Android (WebView, kiosque, hors ligne) : `Linkii.Android/`, §19. Reste : essai sur un vrai boîtier (fluidité des vidéos), publication Google Play, PIN piloté depuis le back-office.
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


## 14. Aires de gestion et membres (8 octobre 2026)

Une organisation range ses écrans, listes de lecture et médias dans des **aires de gestion** (un site, un bâtiment, un service). Les membres d'une aire partagent tout son contenu ; les autres aires leur sont invisibles. Les **zones** d'une aire classent ses écrans (filtres de la page Écrans), sans effet sur les droits.

| Rôle | Contenus de l'aire (écrans, listes, médias, publier) | Zones et membres de l'aire | Aires, déplacements entre aires, réglages, intégrations, widgets |
|---|---|---|---|
| Utilisateur d'aire | oui | non | non |
| Administrateur d'aire | oui | oui | non |
| Administrateur de l'organisation (`ClientAdmin`, plusieurs possibles) | oui, toutes les aires | oui | oui |

- **Modèle** : `Area` (`Db.Areas`, `Zones`) ; `Screen`, `Playlist`, `MediaItem` portent `AreaId` (`IAreaOwned`), `Screen.ZoneId` ; `User.AreaRoles` (rôle par aire, membres), `User.LastAreas` (aire ouverte, par organisation), `LastLoginUtc` / `LastActiveUtc` (actifs sur 30 jours).
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

Maquette validée : `maquettes/player-android.html`. Projet `Linkii.Android/` (Kotlin, **aucune dépendance**, pas d'AndroidX) : une coquille native autour du player web. Le player garde l'appairage par code, la diffusion et le cache hors ligne (service worker + Cache Storage, §7) ; l'application ajoute ce que le navigateur ne peut pas faire.

- **Lancement** : démarrage au boot et après une mise à jour (`BootReceiver`, nécessite l'autorisation « par-dessus les autres applications » sur Android 10+ ; menu technicien › *Autoriser le démarrage automatique*), ou **écran d'accueil de l'appareil** (filtre `HOME`, recommandé sur un boîtier). Plein écran immersif permanent, écran jamais en veille, bouton Retour sans effet. Verrouillage kiosque (`startLockTask`) seulement si l'appareil est géré (MDM) : sinon Android demanderait une confirmation à chaque lancement.
- **Appairage** : celui du player (code à 6 chiffres affiché, saisi dans *Écrans › Connecter un écran*) ; rien à développer côté back-office. L'agent utilisateur du WebView se termine par `LinkiiPlayer/<version> Android`.
- **Pannes** : serveur injoignable au lancement (ni réseau ni cache) → page de secours (`assets/offline.html`) puis nouvel essai toutes les 10 s ; processus de rendu tué par Android → la vue est recréée et le player rechargé.
- **Menu technicien** : 5 appuis sur OK (télécommande) ou 5 touchers dans le coin supérieur droit, en moins de 3 s. Code PIN (**9999 par défaut**, local à l'appareil) puis diagnostic (serveur, version, réseau, stockage libre) et actions : reprendre, recharger le player, changer de serveur, définir le PIN, autoriser le démarrage, désappairer (efface `lk_token`, `lk_screen`, `lk_playlist`, `lk_acked`, `lk_brand`, comme `unpair()` du player), quitter.
- **Serveur** : `https://linkii.duckdns.org` par défaut (`-PserverUrl=…` à la compilation, modifiable dans le menu). **HTTPS obligatoire** (sans lui, pas de service worker donc pas de hors ligne) ; seuls `localhost` et `10.0.2.2` sont admis en HTTP (`network_security.xml`).
- **Compiler** : `Linkii.Android/gradlew :app:assembleDebug` (APK) ou `:app:bundleRelease` (`.aab` pour Google Play, signature par `LINKII_KEYSTORE*` dans `~/.gradle/gradle.properties`). AGP 9.4.1, Gradle 9.7.1, JDK 17+ (celui d'Android Studio convient), SDK 35 ; minSdk 24. Détails et publication Play Store : `Linkii.Android/README.md`.
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
- **Alerte hors ligne** (`AlertOffline`, `AlertOfflineMinutes`, `AlertEmail`, `Mailer.cs`) : un e-mail par panne quand un écran est hors ligne depuis N minutes (destinataire : l'adresse réglée, sinon les administrateurs). Réglages › Supervision.
- **Appareil remonté par le player** : `DeviceKind` (androidtv / android / web), système, modèle, version de l'application, dernier démarrage.
- **Miroir** (écran et mur : `MirrorH`, `MirrorV`) : pour un écran vu dans un miroir ; combiné par OU exclusif écran/mur ; ne demande aucune publication mais recharge les écrans (`SyncStamp`). Maquette : `maquettes/direct-ecrans.html`.
- **Médiathèque** : miniatures WEBP 320 px générées dans `Linkii.Poc/thumbs/` (données d'exécution, ignorées par git).
- Tests : `Linkii.Poc.Tests/ControlTests.cs`. Non vérifié sur un parc réel d'appareils.

## 24. Reconnexion automatique du back-office (10 octobre 2026)

Après un arrêt/redémarrage du serveur, le circuit Blazor est perdu et la page restait bloquée sur « Could not reconnect to the server ». `wwwroot/reconnect.js` (Blazor démarré avec `autostart="false"` dans `App.razor`) remplace l'écran par défaut : bandeau « Connexion perdue, reconnexion en cours… », sondage du serveur toutes les 2 s (sans limite), **rechargement automatique** dès qu'il répond, ou au retour du réseau / de l'onglet. Non vérifié par un vrai redémarrage.

## Plans et prochaines étapes

- Android : tester l'app 1.1.0 (aperçu du direct, redémarrage, version remontée) sur un vrai boîtier ; PIN du menu technicien à définir depuis le back-office ; icône définitive.
- Valider un vrai Drive pour le bandeau d'import (§ 21) et la conversion 1080p sur serveur Linux.
- Supervision : statistiques de diffusion annoncées avec Growth, non codées.
- Reconnexion : tester l'arrêt/redémarrage du site en conditions réelles.
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
