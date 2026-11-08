# Réservation de salle sur place — analyse complète (installation → configuration finale)

> **⚠ Remplacée en partie par `ANALYSE-v2.md` (8 nov. 2026).** Le produit, le parcours et les plans y sont redéfinis (application « Réservation de salle » du Store + type d'écran « Écran de salle », **tous les plans** : la recommandation « Pro » ci-dessous est annulée). Ici restent valables : §2 (existant), §4 (où écrire), §6 (règles métier), §7 (architecture), §8 (sécurité), §9 (données) et §11 (vérifications).

> Document de reprise. Rédigé le 8 novembre 2026. **Aucun code n'a été écrit** : ce document décrit l'existant (vérifié dans le code), l'écart avec le besoin, le parcours cible, les décisions à prendre et le découpage en lots.
> Visuels associés : `maquettes/salle-reservation-sur-place.html` (l'écran et le téléphone, interactif) et `maquettes/salle-parcours-complet.html` (parcours complet, phase par phase).
> Une autre session Claude doit pouvoir implémenter à partir de ce seul fichier + les deux maquettes. Lire d'abord §0 (règles du projet) et §12 (décisions ouvertes).

---

## 0. Règles du projet à respecter (venues de l'utilisateur, non écrites ailleurs)

- **Maquette validée avant tout code** pour une refonte d'écran. Les deux maquettes ci-dessus sont des propositions : l'utilisateur doit les valider (ou les corriger) avant de coder l'interface.
- **Interrupteurs, jamais de cases à cocher** pour les options on/off.
- **Vocabulaire générique** (pas de termes sectoriels) ; les fonctions avancées sont **activables** (mode simple / mode expert dans Réglages). La réservation sur place doit être **masquée en mode simple** ou limitée à un réglage minimal.
- Noms commerciaux des licences : **Free, Essential, Pro, Enterprise**. Dans le code, Pro s'appelle encore `Plans.Growth` / `IsGrowth` (libellés seulement renommés). Pendant l'essai, le client a Pro d'office.
- Journal de développement : `DEV-POC.md`, sections numérotées (dernière connue : §208). Cette fonction prendrait **§209+**.
- L'arbre de travail contient beaucoup de modifications non commitées d'autres chantiers (assistant d'ajout simplifié, etc.) : ne stager que les fichiers de ce chantier, ne rien commiter sans demande.
- Dépôt : `C:\DEV\En-cours\Linkii` (.NET / Blazor, projet serveur `Linkii.Poc`, players `Linkii.AndroidPlayer`, `Linkii.WindowsPlayer`, `Linkii.RaspberryPlayer`, tests `Linkii.Poc.Tests`). Branche courante : `fenetres-apercu-config`.

---

## 1. Le besoin

1. **Un calendrier lié à un écran** (tablette à la porte de la salle).
2. Quand le calendrier a un rendez-vous en cours : l'écran affiche **Occupé** ; sinon **Libre**.
3. Les **prochaines réservations** sont visibles sur l'écran.
4. Une personne peut **réserver pour une durée fixe** (15, 30, 45 min, 1 h, 2 h, …) depuis l'écran, ou autrement.

Les points 1 à 3 existent déjà (§146-147). **Le point 4 est entièrement nouveau**, et il change la nature de la fonction : aujourd'hui Linkivi *lit* un calendrier ; il devra aussi *écrire* dedans.

---

## 2. Existant vérifié dans le code

| Sujet | Où | Ce qui existe |
|---|---|---|
| Une salle = une ligne de calendrier | `Models.cs` (~l.787-793), `Apps/SharedCalendars.cs` | `SharedCalendar` : `Room`, `RoomName`, `RoomCap`, `RoomPlace`, `RoomAllDay`, `RoomSoon` (10), `RoomTitles` (false = seulement « Occupé »). `SharedCalendarSources.RoomContent` la présente comme un contenu « agenda » de même Id. |
| Écran ↔ calendrier | `Models.cs` l.834, `Components/ScreenSettingsDialog.razor` | `Screen.RoomCalendarId`, champ « Calendrier de la salle » (visible s'il existe une salle). Servi par la source « Cet écran » (`Compositions.cs` l.262). |
| Affichage porte | `wwwroot/player/composition.js` (`roomState`, `roomHtml`), `Compositions.cs` (`RoomType`) | Élément `room` : `door`, `dot`, `rect`, `circle`, `board`. États LIBRE (vert) / BIENTÔT OCCUPÉ (orange) / OCCUPÉ (rouge) / SALLE NON CHOISIE (gris). État **calculé sur l'écran** chaque seconde à partir des événements ; copie hors ligne gardée. Prochains rendez-vous (`nextN`, 0 à 3). |
| Éditeur | `Components/Pages/CompositionEditor.razor`, `wwwroot/compo-editor.js` | Menu « Salles » (si ≥ 1 salle), 4 modèles, barre Simuler (Réel/Libre/Bientôt/Occupé/Non choisie). |
| Assistant d'ajout | `Components/CalendarWizard.razor`, `CalendarSourceDialog.razor` | Interrupteur « Salle » + réglages par calendrier (Microsoft, Google, CalDAV, Exchange). |
| Connecteurs calendrier | `Apps/CalendarConnectors.cs` | Interface `ICalendarConnector` avec **une seule méthode : `GetEvents`** (lecture). Implémentations : `M365Connector` (Graph), `GoogleConnector`, `IcsConnector`, CalDAV, EWS. Secrets et liens restent côté serveur (`SafeHttp`). |
| Droits OAuth | `Apps/MicrosoftAuth.cs`, `Apps/GoogleAuth.cs`, `Apps/ConnectedAccounts.cs` | Microsoft : `Calendars.Read` + `Calendars.Read.Shared`. Google : `calendar.readonly`. Libellés « Agendas (lecture) ». **Aucun droit d'écriture n'est demandé aujourd'hui.** |
| Sources | `Apps/SharedCalendars.cs` | `m365`, `google`, `caldav`, `ics`, `ews`. Les textes disent « lecture seule ». |
| Le player parle au serveur | `Program.cs` l.763+ (`/api/player/...`) | Authentification de l'appareil par `Auth(req, db)` + `Gate(s, db)`. Précédent d'**action poussée depuis l'écran** : `HaCommand` (`/player/app/{id}/ha`) — vérifie que le contenu est bien publié sur cet écran, que l'action est autorisée, appelle le fournisseur côté serveur, puis `live.Nudge(clientId)`. **C'est le modèle à copier** pour « réserver ». |
| QR public | `Program.cs` l.572 `/q/{code}` | Route publique, code court, `no-store`, anti-robots, comptage Pro. Précédent pour la page de réservation par QR. |
| Borne tactile | listes interactives (§168-170), `kiosk-events` | Le player sait déjà gérer le tactile en mode borne (listes interactives). **À vérifier : une composition (élément `room`) est-elle tactile ?** (voir §11). |
| Temps réel | `Notifier`, `live.Nudge`, SSE/poll | Le serveur peut prévenir les écrans d'un changement sans publication. |
| Approbation / droits | `Approvals`, membres + partage lecture seule | Une réservation n'est pas une publication : elle ne doit **pas** passer par l'approbation. |

**Jamais essayé** (note du §146) : un vrai calendrier, une vraie tablette Android, d'autres formats que 16:10.

---

## 3. L'écart : ce qui manque pour réserver

1. **Écriture dans le calendrier** (aucun connecteur n'écrit, aucun droit d'écriture demandé).
2. **Une action tactile** sur l'écran de la salle (boutons de durée) + un canal sécurisé écran → serveur.
3. **Une règle de disponibilité** côté serveur (qui arbitre les conflits).
4. **Une page web de réservation** (QR code) pour les écrans sans tactile et les téléphones.
5. **Des réglages** de réservation par salle (durées, durée max, libération, etc.).
6. **Un journal** des réservations faites depuis Linkivi (supervision, erreurs, statistiques).

---

## 4. Décision structurante : où écrit-on la réservation ?

| Option | Principe | Pour | Contre |
|---|---|---|---|
| **A. Écrire dans le calendrier source** | Le serveur crée un événement dans Outlook / Google / CalDAV / Exchange. | Une seule vérité ; visible partout (Outlook, téléphone) ; aucune double réservation. | Demande des **droits d'écriture** (nouveau consentement OAuth, droit « éditeur » sur la salle) ; **impossible pour un lien ICS** (lecture seule par nature). |
| **B. Réservations locales Linkivi** | Linkivi garde sa propre liste et la fusionne avec les événements lus. | Marche avec tous les calendriers, y compris ICS ; aucun droit à demander. | Invisible dans Outlook/Google : **risque de double réservation** avec les invitations faites dans l'agenda. |
| **C. Hybride (recommandé)** | Par salle, un mode : « Lecture seule » (défaut) ou « Réservation sur place ». Si le calendrier accepte l'écriture → A. Sinon → B avec un avertissement clair (« visible seulement sur les écrans Linkivi »). | Fonctionne partout ; l'option sûre est proposée quand elle est possible ; défaut = ne rien changer à l'existant. | Deux chemins à maintenir et à tester. |

**Recommandation : C**, en codant **A d'abord pour Microsoft 365 et Google** (les deux sources majoritaires, déjà connectées par « Se connecter avec… »), et B pour ICS ensuite. CalDAV et Exchange en dernier lot.

Dans tous les cas, **le serveur Linkivi est l'arbitre** : le player ne décide jamais seul qu'une salle est libre (voir §7).

---

## 5. Le parcours complet, phase par phase

Sept phases. Pour chacune : qui, où dans Linkivi, ce que voit la personne, ce qui se passe côté serveur, existant / à construire.

### Phase 1 — Mise en route de l'organisation
- **Qui** : l'administrateur du client.
- **Où** : inscription (e-mail / Microsoft / Google), essai gratuit (Pro d'office), page « Premiers pas ».
- **Existant** : tout (§122, §190). Rien à construire.
- **À décider** : la réservation sur place est-elle réservée à **Pro** (comme commandes, approbation, batterie) ? Recommandation : **oui, Pro** (elle écrit chez le client et suppose un écran tactile dédié) ; la **lecture** (libre/occupé) reste dans tous les plans comme aujourd'hui. Pendant l'essai : disponible.

### Phase 2 — Installer l'écran de la salle
- **Qui** : l'administrateur ou le technicien sur place.
- **Où** : « Connecter un écran » → choix du player (menu latéral « Installer le player »), `/download/android` (APK), `/download/windows` (MSI), Raspberry Pi.
- **Ce que voit la personne** : installe le player sur la tablette, saisit/affiche le code d'appairage, l'écran apparaît dans « Écrans ». Elle règle **paysage**, plein écran, veille désactivée à la porte (ou plage de veille de la salle).
- **Existant** : tout le flux (§100-101, §126, appairage, ré-appairage, kiosque).
- **À construire** : rien d'obligatoire. **Recommandation** : un réglage d'écran « **Écran tactile** » (interrupteur) dans Paramètres de l'écran, car il conditionne l'affichage des boutons de durée (sinon on affiche le QR code seul). À défaut de détection fiable côté Android, c'est un choix manuel.
- **À vérifier sur appareil** : tactile réellement transmis à la composition (§11), mode kiosque (barre système masquée), luminosité, rotation.

### Phase 3 — Connecter le calendrier de la salle
- **Qui** : l'administrateur.
- **Où** : Applications → « Comptes connectés » (Microsoft / Google), puis Applications › Calendriers partagés (assistant `CalendarWizard`).
- **Ce que voit la personne** : « Se connecter avec Microsoft » (ou Google). **Nouveau** : un choix en deux niveaux de droits.
  - « **Lire les agendas** » (comme aujourd'hui) — suffit pour afficher Libre/Occupé.
  - « **Lire et réserver** » — nécessaire pour que les réservations faites sur l'écran soient écrites dans l'agenda de la salle.
  Le texte doit dire clairement ce que Linkivi pourra faire (« créer des rendez-vous dans l'agenda de la salle, jamais lire d'autres boîtes que celles que vous choisissez »).
- **Côté serveur** :
  - Microsoft : droits délégués `Calendars.ReadWrite` + `Calendars.ReadWrite.Shared` (en plus de `.Read`). Pour une **salle** (boîte ressource Exchange), le compte connecté doit avoir le droit d'écrire dans cette boîte (délégation « Éditeur » sur le calendrier, ou rôle adapté). Les salles Exchange peuvent avoir un traitement automatique des demandes qui interagit avec la création d'événements : **cas à tester avec un vrai tenant**.
  - Google : `calendar.events` (suffisant pour créer/modifier/supprimer des événements) à la place de `calendar.readonly` ; le compte doit avoir le droit « Apporter des modifications aux événements » sur l'agenda de la salle (ressource Google Workspace).
  - Rappel de la mécanique existante : `MicrosoftAuth.Has(t, scope)` / `GoogleAuth.Has(t, scope)` indiquent si le jeton a le droit ; `ConnectedAccounts` liste les « autorisations » avec un libellé (« Agendas (lecture) ») — à compléter par une ligne « Agendas (réservation) ». Une **montée de droits** exige un nouveau consentement : prévoir le bouton « Autoriser la réservation » sur un compte déjà connecté en lecture.
  - CalDAV : écriture par `PUT` d'un fichier `.ics` (suppose des identifiants avec droit d'écriture). Exchange sur site (EWS) : `CreateItem`. ICS : **pas d'écriture** → mode B (local).
- **Existant** : connexion, assistant, aperçu des événements. **À construire** : droits d'écriture, libellés, bouton de montée de droits, test d'écriture.

### Phase 4 — Déclarer la salle et régler la réservation
- **Qui** : l'administrateur.
- **Où** : sur la ligne du calendrier (assistant et fenêtre « Gérer »), interrupteur **« Salle »** (existant), qui déplie les réglages. **Nouvelle section « Réservation »** sous les réglages existants (nom, capacité, lieu, bientôt occupé, journée entière, titres).
- **Réglages proposés** (tous par salle ; valeurs par défaut entre parenthèses) :
  | Réglage | Valeur | Remarque |
  |---|---|---|
  | Réservation sur place | interrupteur (**désactivé**) | Tant qu'il est désactivé, rien ne change. |
  | Durées proposées | 15, 30, 45 min, 1 h, 2 h (3 h, journée en option) | Liste choisie parmi des durées fixes. |
  | Durée maximale | 2 h | Limite aussi les prolongations. |
  | Prolongation | +15 min (interrupteur, activé) | Seulement si rien ne suit. |
  | Tampon entre deux réservations | 0 min (0 / 5 / 10 / 15) | Temps de nettoyage/transition. |
  | Plages réservables | heures d'ouverture de la salle (ex. 07:00-19:00, lun-ven) | Hors plage : boutons grisés « Hors horaires ». |
  | Anticipation maximale | « maintenant » seulement pour l'écran ; jusqu'à 30 jours pour la page QR | |
  | Libérer si personne n'arrive | interrupteur + délai (10 min) | Voir §7 « présence ». Ne concerne que les réservations créées sur place. |
  | Titre du rendez-vous créé | « Réservé sur place » (modifiable) | Ce qu'on voit dans Outlook/Google. |
  | QR code sur l'écran | interrupteur (activé) | Réservation depuis un téléphone. |
  | Qui peut réserver depuis le QR | Tout le monde avec le lien / Comptes de l'organisation seulement | Voir §8 sécurité. |
  | Réserver sans nom | interrupteur (désactivé sur écran = « Réservé sur place ») | Si activé sur la page QR : un nom est demandé. |
- **Mode simple** : seulement l'interrupteur « Réservation sur place » + le réglage des durées ; le reste prend les valeurs par défaut.
- **Côté serveur / données** : nouveaux champs sur `SharedCalendar` (voir §9). Rien d'autre à migrer : les salles existantes restent en lecture seule.
- **Vérification de configuration** : un bouton « **Tester la réservation** » crée puis supprime un événement de 15 min à une heure hors plage ; affiche « OK » ou le droit manquant (« Le compte connecté ne peut pas écrire dans cet agenda »). C'est le moyen de détecter tout de suite les problèmes de droits (§4).

### Phase 5 — Dessiner l'affichage de la porte
- **Qui** : l'administrateur, éditeur de compositions.
- **Où** : menu **Salles** du rail de l'éditeur (existant). Modèles actuels : porte, pastille, forme, tableau.
- **À construire** :
  - Un **nouveau modèle « Porte avec réservation »** (ou une option de l'élément `door` : `book` = interrupteur) qui ajoute, **quand la salle est libre**, la zone « Réserver maintenant, pour… » avec les boutons de durée, le bouton « Réserver HH:MM – HH:MM », et, **quand elle est occupée par une réservation faite sur place**, les boutons « + 15 min » et « Terminer maintenant ». Le QR code se place en bas du panneau de droite.
  - Réglages de l'élément : afficher les boutons (oui/non), afficher le QR (oui/non), couleurs (déjà `cFree` / `cSoon` / `cBusy`), taille des boutons (cible tactile ≥ 48 px, idéalement ≥ 64 px).
  - Barre **Simuler** : ajouter la simulation de la réservation (clic sur un bouton sans appel serveur, message « simulé »).
  - Variante portrait.
- **Maquette** : voir `maquettes/salle-reservation-sur-place.html` (scène 1) pour le dessin de référence (panneau d'état à gauche, journée à droite, QR en bas).
- **Contrainte** : un même modèle de composition sert toutes les tablettes (la salle vient de « Calendrier de la salle » de l'écran, §146) : les boutons ne doivent donc **jamais** nommer une salle précise.

### Phase 6 — Affecter et publier sur l'écran
- **Qui** : l'administrateur.
- **Où** : Paramètres de l'écran → « Calendrier de la salle » (existant) + interrupteur « Écran tactile » (nouveau, §Phase 2) ; Contenus → liste de diffusion / composition plein écran sur l'écran ; publication.
- **Côté serveur** : à la publication, `Compositions.Publish` calcule `roomIds` (calendriers dont l'écran reçoit les données). Ajouter à la publication la **politique de réservation** de la salle (durées, max, tampon, plages) pour que l'écran puisse **griser** les boutons sans appel réseau — mais l'écran ne fait que de l'affichage : **la décision finale reste au serveur**.
- **Approbation des publications (Pro)** : si elle est active, publier la composition suit le circuit habituel. Les **réservations**, elles, n'en dépendent pas.
- **Mur d'écrans / plusieurs salles** : un écran = une salle. Le tableau des salles (élément `board`) reste en lecture ; décision ouverte : y ajouter « Réserver » (voir §12).

### Phase 7 — Utilisation quotidienne
Quatre portes d'entrée, une seule vérité.

1. **Écran tactile** : toucher une durée → `Réserver` → le serveur vérifie, écrit, répond ; l'écran passe « Occupé » et affiche « Votre réservation » avec + 15 min / Terminer.
2. **QR code** : le téléphone ouvre la page de réservation de la salle → durée → nom (ou connexion Microsoft/Google) → réservé.
3. **Agenda habituel** : invitation de la salle dans Outlook/Google ; l'écran se met à jour à la prochaine lecture.
4. **API / intégration** (hors périmètre, voir §12) : le socle API/webhooks existe (§153-158).

**Présence (« no-show »)** : une réservation faite sur place démarre tout de suite, donc la personne est là. Pour une réservation faite *à distance* (QR, page, plus tard) : « confirmer sa présence » sur l'écran dans les N minutes, sinon libération (option, désactivée par défaut). Recommandation : **ne livrer la libération automatique qu'en lot 4** ; le mockup l'affiche déjà, à nuancer.

**Supervision** :
- Écran hors ligne → bandeau existant ; boutons **désactivés** (« Hors ligne : réservation impossible ») — jamais de réservation différée.
- Erreur d'écriture (droit retiré, jeton expiré) → l'écran dit « Réservation impossible, contactez l'administrateur » ; la salle passe en alerte dans le **Centre de notifications** (existant) avec le motif.
- Journal : « Réservations » par salle (date, durée, origine : écran / QR / agenda, nom, résultat) ; statistique d'occupation (Pro, dans Stats).

---

## 6. Règles métier

**États de la salle** (déjà codés, inchangés) : Libre → Bientôt occupé (X min avant le suivant) → Occupé → Libre. Salle non choisie : gris.

**Durées proposées à l'instant T** (calcul serveur, répété côté écran pour l'affichage) :
- Libre jusqu'à `prochain.début − tampon`. Une durée D est proposée si `maintenant + D ≤ prochain.début − tampon`, `D ≤ durée max`, et si `maintenant + D` reste dans la plage réservable.
- Sinon bouton grisé avec la raison : « conflit à 15:00 » / « hors horaires » / « dépasse la durée maximale ».
- Arrondi : une réservation sur place commence **à la minute courante** (pas d'arrondi au quart d'heure en V1) ; sa fin est `début + D`.

**Prolonger** : +15 min si `fin + 15 ≤ prochain.début − tampon` et durée totale ≤ max. Terminer : fin ramenée à maintenant (modifier l'événement, ne pas le supprimer, pour garder la trace).

**Qui peut modifier/terminer** : uniquement une réservation **créée par Linkivi** (marquée, §9). Un rendez-vous venu de l'agenda n'est jamais modifiable depuis l'écran.

**Concurrence** : le serveur reverifie juste avant d'écrire (relit les événements de la fenêtre), écrit, puis relit pour confirmer. Deux personnes qui touchent en même temps : la seconde reçoit « Déjà réservée » et l'écran se met à jour. Réponse HTTP `409` côté API.

**Fuseau horaire** : celui de l'écran (déjà géré pour l'état de la salle) ; le serveur écrit en UTC avec le fuseau de la salle.

---

## 7. Architecture proposée (sans code)

```
Écran (player)                    Serveur Linkivi                         Fournisseur
─────────────                     ───────────────                         ───────────
composition.js  ──POST──►  /api/player/room/{id}/book   ──►  RoomBooking.Book()
(boutons de durée)         (Auth(req) + Gate, comme HaCommand)  │ 1. vérifie : l'écran a bien cette salle, politique, plage
                                                                │ 2. relit les événements (GetEvents)
                                                                │ 3. écrit (connecteur.CreateEvent)  ──►  Graph / Google / CalDAV / EWS
                                                                │ 4. journalise (RoomBookingLog)
                                                                │ 5. live.Nudge(clientId)  ──►  tous les écrans de la salle
Téléphone ──GET──► /r/{code}  (page publique, jeton de salle) ──POST──► /r/{code}/book  (limites, anti-abus)
```

**Interface de connecteur** : étendre `ICalendarConnector` d'une **capacité d'écriture optionnelle** (par exemple une seconde interface `IWritableCalendarConnector` : créer un événement, le prolonger/terminer, indiquer `CanWrite`). Les connecteurs en lecture seule (ICS) n'en ont pas → mode B.

**Marquage des réservations Linkivi** : propriété étendue de l'événement (Graph : extension ouverte ; Google : `extendedProperties.private` ; CalDAV/EWS : propriété X-). Sert à (1) reconnaître ce que Linkivi peut modifier, (2) éviter les doublons, (3) retrouver l'origine dans le journal.

**Canal écran → serveur** : copier `HaCommand` : l'écran s'authentifie, le serveur vérifie que le contenu `room` est bien **publié sur cet écran** et que la salle est celle de l'écran. Un écran ne peut jamais réserver une autre salle.

**Mise à jour des écrans** : après écriture, `live.Nudge` + l'écran applique **immédiatement** la réponse (état Occupé) sans attendre la relecture périodique du calendrier.

**Cache** : les événements sont mis en cache par `CacheKey` ; invalider le cache de la salle après chaque écriture.

---

## 8. Sécurité et vie privée

- **Écran** : action protégée par l'authentification de l'appareil (jeton d'écran) ; liste blanche : seulement `book`, `extend`, `end` sur **la** salle de l'écran. Limite de débit (par écran).
- **Page QR publique** : le QR contient un **code de salle court** (comme `/q/{code}`), **jamais** l'identifiant du calendrier ni un secret. Risques : quelqu'un à l'extérieur qui scanne une photo du QR. Mesures proposées : (a) **QR à jeton tournant** (régénéré toutes les 5 min, affiché par l'écran, prouve la présence) — recommandé pour « Tout le monde avec le lien » ; (b) option « Comptes de l'organisation seulement » (connexion Microsoft/Google, domaine vérifié) ; (c) limite de débit par IP et par salle, durée max, pas plus de N réservations par personne et par jour. Pas de réservation à distance sans (a) ou (b).
- **Vie privée** : l'écran n'affiche **que « Occupé »** par défaut (`RoomTitles` = false) ; les titres et noms ne quittent pas le serveur. Le nom saisi par une personne (page QR) n'apparaît **pas** sur l'écran de la porte sauf si « Afficher le titre des réunions » est activé. Conserver le journal peu de temps (réglable, 90 jours par défaut).
- **Droits minimum** : demander l'écriture **seulement** si l'administrateur active « Lire et réserver ». Bien expliquer le périmètre ; ne jamais élargir silencieusement les droits d'un compte déjà connecté.
- **Secrets** : jetons et identifiants restent chiffrés côté serveur (`SecretBox`), comme aujourd'hui.
- **Coupure du fournisseur** : en cas d'échec d'écriture, **ne jamais** afficher « Réservé » : afficher l'erreur. Pas de file d'attente d'écritures différées.

---

## 9. Données à ajouter (proposition, noms indicatifs)

Sur `SharedCalendar` (à côté des champs `Room*`) :
`RoomBookable` (bool, faux), `RoomDurations` (liste de minutes), `RoomMaxMin`, `RoomBuffer`, `RoomExtend` (bool), `RoomHours` (plages par jour de semaine), `RoomReleaseMin` (0 = jamais), `RoomBookTitle`, `RoomQr` (bool), `RoomQrAccess` (lien / organisation), `RoomWriteMode` (`source` = écrire dans le calendrier, `local` = réservations Linkivi seulement), `RoomPublicCode` (code court du QR).

Sur `Screen` : `Touch` (bool, écran tactile).

Nouveau : `RoomBooking` (id, salle, début, fin, origine `screen|qr`, nom éventuel, identifiant de l'événement chez le fournisseur ou `local`, état `active|ended|cancelled|failed`, date de création) + journal. Pour le mode `local`, ces enregistrements sont **fusionnés** aux événements lus avant le calcul de l'état.

Sur les comptes connectés : un niveau de droit « réservation » par fournisseur (indicateur issu des scopes réellement accordés).

---

## 10. Découpage en lots d'implémentation

| Lot | Contenu | Critère d'acceptation | Dépend de |
|---|---|---|---|
| **0 — Valider** | Faire valider les 2 maquettes et les décisions §12. | Réponses de l'utilisateur consignées. | — |
| **1 — Écriture Microsoft/Google** | Droits d'écriture + montée de droits, interface de connecteur d'écriture, création/modification/fin d'événement, marquage, bouton « Tester la réservation ». | Sur un **vrai** tenant Microsoft et un **vrai** compte Google : un événement apparaît dans l'agenda de la salle, se prolonge, se termine ; erreur claire si droit manquant. Tests unitaires (faux connecteur) + essai réel documenté. | 0 |
| **2 — Réglages de la salle** | Champs §9, section « Réservation » (assistant + Gérer), mode simple/expert, calcul des durées possibles (fonction pure, testée). | Durées grisées correctes dans tous les cas du §6 (tests en Node/C#). | 1 |
| **3 — Réservation sur l'écran** | Endpoint écran, élément `room` avec boutons, + 15 / Terminer, Simuler, `Screen.Touch`, `live.Nudge`, journal. | Sur une **vraie tablette Android** : toucher 30 min → l'agenda reçoit l'événement et l'écran passe Occupé en moins de 3 s ; double appui simultané → un seul gagne ; hors ligne → boutons grisés. | 2 |
| **4 — Page QR** | Route publique, code de salle, jeton tournant, limites, connexion Microsoft/Google optionnelle, page mobile. | Réservation depuis un téléphone sans compte (mode lien + jeton tournant) et avec compte ; refus avec un ancien QR. | 3 |
| **5 — Local (ICS) et libération** | Mode B (réservations locales fusionnées), confirmation de présence/libération automatique. | Salle ICS réservable sur écran, mention « visible seulement sur les écrans Linkivi » ; libération après délai. | 3 |
| **6 — CalDAV, Exchange** | Écriture CalDAV (`PUT` .ics) et EWS. | Essai sur un vrai serveur (Nextcloud, Exchange). | 1 |
| **7 — Supervision et stats** | Journal « Réservations », alertes (Centre de notifications), statistiques d'occupation (Pro). | Alerte reçue sur droit révoqué ; taux d'occupation affiché. | 3 |

Chaque lot : `dotnet build` sans erreur, tests (`Linkii.Poc.Tests`), vérification dans le navigateur, **puis** `DEV-POC.md` (§209+) en précisant honnêtement ce qui n'a pas été essayé sur appareil/serveur réel (c'est l'usage du projet).

---

## 11. Points à vérifier avant ou pendant le lot 1-3 (non vérifiés dans cette analyse)

1. **Tactile dans une composition** : le player Android transmet-il les touches à `composition.js` (au-delà du mode borne/listes interactives) ? Vérifier dans `Linkii.AndroidPlayer` (WebView) et `wwwroot/player/composition.js`. Sinon, soit activer le mode tactile pour les compositions, soit implémenter la porte tactile via le mécanisme de la borne.
2. **Contexte d'authentification de l'écran** côté `composition.js` : comment il appelle déjà `/api/player/...` (Home Assistant) pour réutiliser le même mécanisme.
3. **Salles Exchange / Google (ressources)** : comportement exact de l'écriture dans la boîte d'une ressource (acceptation automatique, organisateur, invités). À essayer sur un tenant réel avant de figer l'interface.
4. **Latence de lecture** : fréquence actuelle de relecture des calendriers pour les écrans et délai réel d'apparition d'un rendez-vous créé dans Outlook.
5. **Mode simple** : où exactement masquer la section (`AppMenu` / règles du mode simple §174).
6. **Droits de l'application OAuth** : l'application Microsoft (Entra) et Google (écran de consentement) étant **fournies par Linkivi** (plus d'application par client), l'ajout de scopes d'écriture peut demander une **revue/vérification** côté Google (scopes sensibles) et un consentement administrateur côté Microsoft pour certains tenants. À anticiper : c'est un délai calendaire, pas de développement.

---

## 12. Décisions ouvertes (à poser à l'utilisateur)

1. **Hybride (C)** retenu ? Ou Microsoft/Google seulement en V1 (ICS non réservable) ?
2. **Plan** : réservation sur place réservée à **Pro** ? (recommandé)
3. **Écran sans tactile** : afficher le QR seul (recommandé) ?
4. **QR** : jeton tournant par défaut (recommandé) ou lien fixe + connexion obligatoire ?
5. **Réservation différée** (plus tard dans la journée) sur l'écran lui-même, ou seulement « maintenant » sur l'écran et « plus tard » sur la page QR (recommandé) ?
6. **Tableau des salles** (`board`) : y ajouter la réservation, ou garder lecture seule (recommandé en V1) ?
7. **Libération automatique** si personne n'arrive : V1 ou lot 5 (recommandé lot 5) ?
8. **Nom du réservant** : toujours « Réservé sur place », ou demander un nom sur écran tactile (clavier à l'écran) ? Recommandation : jamais de clavier sur l'écran de porte.
9. **Intégration Salle ↔ plan du bâtiment Home Assistant** (§195-208) : réutiliser l'état de la salle ? Hors périmètre ici.

---

## 13. Fichiers du dépôt concernés (pour s'orienter)

- Modèle : `Linkii.Poc/Models.cs` (`SharedCalendar`, `Screen`), `Linkii.Poc/Apps/SharedCalendars.cs`
- Connecteurs : `Linkii.Poc/Apps/CalendarConnectors.cs`, `DataServices.cs` (`GraphService`, Microsoft), `Apps/MicrosoftAuth.cs`, `Apps/GoogleAuth.cs`, `Apps/ConnectedAccounts.cs`
- Écran et composition : `Linkii.Poc/Compositions.cs` (`RoomType`, `Publish`), `Linkii.Poc/wwwroot/player/composition.js` (`roomState`, `roomHtml`, `tick`), `Linkii.Poc/Notifier.cs`
- Éditeur : `Components/Pages/CompositionEditor.razor`, `wwwroot/compo-editor.js` / `.css`
- Interface de configuration : `Components/CalendarWizard.razor`, `Components/CalendarSourceDialog.razor`, `Components/ScreenSettingsDialog.razor`, `Components/Pages/Integrations.razor`
- Canal écran → serveur (modèle à copier) : `Program.cs` `HaCommand` (l.1013), `/api/player/...` (l.763+) ; QR public : `Program.cs` `/q/{code}` (l.572)
- Documentation du projet : `DEV-POC.md` §146-147 (salles), §153-158 (API/webhooks), §168-170 (bornes), §174 (mode simple), §162 (approbation)
- Maquettes existantes : `maquettes/salles-reservation.html`, `maquettes/salle-google-microsoft.html`, **nouvelles** : `maquettes/salle-reservation-sur-place.html`, `maquettes/salle-parcours-complet.html`

---

## 14. Résumé en dix lignes

1. L'affichage Libre/Occupé + prochains rendez-vous **existe** ; la réservation **n'existe pas**.
2. Elle exige d'**écrire** dans les calendriers : aujourd'hui tout est en lecture seule (droits OAuth compris).
3. Recommandation : mode hybride par salle (écrire dans l'agenda quand c'est possible, réservations locales pour ICS).
4. Le **serveur arbitre** ; l'écran n'est qu'une télécommande (modèle : `HaCommand`).
5. Trois entrées : écran tactile (durées 15/30/45/1 h/2 h), QR code (page mobile), agenda habituel.
6. Réglages par salle dans l'interrupteur « Salle » existant, section « Réservation » (désactivée par défaut → aucune régression).
7. Réservé à Pro (recommandé), masqué en mode simple.
8. QR à jeton tournant ou comptes de l'organisation seulement : pas de réservation anonyme à distance.
9. Sept lots ; le plus risqué est le lot 1 (droits d'écriture réels Microsoft/Google, vérification des applications OAuth).
10. Rien n'a été essayé avec un vrai calendrier ni une vraie tablette : à faire tôt, avant l'interface.
