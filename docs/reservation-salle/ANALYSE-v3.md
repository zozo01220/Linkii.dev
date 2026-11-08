# Réservation de salle — spécification V1 complète (de l'installation à l'utilisation sur la tablette)

> Rédigée le 8 novembre 2026 à partir de `ANALYSE.md` (v1), `ANALYSE-v2.md`, de la maquette et d'une **vérification du code** (branche `fenetres-apercu-config`, commit `df8f952`). **Aucun code écrit.**
> **Ce document remplace la v1 et la v2.** La session qui code lit ce fichier et la maquette `maquettes/salle-app-parcours.html` (6 vues ; la vue 6 « Sur la tablette » est nouvelle). Elle n'a pas besoin de la v1 ni de la v2.
> Les numéros de ligne cités (`fichier l.N`) sont ceux du 8 nov. : les relire avant de modifier.

---

## 0. Règles du projet (venues de l'utilisateur ; non écrites ailleurs)

Règles de la refonte Applications du 7 nov. (§206 de `DEV-POC.md`), à appliquer ici comme partout :
1. Toute application installée apparaît dans le groupe **« Applications »** du menu de gauche ; l'entrée ouvre `/apps/{id}`.
2. Flux : installation → import (aucun dossier créé) → onglet **Contenus** de l'application → menu.
3. **Contenus** : liste, jamais de cartes, masqué si vide, **aucun bouton « Nouveau contenu »**, **interrupteur Actif** par ligne (désactivé = disparaît partout).
4. Configuration : bouton **« Configurer »** ; aucune fenêtre de configuration ailleurs.
5. Application à compte : **Synchroniser** en haut, à côté de **Désinstaller**.
6. Les comptes se gèrent dans Réglages › Connexions › Comptes.
7. Pas de phrases d'introduction inutiles. L'utilisateur est **très sensible aux allers-retours et à la consommation** : ne rien tester sans nécessité, ne pas deviner, poser une question courte seulement si c'est vraiment ambigu.

Autres règles : **l'interface suit la maquette** (s'en écarter = le dire avant, en une phrase) ; **interrupteurs, jamais de cases à cocher** ; **vocabulaire générique** (pas de termes sectoriels) ; fonctions avancées en **mode expert**, **mode simple** réduit ; formulaire modal = fenêtre **centrée** ; assistants avec points d'étapes (modèle : `WizardShell`) ; licences **Free, Essential, Pro, Enterprise** (dans le code, Pro = `Plans.Growth` / `IsGrowth`).

Travail :
- Dépôt `C:\DEV\En-cours\Linkii` (.NET / Blazor, serveur `Linkii.Poc`, tests `Linkii.Poc.Tests`, player Android `Linkii.AndroidPlayer` = coquille WebView du player web `wwwroot/player`).
- Au 8 nov. l'arbre de travail est **propre** (tout est commité). Lire quand même `git status` avant de commencer : si d'autres chantiers ont laissé des modifications, ne pas les écraser, ne stager que les fichiers de ce chantier. Ne **rien commiter ni pousser** sans demande.
- Journal `DEV-POC.md` : dernière section **§213** → ce chantier écrit **§214 et suivantes**, une par lot, en disant honnêtement ce qui n'a pas été essayé (vrais comptes Microsoft/Google, vraie tablette). Ne jamais écrire « vérifié » pour ce qui ne l'est pas.

---

## 1. Décisions

### 1.1 De l'utilisateur (8 nov.)
1. Le parcours démarre à l'installation de l'application **« Réservation de salle »** depuis le **Store**.
2. Pendant l'installation, on **importe ses agendas** depuis les applications d'agenda (Google, Microsoft, ICS…).
3. Un type d'écran **« Écran de salle »** (tablette) apparaît dans **Écrans** ; **impossible d'en créer sans l'application**.
4. **Tous les plans** (pas seulement Pro).
5. Flux en 3 temps : ① installer l'application ; ② installer le **player** sur la tablette ; ③ **appairer et paramétrer avec l'assistant « Nouvel écran de salle »**.
6. Dans cet assistant, **pas d'étape « Sur quel appareil ? »** ; la **première étape est le code d'appairage** : Code → Salle → Affichage → Terminé.
7. **Linkivi ne modifie jamais un rendez-vous** (ils vivent chez Microsoft, Google…). L'onglet **Réservations** de l'application n'affiche que le **statut des salles** : libre ou occupée, de quelle heure à quelle heure. Ni annulation, ni modification, ni détail, ni « Réserver pour quelqu'un ».
8. **La V1 couvre tout le parcours, jusqu'à l'utilisation sur la tablette**, réservation depuis l'écran comprise.

(Les points 5 à 7 sont datés « 9 nov. » dans la v2 ; ils ont été faits le 8 nov.)

### 1.2 Retenues par défaut (recommandations de l'analyse ; l'utilisateur peut les changer)
| N° | Décision | Pourquoi |
|---|---|---|
| D1 | Page de l'application = **Contenus + onglet Réservations + Configuration + Infos**, pas de menu de gestion séparé. | Règle 1 ; un seul onglet propre à l'application. |
| D2 | Sans l'application, « Écran de salle » est **visible mais grisé** dans Écrans › Ajouter, avec « Ouvrir le Store » (mode expert ; masqué en mode simple, voir D10). | L'utilisateur découvre la fonction. |
| D3 | Un écran de salle **est un écran** : facturé comme les autres (prorata, `WithScreenCharge`). Il n'existe aucune limite d'écrans par plan (vérifié). | Pas de cas particulier dans la facturation. |
| D4 | **Un écran = une salle.** Un tableau de plusieurs salles est un autre type, plus tard. | Simplicité. |
| D5 | Une fois l'application livrée, l'interrupteur « Salle » des calendriers (`CalendarSourceDialog`) est remplacé par la mention **« Gérée par Réservation de salle »** (lien vers l'application) ; sans l'application, il est remplacé par « Installer Réservation de salle ». | L'application est l'unique point d'entrée. |
| D6 | Sur la tablette on réserve **maintenant** seulement (début à la minute courante). | Le besoin d'origine ; pas de clavier ni d'agenda sur la porte. |
| D7 | **Création seulement** : ni « + 15 min » ni « Terminer » sur la tablette. La création d'un rendez-vous depuis la tablette est la **seule écriture** de Linkivi. Linkivi ne supprime que ses **propres** événements techniques (test de réservation, invitation refusée par la salle). | Décision 1.1-7. |
| D8 | Aucun nom demandé sur la tablette : le rendez-vous créé s'appelle **« Réservé sur place »** (modifiable en mode expert). | Pas de clavier sur la porte, pas de donnée personnelle. |
| D9 | Écran non tactile : pas de boutons de durée (la page QR est hors V1). | — |
| D10 | **Mode simple** : l'application ne s'installe qu'en mode expert (la liste `SimpleMode.Apps` reste à 6 applications, règle du §174). Une fois installée, sa page, ses écrans et l'entrée « Écran de salle » restent visibles en mode simple, avec une Configuration réduite. | Ne pas casser la règle du mode simple. |
| D11 | **Migration** : une organisation qui a déjà des salles (`SharedCalendar.Room = true`) reçoit l'application installée d'office, toutes ses sources en « Afficher seulement ». Aucun écran n'est modifié. | Une seule porte d'entrée, sans rien perdre. |
| D12 | Le bouton du Store est **« Utiliser »**, comme toutes les autres fiches (la maquette disait « Installer » : corrigée). Le titre de l'assistant reste « Installer · … ». | Cohérence avec le Store (§76). |
| D13 | Salle venant d'un **lien ICS** : réservations **locales** Linkivi (visibles sur les écrans Linkivi, pas dans Outlook ni Google), annoncées clairement. | Un lien ICS ne s'écrit pas. |
| D14 | **CalDAV et Exchange sur site** : affichage seul en V1 (« Afficher et réserver » grisé « Bientôt »). | Pas d'essai réel possible. |
| D15 | **Pas de règles propres à une salle** en V1 : les règles de l'application valent pour toutes les salles. | Moins d'interface ; ajoutable ensuite. |
| D16 | Partage en lecture seule de l'onglet Réservations (accueil) : **seulement si le partage par page (§117) le permet sans nouveau mécanisme** ; sinon après la V1, et le noter. La page `/apps/{id}` est réservée aux administrateurs (`AppDetail.razor` l.2). | Ne pas inventer un nouveau système de droits. |

---

## 2. Périmètre de la V1

**Inclus** : fiche du Store, assistant d'installation (6 étapes, droits d'écriture Microsoft/Google compris), page de l'application (Contenus, Réservations en statut seul, Configuration avec test de réservation, Infos), type d'écran « Écran de salle » (menu Ajouter, assistant 4 étapes, carte, filtre, fenêtre de modification, simulateur), porte sur la tablette (affichage et **réservation de 15 min à 2 h**), écriture dans Microsoft 365 et Google, réservations locales pour ICS, désinstallation et cas limites, alertes, mode simple, migration.

**Hors V1** : voir §15 (page QR, prolonger / terminer, libération automatique, CalDAV/Exchange en écriture, règles par salle, vues Semaine / Liste, statistiques, enrichissements).

---

## 3. Existant vérifié dans le code (8 nov.)

| Sujet | Où | Constat |
|---|---|---|
| Une salle = un calendrier | `Models.cs` l.777-794, `Apps/SharedCalendars.cs` l.82-95 | `SharedCalendar` : `Room`, `RoomName`, `RoomCap`, `RoomPlace`, `RoomAllDay`, `RoomSoon` (10), `RoomTitles` (false), `Off`. `RoomContents` = `c.Room && !c.Off`. **Actif = !Off** (confirmé : `AppDetail.razor` l.289 et `Svc.SetContentOn("calendar", …)`). |
| Interrupteur « Salle » | `Components/CalendarSourceDialog.razor` l.137-157, l.288 | Seul endroit où on le règle. L'assistant d'ajout `CalendarWizard.razor` (§209) ne l'a plus ; il liste l'annuaire des salles Microsoft (l.243, l.261). |
| Lecture des agendas | `Apps/CalendarConnectors.cs` l.35-40 ; `DataServices.cs` l.18-40 | `ICalendarConnector` n'a que `GetEvents`. `EventDto` ne porte **pas** l'identifiant de l'événement. `CalendarService` garde 20 s en cache par `CacheKey`. |
| Microsoft | `Apps/MicrosoftAuth.cs` l.22-45 ; `DataServices.cs` l.92-181 | Accès `Calendars.Read` + `Calendars.Read.Shared`. `Place.Read.All` n'est plus demandé : `GraphService.ListRooms` échoue souvent (repli : saisie de l'adresse). `GraphService.Get` ne fait que des GET. |
| Google | `Apps/GoogleAuth.cs` l.21-29, l.68 ; `CalendarConnectors.cs` l.100-114 | Accès `calendar.readonly` ; `include_granted_scopes=true` (les accès déjà accordés restent). `ListCalendars` ne lit pas `accessRole` (à ajouter). |
| Retour d'une connexion OAuth | `Apps/ConnectedAccounts.cs` l.10-17 ; `Integrations.razor` l.1485 | `?wiz=<id>` rouvre l'assistant à l'étape 2. **L'état d'un assistant Blazor est perdu** pendant l'aller-retour chez Microsoft ou Google. |
| Application avec son assistant | `Components/HaWizard.razor`, `Apps/HomeAssistantSetup.cs`, `Integrations.razor` l.277-279, l.1365, l.1392 | Modèle à copier : assistant propre, `Install` / `Uninstall`, ouverture depuis le Store (`HasWizard` → `OpenWizard`). Manifeste d'une « fonction » : `Apps/emergency.json` (`"kind": "feature"`). |
| Page `/apps/{id}` | `Components/Pages/AppDetail.razor` | Onglets l.53-57 ; cas particulier déjà présent : « Vue du bâtiment » (`IsPlans` : colonnes et lignes propres l.129, l.378-389) = modèle pour le cas « salles ». |
| Menu | `Apps/AppMenu.cs` l.48-53 | Une application du catalogue est installée si elle a une ligne `AppInstalls`. |
| Écrans | `Components/Pages/Screens.razor` | Menu Ajouter l.37-46 (borne : `OpenPairKiosk` l.2840) ; filtre Type `kindFilter` l.1002, l.1014, l.2685 (dans le menu Filtres depuis le §181, plus d'onglets) ; appairage `Pair` / `PairNow` l.2954-3007 (`ClaimScreen`, `WithScreenCharge` l.2923) ; badge Borne l.2051. |
| Appairage | `Program.cs` l.709-731, l.772-792 | Le player crée le code (`/api/pairing/start`, n'envoie que largeur et hauteur). `/api/player/device` remonte type, système, modèle, batterie : **pas le tactile**. |
| Ce que reçoit le player | `Preview.cs` l.41-94 (`PlayerFeed.ForScreen`) | La borne arrive par `["kiosk"]` : modèle pour `["room"]`. |
| Données de salle aujourd'hui | `Program.cs` l.949 ; `Apps/Providers.cs` l.118-131 | Élément `room` d'une composition publiée → `/api/data/{composition}/{salle}` → `CalendarProvider.Fetch` (titres vidés si `RoomTitles` = false). Relu toutes les 60 s (`composition.js` l.86). |
| État d'une salle | `wwwroot/player/composition.js` l.807-835, exporté l.981 | `roomState` : libre / bientôt occupé / occupé / non choisie ; rendez-vous qui se chevauchent fusionnés. |
| Tactile | `Linkii.AndroidPlayer/.../MainActivity.kt` l.374-384 ; `player.js` l.862-865 ; `Program.cs` l.1013-1048 | Les touchers vont à la WebView (5 touchers dans le **coin haut-droit de 140 dp** ouvrent le menu technicien). Les applications du player reçoivent les clics et envoient des commandes (`h.command` → `/api/player/app/{id}/{action}`, précédent `HaCommand`). **Les compositions ne gèrent aucun clic.** |
| Temps réel | `Apps/HomeAssistantLive.cs` l.394 ; `Notifier` | Message de hub « DataChanged » aux écrans ; `Notifier.NotifyScreen(id)`. |
| Limite d'écrans | — | Aucune (pas de `MaxScreens`) ; chaque écran ajouté est facturé au prorata (`Billing.ScreenProrata`). |
| Mode simple | `ModeService.cs` l.63-70 | `SimpleMode.Apps` : 6 applications seulement. |
| Alertes | `Alerts.cs` l.10-30 | Catégorie `Integrations` (« Applications et API ») : toujours dans le centre de notifications, tous plans. |

**Conséquences pour la conception** (les « À vérifier » de la v2 sont résolus) :
- La porte **ne peut pas être une composition** (pas de clic) : c'est un **module du player** `room-door.js`, monté en plein écran quand le flux contient `room` (comme la borne avec `kiosk`). L'élément `room` des compositions reste pour les compositions libres.
- L'écran de salle reçoit son contenu **sans publication** : `PlayerFeed.ForScreen` ajoute `["room"]` construit à chaque lecture.
- Le tactile se détecte en ajoutant `touch` (`navigator.maxTouchPoints > 0`) à `/api/player/device`. Le player web est servi par le serveur, donc **pas de nouvel APK** à publier.
- Les droits d'écriture se demandent par un nouveau motif de connexion `rooms`, sur le même compte Microsoft ou Google.

---

## 4. Parcours 1 — Store et installation (maquette vues 1-2)

**Fiche du Store** : manifeste `Apps/room-booking.json` (`id` `room-booking`, `kind` `feature`, aucun `plan`, catégorie « Agendas », icône porte). Même carte que les autres, bouton **« Utiliser »**. Texte de la fiche : à quoi elle sert ; « Nécessite un agenda : Google, Microsoft 365, lien ICS… » ; « Ajoute un type d'écran : Écran de salle » ; « Tous les plans ». « Utiliser » ouvre l'assistant (`HasWizard("room-booking")`). En mode simple, la fiche n'est pas proposée (D10).

**Assistant d'installation** (`Components/RoomAppWizard.razor`, fenêtre centrée, `WizardShell`, 6 points, Retour / Continuer). **Rien n'est enregistré avant la dernière étape**, sauf ce qui sert aussi ailleurs et reste en cas d'abandon (comme dans l'assistant d'ajout) : le compte connecté et l'application d'agenda installée à l'étape 2.

**Aller-retour OAuth** : avant de partir chez Microsoft ou Google (étapes 2 et 4), l'assistant enregistre son état dans un **brouillon en mémoire** (`RoomSetupDrafts`, clé organisation + personne, 30 min, comme les `pending` des classes d'authentification). La connexion part avec `wiz=room-booking` ; au retour, l'assistant se rouvre **à l'étape enregistrée** avec ses choix.

| # | Étape | Contenu | Règle |
|---|---|---|---|
| 1 | Bienvenue | 3 repères : ① vos agendas ② vos règles ③ vos écrans. | « Il vous faut au moins un agenda. » |
| 2 | Agendas | Une ligne par source (Microsoft 365, Google Agenda, Liens ICS, CalDAV, Exchange sur site) : état (« Connecté · compte » / « Prête » pour ICS / « Application non installée ») et interrupteur « utiliser ». Non installée : **« Installer et connecter »** : Microsoft / Google → `/microsoft/connect?for=calendar&wiz=room-booking` (ou Google) ; ICS → la source s'active ; CalDAV / Exchange → formulaire de connexion de `CalendarWizard` (étape 1), puis retour. | Continuer impossible sans au moins un agenda connecté et choisi. |
| 3 | Salles | Par source choisie, les agendas trouvés : interrupteur « Salle », nom, places, lieu modifiables. Microsoft : `ListCalendars` + `ListRooms` si l'annuaire répond. Google : `ListCalendars` avec `accessRole`. ICS / CalDAV / Exchange : calendriers déjà connus + **« Ajouter par adresse »** (champs existants). Badge **« Salle détectée »** : Microsoft = dans l'annuaire ; Google = identifiant en `…@resource.calendar.google.com`. Badges « Lecture seule » (ICS) et « Affichage seul » (agenda Google en lecture). Cochés d'office : salles détectées et calendriers déjà `Room = true`. Compteur « N salles sélectionnées ». | Au moins une salle. |
| 4 | Réservation | Par source utilisée : Microsoft / Google → **« Afficher seulement » / « Afficher et réserver »** (défaut : réserver) ; « réserver » sans droit → bouton **Autoriser** (`/microsoft/connect?for=rooms&wiz=room-booking`, idem Google) ; ✓ si le compte a déjà le droit. ICS → « Réservations Linkivi » verrouillé, avec l'explication. CalDAV / Exchange → « Afficher seulement », « Afficher et réserver » grisé « Bientôt ». | Continuer bloqué tant qu'une source « réserver » n'est pas autorisée (ou repassée en « Afficher seulement »). Refus du consentement → message, repli proposé sur « Afficher seulement ». |
| 5 | Règles | Préréglages **Standard** (15, 30, 45 min, 1 h, 2 h ; max 2 h) · **Réunions courtes** (15, 30, 45 min, 1 h ; max 1 h) · **Salles à la journée** (1 h, 2 h, 3 h ; max journée). Puis : durées proposées (puces 15/30/45 min, 1/2/3 h, au moins une), durée maximale, horaires réservables (jours + de / à ; défaut lun-ven 07:00-19:00), **« Afficher le titre des réunions »** (désactivé). Si aucune source ne réserve : seulement « Afficher le titre des réunions ». | Valables pour toutes les salles (D15). |
| 6 | Terminé | « N salles importées depuis M agendas. » Deux cartes : **« Créer un écran de salle »** (ouvre l'assistant d'écran, §6) et « Ouvrir l'application ». | L'installation se fait au clic sur **« Installer »** (bouton de l'étape 5). |

**Ce que fait l'installation** (`RoomSetup.Install(draft)`) : ligne `AppInstalls` (`AppService.Install("room-booking")`) ; entrée de menu (`AppMenu.Set`, avec l'animation des nouveautés) ; `Tenant.RoomApp` (règles et choix par source, §9) ; pour chaque agenda coché : trouve ou crée la ligne `SharedCalendar` (source, nom, `Ref`, `Hint`), puis `Room = true`, nom, places, lieu, `RoomTitles` ; un agenda décoché qui était une salle repasse `Room = false` ; calcul du **mode d'écriture** de chaque salle (§8.3).

**Abandon** : fermer l'assistant demande « Abandonner l'installation ? » ; le brouillon est effacé.

---

## 5. Parcours 2 — la page de l'application (maquette vue 3)

En-tête : icône, nom, « Agendas · version 1.0 · installée le … » ; à droite **Synchroniser** (relit les agendas des sources : noms, calendriers disparus, nouveaux droits ; recalcule les modes d'écriture et les états) et **Désinstaller**.

Onglets : **Contenus** · **Réservations** · **Configuration** · **Infos**. Mise en œuvre : une branche « salles » dans `AppDetail.razor`, comme `IsPlans` (colonnes et lignes propres), avec des composants à part pour ne pas alourdir la page commune à toutes les applications.

- **Contenus** = les salles, en liste (colonnes de la maquette) : pastille de la source ; nom + « 12 places · 2e étage » ; **état en direct** (● Libre jusqu'à 16:30 · ● Occupé jusqu'à 15:00 · ● Bientôt occupé · 14:15 · ● Libre toute la journée) + « Ensuite : 15:30 » (le titre seulement si les titres sont affichés) ; **écran associé** (pastille « ● Porte A101 » verte si en ligne, rouge sinon ; « +1 » s'il y en a plusieurs ; sinon bouton **« Créer l'écran »**, qui ouvre l'assistant d'écran avec la salle choisie) ; **interrupteur Actif**. Pas de « Nouveau contenu ». État rafraîchi toutes les 60 s tant que la page est ouverte. Actif = off : la salle disparaît partout, ses écrans affichent « Salle indisponible ».
- **Réservations** (`Components/RoomStatusTab.razor`, **lecture seule, statut seul**) : bouton « Aujourd'hui », filtre « Toutes les salles ▾ » ; frise (lignes = salles actives, colonnes = heures, de 8 h à 18 h comme la maquette, élargie si les horaires réservables sont plus larges), barres « Occupée » avec leurs heures, trait rouge « maintenant » ; section **« Maintenant »** : « Occupée de 13:30 à 15:00 », « Libre de 14:10 à 16:30 », « Libre de 14:10 à 14:15 · bientôt occupée », « Libre toute la journée ». **Aucun clic d'action, aucun titre, aucun nom** (le serveur n'envoie que des intervalles). Note permanente : « Lecture seule : les rendez-vous vivent dans vos agendas (Microsoft, Google…) et se modifient là-bas. » Rafraîchi toutes les 60 s. Partage : D16.
- **Configuration** :
  - **Agendas** : une ligne par source utilisée (logo, compte, mode « Afficher et réserver » / « Afficher seulement » / « Réservations Linkivi »), bouton **« Gérer les salles »** (rouvre `RoomAppWizard` en mode gestion, sur les étapes Salles et Réservation de cette source ; enregistre à la fin) ; **« + Ajouter un agenda »** (même assistant, à l'étape Agendas). Si le droit d'écrire a été perdu : ligne en alerte + bouton **« Autoriser à nouveau »**.
  - **Règles par défaut** : résumé « 15 · 30 · 45 min · 1 h · 2 h — max 2 h — lun-ven 07:00-19:00 » + bouton **Configurer** (`RoomRulesDialog.razor`, fenêtre centrée, contenu de l'étape 5 ; en plus, en mode expert : tampon entre deux réunions 0 / 5 / 10 / 15 min, titre du rendez-vous créé).
  - **Santé** : « Test de réservation » + **Tester** (§8.5) ; ligne d'état « ✔ 4 salles réservables · ⚠ « Salle de conférence » : lecture seule (ICS) ».
  - « Dans le menu » (interrupteur commun à toutes les applications).
- **Infos** : version, « Tous les plans », agendas utilisés, « Ajoute le type d'écran « Écran de salle » », nombre de salles et d'écrans.

**Mode simple** : Contenus, Réservations, Configuration sans le tampon ni le titre du rendez-vous.

---

## 6. Parcours 3 — le type d'écran « Écran de salle » (maquette vue 4)

### 6.1 Menu Ajouter (`Screens.razor` l.37-46)
Troisième entrée **« Écran de salle »** (icône porte, badge « Nouveau », « Tablette à la porte d'une salle (player déjà installé dessus). L'assistant demande d'abord son code. »). Sans l'application : entrée **grisée**, badge « Application requise », « Installez « Réservation de salle » pour créer des écrans de salle » + bouton orange **« Ouvrir le Store »** ; masquée en mode simple (D10). Mêmes droits que « Écran de diffusion » (`canAdd`).

### 6.2 Assistant « Nouvel écran de salle » (`Components/RoomScreenWizard.razor`, `WizardShell`, 4 points)
1. **Code** : champ large « 000 000 », bouton **« Connecter la tablette »**. Contrôle du code (`Token == null && PairingCode == code`), puis `WithScreenCharge` (fenêtre de paiement du prorata si besoin, comme aujourd'hui), puis appairage (`ClaimScreen`) : l'écran est créé **écran de salle sans salle** (`Screen.Room` renseigné, `RoomCalendarId` vide, nom provisoire « Écran de salle 123456 », zone par défaut, `MemberOps.AdoptScreen`). La tablette affiche aussitôt « Appairée · en attente de configuration ». L'assistant attend jusqu'à 10 s le rapport `/api/player/device` puis affiche **« Tablette détectée : Android · paysage · tactile »** (type, orientation d'après la taille détectée, tactile) ; sans rapport : « Tablette connectée ». Continuer reste désactivé tant que la tablette n'est pas appairée. Rappel sous le champ : « Pas encore de player sur la tablette ? Installer le player » (ouvre « Installer le player », Android).
2. **Salle** : cartes des salles actives (badge « Sans écran » / « 1 écran »). Une salle peut avoir plusieurs écrans. Si l'assistant vient de « Créer l'écran » (Contenus), la salle est déjà choisie.
3. **Affichage** : à gauche, **aperçu de la porte** (le vrai module `room-door.js` en mode aperçu, avec l'état réel de la salle) ; thème **Clair / Sombre** ; **« Boutons de durée »** (visible seulement si la salle peut réserver ; activé par défaut si la tablette est tactile ou si on ne le sait pas) ; **« Titre des réunions »** (repris de la salle ; « Sinon : seulement « Occupé » ») ; **nom de l'écran** (défaut « Porte A101 »). Pas de QR (hors V1).
4. **Terminé** : enregistrement (`RoomCalendarId`, options, nom), `Notifier.NotifyScreen` : la tablette montre la porte en quelques secondes. « Écran de salle créé · La tablette affiche maintenant « Salle A101 ». Rien à publier : l'écran suit la salle et ses règles. » + « ● En ligne · Porte A101 ». Bouton **Terminer**.

Fermer après l'étape 1 : « La tablette est connectée. Fermer sans choisir de salle ? » ; l'écran reste, en « Salle non choisie », et sa carte propose **« Choisir la salle »** (même assistant, étapes 2-4).

### 6.3 Liste des écrans
- Filtre **Type** (menu Filtres, §181) : nouvelle valeur **« Écrans de salle »**, proposée seulement s'il en existe (comme les bornes). La maquette montre encore des onglets : suivre le menu Filtres actuel (maquette corrigée).
- **Carte** (carte allégée `.ec-*` du §211, à respecter) : l'aperçu est remplacé par un **bandeau de la couleur de l'état** (vert Libre, orange Bientôt, rouge Occupé, gris sans salle / indisponible / application retirée) avec le nom de la salle, le mot de l'état et « jusqu'à … » ; en dessous, nom de l'écran (icône porte), pastille En ligne / Hors ligne, « Tablette Android · paysage ». Pied « Rien à publier · suit la salle ». Les écrans de salle sont **exclus** des compteurs « à publier », du bouton Publier et de l'approbation des publications (§162).
- Clic sur la carte : fenêtre centrée **« Modifier l'écran de salle »** (onglets Salle / Affichage = étapes 2-3 de l'assistant, bouton Enregistrer). Menu ⋮ inchangé (Paramètres de l'écran, Simuler, commandes de l'appareil…). Dans `ScreenSettingsDialog.razor`, le champ « Calendrier de la salle » est masqué pour un écran de salle.
- **Simuler l'écran** : le simulateur montre la porte ; les boutons y sont actifs mais ne réservent rien (« Aperçu : réservation simulée »).

---

## 7. Parcours 4 — sur la tablette (maquette vue 6, nouvelle)

Module `wwwroot/player/room-door.js`, monté par `player.js` quand le flux contient `room` (à la place de la liste de lecture, comme la borne). Deux couches : **Logique** (fonctions pures : état, durées possibles, textes ; testées sous Node dans `Linkii.Poc.Tests/js`, comme `building-view.js`) et **Affichage**. L'état est recalculé chaque seconde à partir de la dernière copie des rendez-vous (réutiliser `LinkiiComposition.roomState`, exporté), donc la porte continue de passer de Libre à Occupé même hors ligne.

**Disposition** (paysage ; en portrait, les deux panneaux s'empilent) :
- **Panneau d'état** (≈ 60 %, couleur de l'état) : nom de la salle · places · lieu ; grand mot **LIBRE / BIENTÔT OCCUPÉ / OCCUPÉ** ; « jusqu'à 15:00 » (ou « toute la journée ») ; si la salle est réservable, libre ou bientôt occupée, les boutons activés et l'écran en ligne : **« Réserver maintenant, pour… »**, puces des durées (une durée impossible est grisée avec sa raison : « Conflit à 15:00 », « Hors horaires ») et bouton **« Réserver 14:10 – 14:40 »** (« Choisissez une durée » tant qu'aucune n'est choisie). Cibles tactiles ≥ 64 px.
- **Panneau du jour** (thème clair ou sombre) : heure, « AUJOURD'HUI », 4 prochains créneaux « 15:00 – 16:00 · Occupé » (le titre si les titres sont affichés), « Rien d'autre aujourd'hui ».
- Le **coin haut-droit (140 dp)** reste vide : 5 touchers y ouvrent le menu technicien.

**Réserver** : toucher une durée → la choisir ; toucher « Réserver… » → « Réservation… » (boutons bloqués) → `POST /api/player/room/book` :
- **200** : confirmation plein panneau « ✓ Réservée jusqu'à 14:40 » (4 s), puis OCCUPÉ « Réservé sur place · jusqu'à 14:40 » (ce titre générique est montré même si les titres sont masqués) ;
- **409** : « Déjà réservée : la salle vient d'être prise », puis relecture ;
- **autre erreur ou délai dépassé (15 s)** : « Réservation impossible. Prévenez l'administrateur. » (8 s). **Jamais « Réservée » sans réponse 200.**
Le choix d'une durée s'efface après 20 s sans toucher.

**États particuliers** :
| Cas | La porte affiche |
|---|---|
| Salle en « Afficher seulement » (ou boutons désactivés) | L'état, sans zone de réservation. |
| Hors ligne | L'état (copie locale) + « Hors ligne : réservation impossible », boutons grisés. |
| Invitation refusée par la salle (§8.4) | « La salle a refusé la réservation » (10 s, une fois), puis l'état réel. |
| Écran sans salle | Gris « APPAIRÉE · EN ATTENTE DE CONFIGURATION ». |
| Salle Actif = off | Gris « SALLE INDISPONIBLE ». |
| Application désinstallée | Gris « APPLICATION RETIRÉE ». |
| Agenda illisible | Dernière copie + « Mise à jour impossible » ; au-delà de 30 min : gris « DONNÉES INDISPONIBLES ». |
| Urgence (§159) | Le message d'urgence couvre la porte, comme sur tout écran. |
| Plages de veille de l'aire | Comme tout écran (rien de particulier en V1). |

---

## 8. Moteur : lecture, état, réservation

### 8.1 Lecture et état (`RoomStatusService`, serveur)
`Status(tenant, roomId)` : rendez-vous de la salle du début de la journée locale (fuseau `Tenant.Timezone`) à +2 jours, par `CalendarService` (cache 20 s), **plus les réservations Linkivi** (`RoomBooking` en attente ou confirmées) qui n'apparaissent pas encore dans l'agenda (doublon = un rendez-vous qui commence et finit à ±2 min près). Rend des intervalles (avec titre seulement si la salle ou l'écran les affiche) et l'état calculé par `RoomRules.State` (même règle que `roomState` en JS ; **jeu de cas partagé** C# / Node pour garantir le même résultat). Cache de 30 s par salle. Servi à : la porte (`GET /api/player/room`), Contenus, cartes des écrans, onglet Réservations, aperçu de l'assistant.

### 8.2 Durées possibles (`RoomRules.Options`, fonction pure)
À l'instant T (minute courante, secondes ignorées), pour chaque durée D de la liste : proposée si **D ≤ durée max**, **T + D ≤ début du prochain rendez-vous − tampon**, **T dans les horaires réservables** (jour et heures) et **T + D ≤ fin des horaires** du jour. Sinon grisée avec la raison (« Conflit à HH:MM » / « Hors horaires »). Salle occupée : aucune durée. Un rendez-vous sur la journée entière ne compte que si `RoomAllDay`. La même fonction existe côté porte (affichage) ; **le serveur décide**.

### 8.3 Mode d'écriture de chaque salle (`SharedCalendar.RoomWriteMode`)
Calculé à l'installation, à « Gérer les salles », à « Synchroniser » et quand un droit change :
| Mode | Quand | Écriture |
|---|---|---|
| `none` | Source en « Afficher seulement » ; CalDAV / Exchange ; agenda Google en lecture (`accessRole` reader / freeBusyReader) ; droit perdu. | Aucune : pas de boutons. |
| `direct` | Microsoft : agenda du compte `cal:{id}`. Google : agenda en `writer` / `owner` (hors ressource). | Événement créé **dans l'agenda de la salle** : Graph `POST /me/calendars/{id}/events` ; Google `POST /calendars/{id}/events`. |
| `invite` | Microsoft : adresse d'une boîte de salle. Google : ressource `…@resource.calendar.google.com`. | Événement créé **dans l'agenda du compte connecté**, la salle **invitée** comme ressource (Graph `POST /me/events`, participant `type: resource` + lieu ; Google `POST /calendars/primary/events`, `attendees: [{ email, resource: true }]`). La salle accepte ou refuse seule (acceptation automatique par défaut chez Microsoft et Google). C'est le chemin standard : aucun droit de délégation sur la boîte de la salle. |
| `local` | Lien ICS. | `RoomBooking` seulement (visible sur les écrans Linkivi, pas dans Outlook ni Google). |

**Droits** : nouveau motif de connexion `AccountPurposes.Rooms` (`"rooms"`, à accepter par `AccountPurposes.Parse`) : Microsoft `Calendars.ReadWrite` + `Calendars.ReadWrite.Shared` ; Google `https://www.googleapis.com/auth/calendar.events`. `ScopesFor` / `RequiredScopes` des deux classes ; `MicrosoftAuth.Has` / `GoogleAuth.Has` disent si le compte l'a. `ConnectedAccounts` ajoute la ligne d'autorisation « Agendas (réservation) ». L'écriture n'est **jamais** demandée si aucune source n'est en « Afficher et réserver ».

**Connecteurs** : interface `IWritableCalendarConnector` (à côté de `ICalendarConnector`) : `Create`, `Delete` (événement Linkivi seulement), `Response` (réponse de la salle à une invitation). Implémentée par `M365Connector` (ajouter à `GraphService` un envoi POST / DELETE) et `GoogleConnector`. ICS, CalDAV, Exchange : non.

### 8.4 Réserver (`POST /api/player/room/book`, corps `{ minutes }`) — `RoomBookingService.Book`
1. `Auth(req)` + `Gate` (comme `HaCommand`) ; l'écran est un écran de salle **avec** salle ; l'application est installée ; la salle est `Room && !Off` ; boutons de l'écran activés ; mode ≠ `none` ; limite : 1 réservation / 10 s et 12 / heure par écran. Sinon 400 / 403 avec un message lisible.
2. **Verrou par salle** (une réservation à la fois par salle).
3. Relecture **sans cache** des rendez-vous + réservations locales ; `RoomRules.Options` doit autoriser `minutes`, sinon **409** `{ error: "Déjà réservée", until }`.
4. `RoomBooking` créé (en attente).
5. Écriture selon le mode : sujet = `Tenant.RoomApp.BookTitle` (« Réservé sur place ») ; texte « Réservé depuis l'écran « Porte A101 » (Linkivi). » ; **marquage** (Microsoft : propriété étendue `LinkiviBooking` = identifiant de la réservation, et `transactionId` = même identifiant contre les doublons ; Google : `extendedProperties.private.linkiviBooking` + identifiant d'événement dérivé de la réservation). `direct` et `local` → confirmée ; `invite` → en attente de la salle.
6. Cache de la salle vidé (`CalendarService`, `RoomStatusService`) ; **tous les écrans de la salle** prévenus par le hub (comme « DataChanged ») ; réponse **200** `{ from, to }`.
7. Erreurs : 401 / 403 / jeton révoqué → réservation « échec », **source marquée « droit perdu »** (ses salles passent en `none`), alerte « Applications et API » : « Le compte it@acme.ch ne peut plus créer de rendez-vous dans « Salle A101 » » ; délai dépassé → échec, 504. Aucune écriture différée.

**Réponse de la salle (mode `invite`)** : tâche de fond (`IHostedService`, toutes les 30 s) sur les réservations en attente de moins de 10 min : Microsoft `GET /me/events/{id}?$select=attendees` (réponse de la salle) ; Google `events.get` (`responseStatus` de la ressource). Acceptée ou provisoire → confirmée. **Refusée** → Linkivi supprime **son** événement, réservation « refusée », écrans prévenus (message de la porte, §7), notification. Toujours sans réponse après 10 min → confirmée (salle à validation manuelle ; à noter).

### 8.5 Test de réservation (Configuration › Santé)
Pour chaque salle réservable : crée un événement « Test Linkivi » de 15 min **demain à 02:00** (heure locale), puis le supprime ; en mode `invite`, attend la réponse de la salle (60 s au plus). Résultat par salle : « Écriture possible » / « Le compte ne peut pas écrire dans cet agenda » / « La salle n'a pas répondu à l'invitation » / ICS : « Réservations Linkivi : rien à tester ». Lancé seulement par le bouton.

### 8.6 Temps réel et délais
La porte relit `GET /api/player/room` toutes les 30 s et dès qu'un message du hub arrive. Un rendez-vous pris dans Outlook apparaît en moins d'une minute (30 s + cache 20 s). Une réservation faite sur la tablette : OCCUPÉ tout de suite sur cette tablette (réponse), et sur les autres écrans de la salle en quelques secondes (hub).

---

## 9. Données

- **`Tenant.RoomApp`** (`RoomAppSettings`) : `Durations` ([15, 30, 45, 60, 120]), `MaxMin` (120 ; 0 = journée), `Days` (lun-ven), `From` (« 07:00 »), `To` (« 19:00 »), `BufferMin` (0), `BookTitle` (« Réservé sur place »), `Sources` (source → `read` / `write` ; ICS = `local`), `LostRights` (sources dont le droit d'écrire a été perdu). L'application est installée si la ligne `AppInstalls` existe (comme les autres).
- **`SharedCalendar`** : + `RoomWriteMode` (`none` / `direct` / `invite` / `local`). Le reste inchangé ; pas de règles par salle (D15).
- **`Screen`** : + `Room` (`RoomScreen?` : `Theme` light / dark, `Buttons`, `Titles`, `Revision`) ; `RoomCalendarId` réutilisé ; + `DeviceTouch` (`bool?`, remonté par `/api/player/device`). Un écran de salle n'a ni `Kiosk` ni liste de lecture. `Revision` entre dans `Notifier.Revision` / `Helpers.Revision`, comme la borne, pour que le player relise sa configuration.
- **`RoomBooking`** (nouvelle liste, `IClientOwned`, isolée par organisation comme les écrans) : `Id`, `ClientId`, `RoomId`, `ScreenId`, `StartUtc`, `EndUtc`, `Mode`, `State` (pending / confirmed / refused / failed), `ProviderEventId`, `Error`, `CreatedUtc`. Purge au-delà de 90 jours (démarrage + chaque jour).
- **DTO** : `DeviceDto` + `Touch` (`bool?`).
- **Flux du player** : `PlayerFeed.ForScreen` + `["room"]` = `{ state: ok | none | off | retired, theme, buttons, titles, room: { name, cap, place, soon, allDay } }` ; les rendez-vous et les durées possibles viennent de `GET /api/player/room` (`{ events, options, mode, canBook, until }`), jamais de secret, titres seulement si l'écran les affiche.

---

## 10. Architecture : fichiers

**Nouveaux** :
- `Linkii.Poc/Apps/room-booking.json` — manifeste (modèle `emergency.json`).
- `Linkii.Poc/Apps/RoomBooking.cs` (ou un dossier `Apps/Rooms/`) — `RoomRules` (pur : `State`, `Options`, `Merge`, `WriteModeOf`), `RoomStatusService`, `RoomBookingService` (`Book`, `Test`, tâche des réponses), `RoomSetup` (`Install`, `Uninstall`, `Migrate`), `RoomSetupDrafts`.
- `Components/RoomAppWizard.razor`, `Components/RoomRulesDialog.razor`, `Components/RoomStatusTab.razor`, `Components/RoomScreenWizard.razor` (création + modification).
- `wwwroot/player/room-door.js` (styles intégrés, comme `kiosk.js`).

**Modifiés** :
- `Apps/CalendarConnectors.cs`, `DataServices.cs` (`GraphService` : POST / DELETE ; `ListCalendars` Google avec `accessRole`), `Apps/MicrosoftAuth.cs`, `Apps/GoogleAuth.cs`, `AccountPurposes`, `Apps/ConnectedAccounts.cs`.
- `Components/Pages/Integrations.razor` (ouverture de l'assistant depuis le Store, reprise après OAuth ; retrait d'une application d'agenda refusé si des salles l'utilisent).
- `Components/Pages/AppDetail.razor` (branche « salles » ; ne rien casser pour les autres applications).
- `Components/Pages/Screens.razor` (menu Ajouter, filtre Type, carte, fenêtre de modification, exclusion de la publication), `Components/ScreenSettingsDialog.razor`, `Components/CalendarSourceDialog.razor` (D5).
- `Program.cs` (`GET /api/player/room`, `POST /api/player/room/book`, `Touch` dans `/api/player/device`, aperçu, services, tâche de fond), `Preview.cs` (`["room"]`, aperçu), `Notifier` / `Helpers` (révision), `Models.cs`, migration D11.
- `wwwroot/player/player.js` (montage de la porte ; `touch` dans `webDevice()` et dans l'objet de l'app Android).

**Tests** (`Linkii.Poc.Tests`) : `RoomRules` (états, durées : conflit, horaires, max, tampon, journée entière, bientôt occupé ; même jeu de cas que les tests Node de `room-door.js`) ; réservation avec un faux connecteur en écriture (200, 409, droit perdu, limite, salle d'un autre écran refusée, salle désactivée, application retirée) ; réponses d'invitation (acceptée, refusée) ; installation depuis un brouillon ; désinstallation ; isolation entre organisations ; `/api/player/device` avec `touch`.

---

## 11. Sécurité et vie privée

- **Porte** : jeton de l'écran ; seulement **la** salle de l'écran ; une seule action (`book`) ; limite de débit ; titres jamais envoyés si l'écran ne les affiche pas (filtrés côté serveur) ; aucun secret.
- **Droits minimum** : écriture demandée seulement pour une source en « Afficher et réserver » ; le texte dit ce que Linkivi pourra faire (« créer des rendez-vous dans vos salles ; jamais modifier les rendez-vous existants ») et qu'on peut retirer l'accès chez Microsoft ou Google.
- **Google** : `calendar.events` est un accès « sensible » : l'application OAuth de Linkivi devra être **vérifiée par Google** avant la production (sinon écran d'avertissement et 100 utilisateurs au plus). **Microsoft** : `Calendars.ReadWrite` est accordé par l'utilisateur, mais certains tenants exigent l'accord d'un administrateur (cas déjà géré au retour : `microsoft=admin`).
- Jamais « Réservée » sans confirmation du serveur ; aucune file d'écritures différées.
- Aucun nom de personne sur la tablette ; journal gardé 90 jours.
- Linkivi ne supprime que ses propres événements, reconnus par leur marquage.

---

## 12. États et cas limites (maquette vue 5)

| Si… | Alors… |
|---|---|
| L'application est **désinstallée** | Confirmation « N écrans de salle seront désactivés ». Écrans gardés, porte « APPLICATION RETIRÉE », réservations refusées. `SharedCalendar.Room` gardé (une réinstallation retrouve tout). Agendas et applications d'agenda gardés. Entrée de menu retirée. |
| Une salle passe à **Actif = off** | Elle disparaît partout ; ses écrans : « SALLE INDISPONIBLE » ; réservation refusée. |
| On veut retirer une **application d'agenda** utilisée par des salles | Refusé : « 3 salles utilisent Google Agenda. » |
| Le **droit d'écrire** est retiré chez Microsoft / Google | Source « droit perdu », ses salles en affichage seul, alerte (Applications et API), Configuration : « Autoriser à nouveau ». |
| L'**écran est hors ligne** | Pastille rouge (Écrans, Contenus) ; tablette : « Hors ligne : réservation impossible ». |
| **Deux tablettes** pour la même salle | Verrou par salle + 409 pour la seconde ; les deux se mettent à jour par le hub. |
| Une réunion est prise dans Outlook **en même temps** | `invite` : la salle refuse la seconde demande ; `direct` : les deux existent, la porte affiche Occupé. |
| **Lien ICS** | Réservations « Linkivi seulement », annoncées partout (assistant, Configuration, Santé). |
| Fin d'essai ou espace suspendu | Le player est bloqué comme aujourd'hui (`Gate` → 403). |
| Un écran de salle est **supprimé** | Ses réservations restent dans le journal. |

---

## 13. Lots d'implémentation (dans l'ordre)

| Lot | Contenu | Fin du lot |
|---|---|---|
| **0 — Valider** (utilisateur) | Maquette (surtout la **vue 6** et l'assistant d'écran), décisions §1.2. | Réponses notées. |
| **1 — Socle** | Données (§9), `RoomRules` + tests C# et Node, `RoomStatusService`, manifeste, migration D11. | Build + tests. |
| **2 — Application** | Fiche du Store, `RoomAppWizard` (6 étapes, brouillon, reprise après OAuth, motif `rooms`), `RoomSetup`, page de l'application (Contenus, Configuration sans le test, Infos), D5, refus de retrait. | Build + tests ; un essai dans le navigateur (instance de test) avec un lien ICS public, seule source essayable sans compte. |
| **3 — Écran de salle (affichage)** | Menu Ajouter, `RoomScreenWizard`, `touch` du player, `["room"]` dans le flux, `GET /api/player/room`, `room-door.js` (affichage, sans réservation), carte, filtre, fenêtre de modification, simulateur. | Build + tests ; la porte dans le simulateur avec un vrai lien ICS. |
| **4 — Écriture Microsoft / Google** | `IWritableCalendarConnector`, modes d'écriture, test de réservation, tâche des réponses d'invitation, droit perdu + alerte. | Build + tests (faux connecteurs). Vrais comptes : par l'utilisateur. |
| **5 — Réservation sur la tablette** | `POST /api/player/room/book` (verrou, 409, limites), mode `local` (ICS), hub, zone de réservation et messages de la porte, réservation simulée dans l'aperçu. | Build + tests ; parcours complet en local avec une salle ICS et le **player web ouvert dans un onglet du navigateur** (il s'appaire comme une tablette). |
| **6 — Onglet Réservations** | `RoomStatusTab` (frise, « Maintenant », filtre, aucune action), D16. | Build + tests. |
| **7 — États et finitions** | Désinstallation / réinstallation, cas du §12, mode simple, relecture complète, `DEV-POC.md`. | Build + tests ; section DEV-POC finale. |

Chaque lot : `dotnet build` sans erreur, tests `Linkii.Poc.Tests` (et Node), puis une section numérotée de `DEV-POC.md` (§214+). Vérifications dans le navigateur : seulement aux lots 2, 3 et 5.

---

## 14. Ce qui ne s'essaie qu'en réel (à reporter tel quel dans DEV-POC)

1. **Microsoft 365** avec une vraie boîte de salle : acceptation automatique de l'invitation, délai avant que l'agenda de la salle la montre, refus en cas de conflit, organisateur = compte connecté.
2. **Compte Microsoft personnel** (outlook.com) : agendas seulement, mode `direct`.
3. **Google Workspace** avec une ressource (salle) : invitation acceptée ; agenda Google en écriture : mode `direct`.
4. **Vérification Google** de l'accès `calendar.events` avant la production ; tenants Microsoft qui exigent l'accord d'un administrateur.
5. **Tablette Android** : touchers, taille des boutons, coin du menu technicien, paysage / portrait, hors ligne, veille, mode kiosque.
6. **Délais** : réunion prise dans Outlook → porte en moins d'une minute ; réservation sur la tablette → Outlook / Google.

---

## 15. Hors V1 (repoussé)

Page de réservation par **QR code** (et le QR sur la porte) · **« + 15 min » / « Terminer »** · libération automatique si personne n'arrive · réservation « plus tard » · **CalDAV / Exchange en écriture** · règles propres à une salle · vues Semaine / Liste, origine des réservations et annulation dans l'onglet Réservations · statistiques d'occupation · équipements, « Hors service », type d'écran « Tableau des salles », salle libre la plus proche, logo / langue / taille de police · API et webhooks des réservations · lien avec les capteurs de présence Home Assistant.
