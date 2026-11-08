# Réservation de salle — analyse v2 : une application du Store + un type d'écran

> Rédigée le 8 novembre 2026. **Aucun code écrit.** Cette v2 **remplace la v1** (`ANALYSE.md`) pour le produit, le parcours et les plans. La v1 reste valable pour le **moteur technique** : écriture dans les agendas (§4), règles de disponibilité (§6), architecture serveur (§7), sécurité (§8).
> Maquette de référence (interactive, à valider avant tout code d'interface) : `maquettes/salle-app-parcours.html` (5 vues : Store, installation, page de l'application, Écrans, vue d'ensemble). Les maquettes `salle-reservation-sur-place.html` (dessin de la porte) et `salle-parcours-complet.html` (v1) restent des références secondaires.
> Document pensé pour qu'une autre session Claude implémente sans le contexte de celle-ci. Lire §0 d'abord.

---

## 0. Règles du projet (venues de l'utilisateur ; non écrites ailleurs)

Règles de la refonte Applications du 7 nov. (§206 de `DEV-POC.md`), **à appliquer à cette application comme à toutes** :
1. Toute application installée apparaît dans le groupe **« Applications »** du menu de gauche ; l'entrée ouvre `/apps/{id}`.
2. Flux : installation → import dans la médiathèque (racine, aucun dossier créé) → onglet **Contenus** de l'application → menu.
3. **Contenus** : liste, jamais de cartes, masqué si vide, **aucun bouton « Nouveau contenu »**, **interrupteur Actif** par ligne (désactivé = disparaît partout).
4. Configuration : bouton **« Configurer »** ; aucune fenêtre de configuration ailleurs.
5. Application à compte : bouton **Synchroniser** en haut, à côté de **Désinstaller**.
6. Les comptes se gèrent dans Réglages › Connexions › Comptes.
7. Pas de phrases d'introduction inutiles ; l'utilisateur est **très sensible aux allers-retours et à la consommation** : ne rien tester sans demande, ne pas deviner, poser une question courte si c'est ambigu.

Autres règles : **maquette validée avant de coder** ; **interrupteurs, jamais de cases à cocher** ; **vocabulaire générique** (pas de termes sectoriels) ; fonctions avancées activables / **mode simple** réduit ; formulaire modal = fenêtre **centrée** (sauf exception) ; assistants avec points d'étapes (modèle : `WizardShell`) ; licences **Free, Essential, Pro, Enterprise** (dans le code Pro = `Plans.Growth`) ; journal `DEV-POC.md` numéroté (dernier connu §208 → **§209+**).
**Précaution** : l'arbre de travail contient des modifications non commitées d'autres chantiers (`AppDetail.razor` +200 lignes, `AppMenu.cs`, `AppService.cs`, `MainLayout.razor`, `WizardShell.razor`, `Models.cs`, `app.css`, `Ico.razor`, assistants Drive/Calendrier/Canva…, `DriveFolderTree.razor` neuf). Lire `git diff` avant d'y toucher ; ne stager que les fichiers de ce chantier ; ne rien commiter sans demande.

---

## 1. Ce que l'utilisateur a décidé (8 nov.)

1. Le parcours **démarre à l'installation** de l'application **« Réservation de salle »** depuis le **Store**.
2. **Pendant l'installation**, l'utilisateur **importe ses calendriers** depuis les applications d'agenda du Store (Google, Microsoft, ICS, …).
3. Un **type d'écran spécifique « Écran de salle »** (tablette) apparaît dans le menu **Écrans**. **Impossible d'en créer sans l'application installée.**
4. **Tous les plans**, pas seulement Pro (annule la recommandation « Pro » de la v1).
5. Questions posées : faut-il une vue « libre / occupé » dans **Contenus**, ou un **menu de gestion** ? → réponse en §4.
6. L'utilisateur invite à **enrichir** la proposition (idées en §9).

**Corrections de l'utilisateur (9 nov., v2.1 — elles priment sur tout ce qui suit)** :
7. **Flux en 3 temps** : ① installer l'application « Réservation de salle » ; ② installer le **player** sur la tablette ; ③ **appairer et paramétrer avec l'assistant « Nouvel écran de salle »**.
8. Dans cet assistant, **plus d'étape « Sur quel appareil ? »** (le type d'appareil est reconnu à l'appairage) et **la première étape est la saisie du code d'appairage**. Assistant : Code → Salle → Affichage → Terminé.
9. **Linkivi ne modifie jamais un rendez-vous** (ils vivent dans des comptes Microsoft, Google ou autres). L'onglet **Réservations** de la page de l'application affiche **pour le moment seulement le statut des salles** : libre ou occupée, **de quelle heure à quelle heure**. Pas d'annulation, pas de modification, pas de détail de rendez-vous, pas de « Réserver pour quelqu'un ».

---

## 2. Le modèle produit

```
Store ──install──► Application « Réservation de salle »
                     ├─ dépend de ≥ 1 application d'agenda (Google · Microsoft 365 · ICS · CalDAV · Exchange)
                     ├─ Contenus  = les SALLES (un agenda = une salle), état en direct, interrupteur Actif
                     ├─ Réservations (onglet propre) = planning + annulation
                     ├─ Configuration = agendas, règles par défaut, test
                     └─ débloque le type d'écran « Écran de salle » dans Écrans
Écrans › Ajouter › Écran de salle ──► assistant 4 étapes ──► tablette à la porte
```

**Rapport avec l'existant (§146-147)** : aujourd'hui une salle = une ligne de calendrier partagé avec l'interrupteur « Salle » (`SharedCalendar.Room`, `RoomName`, `RoomCap`, `RoomPlace`, `RoomAllDay`, `RoomSoon`, `RoomTitles`), un champ « Calendrier de la salle » dans les paramètres d'écran (`Screen.RoomCalendarId`) et l'élément `room` de l'éditeur de compositions. **Recommandation : conserver ce modèle de données** ; l'application en devient **l'unique point d'entrée** : son assistant écrit ces champs, importe les agendas choisis, et le type « Écran de salle » renseigne `RoomCalendarId`. L'interrupteur « Salle » des assistants de calendrier (§147) est alors **masqué ou remplacé par « Gérée par Réservation de salle »** une fois l'application livrée (décision §10-6). L'élément `room` et le menu « Salles » de l'éditeur restent pour les compositions libres.

**Comment les agendas sont des « applications »** (vérifié) : les sources d'agenda sont des applications virtuelles du menu, identifiants `cal-m365`, `cal-google`, `cal-caldav`, `cal-ics`, `cal-ews` (`Apps/AppMenu.cs` l.51 : installée = `SharedCalendarSources.Enabled(tenant, source)` ou ajoutée aux intégrations). Les installer / connecter existe déjà (assistants `CalendarWizard`, comptes Microsoft/Google : `MicrosoftAuth`, `GoogleAuth`).

---

## 3. Parcours 1 — du Store à l'application installée (maquette vue 1-2)

**Entrée** : fiche dans le Store (même carte que les autres, bouton orange « Installer »). La fiche annonce : à quoi ça sert, « Nécessite un agenda : Google, Microsoft 365, lien ICS… », « Ajoute un type d'écran : Écran de salle », « Tous les plans ».

**Assistant d'installation — 6 étapes** (fenêtre centrée, points d'étapes, Retour / Continuer ; **rien n'est enregistré avant la dernière étape** — même principe que l'assistant d'ajout des intégrations, §89-90) :

| # | Étape | Contenu | Règle / validation |
|---|---|---|---|
| 1 | Bienvenue | 3 repères : vos agendas → vos règles → vos écrans. | « Il vous faut au moins un agenda. » |
| 2 | Agendas | Une ligne par application d'agenda : état (Connecté · compte / Non installée), interrupteur « utiliser ». Non installée → bouton **« Installer et connecter »** (lance le flux existant de l'application d'agenda *dans* l'assistant, retour à l'étape). | Continuer impossible sans ≥ 1 agenda connecté et choisi. |
| 3 | Salles | Pour chaque source choisie, liste des agendas trouvés : interrupteur « Salle », nom, places, lieu modifiables ; badge **« Salle détectée »** (annuaire des salles Microsoft ; ressources Google : à vérifier §11) ; ICS : badge « Lecture seule ». Compteur « N salles sélectionnées ». | ≥ 1 salle. Source sans agenda listable (ICS, CalDAV) : « Ajouter par adresse » (champ + aide, existants). |
| 4 | Réservation | Par source : **« Afficher seulement » / « Afficher et réserver »** ; « réserver » demande l'autorisation (bouton **Autoriser** → consentement OAuth d'écriture, voir v1 §5-phase 3) ; ICS : verrouillé « Réservations Linkivi ». | Continuer bloqué tant qu'une source « réserver » n'est pas autorisée (ou repasser en « Afficher seulement »). |
| 5 | Règles | Préréglages (Standard · Réunions courtes · Salles à la journée) puis : durées (15/30/45 min, 1/2/3 h), durée max, horaires réservables, prolongation +15, QR, titres des réunions (interrupteurs). | Valeurs par défaut sensées ; valables pour toutes les salles, modifiables par salle ensuite. |
| 6 | Terminé | « N salles importées depuis M agendas ». Deux boutons : **« Créer un écran de salle »** (ouvre l'assistant d'écran, §5) et « Ouvrir l'application ». | Installation effective ici : `AppService.Install`, import des salles, entrée de menu qui apparaît (animation). |

**Ce que l'installation écrit** : marque l'application installée ; pour chaque salle cochée met `SharedCalendar.Room = true` avec nom/places/lieu (créant la ligne de calendrier si l'agenda n'y était pas encore) ; droits d'écriture choisis par source (v1 §9 `RoomWriteMode`) ; règles par défaut au niveau de l'application. Comme le veut la règle 2, **« import dans la médiathèque »** : les salles sont des *contenus* de l'application (via `SharedCalendarSources.RoomContent`, même Id que le calendrier).

**Cas limites** : aucun agenda → rester à l'étape 2 avec explication ; autorisation refusée → proposer « Afficher seulement » ; aucune salle détectée → ajout par adresse ; fermeture de l'assistant en cours → rien d'enregistré, confirmation « Abandonner l'installation ? ».

---

## 4. Parcours 2 — la page de l'application : réponse à « Contenus ou menu de gestion ? » (maquette vue 3)

**Ma recommandation : option C — Contenus + un onglet « Réservations », pas de menu supplémentaire.**

| Option | Verdict |
|---|---|
| A. Contenus seulement | Trop pauvre : on voit l'état mais ni qui a réservé ni l'annulation. |
| B. Menu dédié « Gestion des salles » | À éviter : casse la règle 1 (une application = une entrée de menu) et fait doublon avec l'application. |
| **C. Contenus + onglet Réservations** | **Retenue** : respecte la structure commune (Contenus / Configuration / Infos), ajoute un seul onglet propre à l'application ; en V1 il ne montre que le statut de la journée des salles, en lecture seule. |

Structure de `/apps/{id}` (modèle : `Components/Pages/AppDetail.razor`, onglets actuels Contenus / Configuration / Infos ; en haut **Synchroniser** et **Désinstaller**) :

- **Contenus** (= les salles). Liste, une ligne par salle : pastille de l'agenda (source), nom + places/lieu, **état en direct** (● Libre jusqu'à 16:30 · ● Occupé jusqu'à 15:00 · ● Bientôt occupé) + « Ensuite : 15:30 Point projet » (le titre n'apparaît que si « Afficher les titres »), **écran associé** (pastille « En ligne / Hors ligne » ou bouton « Créer l'écran »), **interrupteur Actif**. Pas de bouton « Nouveau contenu » : les salles viennent des agendas. *Actif = off* : la salle disparaît partout (règle 3) et son écran affiche « Salle indisponible ».
- **Réservations** (onglet propre à l'application, **lecture seule, statut des salles uniquement** pour le moment). Vue **Aujourd'hui** : frise horaire (lignes = salles, colonnes = heures 8-18, trait rouge « maintenant »), barres « Occupée » avec leurs horaires, filtre par salle. Sous la frise, section **« Maintenant »** : une ligne par salle active — « Occupée de 13:30 à 15:00 », « Libre de 14:10 à 16:30 », « Libre de 14:10 à 14:15 · bientôt occupée », « Libre toute la journée ». **Aucun clic d'action** : ni détail de rendez-vous, ni annulation, ni modification, ni création. **Les titres et noms ne sont jamais affichés** ici (confidentialité : même règle que l'écran de porte, `RoomTitles` = false par défaut). Message permanent : « Lecture seule : les rendez-vous vivent dans vos agendas (Microsoft, Google…) et se modifient là-bas. » L'onglet peut être **partagé en lecture seule** à l'accueil (partage par page, §117) sans ouvrir la Configuration. Les vues Semaine / Liste, l'origine des réservations (écran, QR) et leur annulation sont **repoussées** à une version ultérieure (voir §10).
- **Configuration** : agendas (gérer les salles de chaque source, **« + Ajouter un agenda »**), règles par défaut (bouton **Configurer**, règle 4), **test de réservation** (v1 §5 phase 4), indicateur de santé (« 4 salles réservables · ⚠ lecture seule : Salle de conférence »).
- **Infos** : version, plans (tous), agendas utilisés, type d'écran ajouté.

**Mode simple** : Contenus + Configuration réduite (agendas, durées) ; l'onglet Réservations reste (l'accueil en a besoin) ; options avancées (tampon, horaires par jour, équipements) en expert.

---

## 5. Parcours 3 — le type d'écran « Écran de salle » (maquette vue 4)

**Modèle à copier : la Borne interactive** (vérifié dans `Components/Pages/Screens.razor`) : entrée dans le menu **« + Ajouter »** (l.37-43), onglet de filtre par type (`kindFilter` : tous / diffusion / bornes, l.1002), badge de type sur la carte (l.2051-2052), tiroir de configuration dédié (`KioskDrawer`), workflow ScreenEditor (copie de travail, rien d'écrit avant Enregistrer).

1. **Menu Ajouter** : entrée **« Écran de salle »** (icône porte, badge « Nouveau »). **Sans l'application** : entrée **visible mais grisée**, badge « Application requise », texte « Installez Réservation de salle pour créer des écrans de salle » et bouton « Ouvrir le Store » (l'utilisateur découvre la fonction). Aucun écran de ce type ne peut être créé sans l'application. (Décision §10-3 : visible grisée, ou masquée.)
2. **Flux complet** : ① application installée (§3) ; ② **le player est déjà installé sur la tablette** (menu Écrans › « Installer le player », Android) et affiche un code à 6 chiffres ; ③ **assistant « Nouvel écran de salle »** (ci-dessous). L'assistant rappelle, à sa première étape, « Pas encore de player ? Installer le player ».
   **Assistant 4 étapes** (même fenêtre que l'assistant d'écran ; **pas d'étape « appareil »**) :
   - **Code** : saisie du code d'appairage à 6 chiffres (champ large, bouton « Connecter la tablette »). « Continuer » reste désactivé tant que la tablette n'est pas appairée. À l'appairage, le serveur reconnaît l'appareil (Android · orientation · **tactile ou non**) et l'affiche : « Tablette détectée : Android · paysage · tactile ». Flux d'appairage existant (§100-101 : `/api/player/…`, ré-appairage) à réutiliser tel quel.
   - **Salle** : liste des salles de l'application (badge « Sans écran » / « 1 écran »). Une salle peut avoir plusieurs écrans (porte, accueil).
   - **Affichage** : aperçu à gauche de la porte ; thème **Clair / Sombre**, boutons de durée (activé par défaut si la tablette est tactile, sinon désactivé et masqué), QR code, titre des réunions, nom de l'écran. Modèle prêt à l'emploi, **sans éditeur** (réglages avancés possibles plus tard).
   - **Terminé** : « Écran de salle créé · La tablette affiche maintenant la salle · En ligne » ; « rien à publier ».
3. **Liste des écrans** : nouvel onglet **« Salles »** (visible seulement s'il en existe, comme « Bornes »). **Carte allégée** : bandeau de couleur de l'état (vert Libre, orange Bientôt, rouge Occupé) à la place de l'aperçu de diffusion, nom de l'écran, pastille En ligne / Hors ligne, appareil.
4. **Contenu de l'écran = un modèle intégré à l'application** (la porte), pas une composition à publier. **Proposition technique** : le flux de l'écran (`PlayerFeed`) fournit à ce type d'écran une composition implicite « porte de salle » construite à partir de l'élément `room` existant (`Compositions.RoomType`, `composition.js` : `roomState`, `roomHtml`) et des options de l'assistant ; l'écran suit donc la salle et ses règles **sans publication**. À vérifier §11-1.
5. **Gestion des états de l'application** (maquette vue 5) :
   | Si… | Alors… |
   |---|---|
   | Application désinstallée | Écrans de salle en gris « Application retirée », réservations arrêtées ; agendas et leurs applications conservés ; confirmation « N écrans de salle seront désactivés ». |
   | Salle Actif = off | Écran : « Salle indisponible ». |
   | Application d'agenda utilisée par des salles | Désinstallation refusée : « 3 salles utilisent Google Agenda ». |
   | Droit de réserver retiré | Salles en « Affichage seul », alerte (Centre de notifications), boutons de durée masqués. |
   | Écran hors ligne | Pastilles rouges (Écrans, Contenus) ; tablette : boutons grisés « Hors ligne ». |
   | Lien ICS | Réservation « Linkivi seulement », annoncée clairement. |
6. **Plans** : tous. Point à vérifier : un écran de salle compte-t-il dans la limite d'écrans du plan ? (aucune limite trouvée en recherchant `MaxScreens`/`ScreenLimit` ; vérifier le code des licences, §11-4). Recommandation : il compte comme un écran.

---

## 6. Moteur de réservation (rappel et ajustements par rapport à la v1)

Reprendre **v1 §4, §6, §7, §8** (écriture dans l'agenda quand c'est possible, réservations locales pour ICS, serveur arbitre copiant `HaCommand`, QR à jeton tournant, marquage des événements, 409 en cas de conflit). Ajustements :
- **Plus de Pro** : toutes les fonctions du moteur sont disponibles dans tous les plans. Seules les **statistiques d'occupation** restent à décider (§10-8).
- Le mode d'écriture (« Afficher seulement » / « Afficher et réserver ») est choisi **à l'installation, par source** (étape 4), pas dans chaque calendrier.
- L'onglet Réservations (§4) lit : événements des agendas + journal des réservations Linkivi (`RoomBooking`, v1 §9).
- Les anciennes références de la v1 à « Salle » dans la fenêtre du calendrier deviennent « Configuration de l'application ».
- **Aucune modification de rendez-vous depuis l'interface Linkivi** (§1-9). Conséquences : pas d'annulation dans l'onglet Réservations ; la v1 prévoyait aussi « + 15 min » et « Terminer » sur la tablette : **hors V1 par défaut** (à confirmer §10-11). Reste à confirmer que **la création** d'un rendez-vous depuis la tablette (le besoin d'origine : « réserver 15, 30, 45 min, 1 h, 2 h ») est conservée : c'est le seul cas où Linkivi écrit dans un agenda ; sans lui, l'application est en affichage seul.
- **Découpage possible en deux niveaux de livraison** : (a) *affichage seul* (lots 1, 2, 5 : aucune écriture, aucun nouveau droit OAuth, faible risque) ; (b) *réservation depuis la tablette* (lots 3, 4 : droits d'écriture).

---

## 7. Données — delta par rapport à v1 §9

- **Application** : drapeau installée + règles par défaut (durées, max, horaires, prolongation, QR, titres) au niveau organisation.
- **Salle** : réutiliser `SharedCalendar` (`Room*`) ; `Actif` correspond vraisemblablement à l'inverse de `SharedCalendar.Off` déjà lu par `RoomContents` (`c.Room && !c.Off`) — **à confirmer** ; ajouter les surcharges par salle des règles (nullable = valeur par défaut) ; idée : `RoomOutOfService`, `RoomEquipment`.
- **Écran** : un type « salle » à côté de `Screen.Kiosk` (précédent : `Screen.Kiosk != null`), avec options : thème, boutons de durée (tactile), QR, titres ; `Screen.RoomCalendarId` renseigné par l'assistant.
- Le reste (`RoomBooking`, droits d'écriture par source, `RoomPublicCode`) : voir v1 §9.

---

## 8. Découpage en lots (ordre révisé : la valeur arrive avant l'écriture)

| Lot | Contenu | Critère d'acceptation |
|---|---|---|
| **0 — Valider** | Maquette `salle-app-parcours.html` + décisions §10. | Réponses de l'utilisateur consignées. |
| **1 — Application + installation** | Entrée Store, assistant 6 étapes **sans écriture** (étape 4 : seulement « Afficher » pour l'instant), import des salles dans `SharedCalendar`, page `/apps/{id}` : Contenus (état en direct, Actif), Configuration, Infos ; interrupteur « Salle » des assistants d'agenda masqué. | Installer depuis le Store avec un vrai Google et un vrai Microsoft ; salles visibles dans Contenus avec leur état réel ; désinstaller proprement. |
| **2 — Type d'écran « Écran de salle »** | Entrée du menu Ajouter (grisée sans l'application), assistant 4 étapes **Code → Salle → Affichage → Terminé** (appairage en première étape), onglet « Salles », carte allégée, porte intégrée (affichage seul) sans publication. | Tablette réelle avec player déjà installé : saisie du code, la porte affiche Libre/Occupé et les prochains rendez-vous ; sans application : impossible de créer. |
| **3 — Écriture Microsoft/Google** | = v1 lot 1 + étape 4 de l'assistant active (Autoriser), test de réservation. | Événement créé / prolongé / terminé dans un vrai agenda ; erreur claire si droit manquant. |
| **4 — Réservation sur l'écran** | = v1 lot 3 : boutons de durée, création de l'événement, 409 en cas de conflit, `Nudge`, journal. **Sans « + 15 min » ni « Terminer »** (hors V1). | Tablette réelle : 30 min → agenda mis à jour et écran Occupé en < 3 s. |
| **5 — Onglet Réservations (statut seul)** | Frise « Aujourd'hui » en **lecture seule** + section « Maintenant » (libre / occupée de … à …), filtre par salle, partage lecture seule. **Aucune action.** Peut être livré dès le lot 1 (n'exige aucune écriture). | Les horaires correspondent à l'agenda ; aucun bouton d'action ni titre de rendez-vous ; accueil en lecture seule sans accès à la Configuration. |
| **6 — Page QR** | = v1 lot 4. | Ancien QR refusé. |
| **7 — ICS local, libération, CalDAV, Exchange** | = v1 lots 5-6. | Essais réels. |
| **8 — Enrichissements** | Équipements, Hors service, tableau d'accueil, salle libre proche, stats (voir §9). | Selon décision. |

Chaque lot : `dotnet build` sans erreur, tests `Linkii.Poc.Tests`, vérification dans le navigateur, puis `DEV-POC.md` (§209+) en disant honnêtement ce qui n'a pas été essayé sur appareil ou serveur réel.

---

## 9. Idées d'enrichissement (proposées, à valider ; voir maquette vue 5)

1. **Équipements** (visio, projecteur, tableau…) en icônes sur la porte ; filtre « salle avec visio ».
2. **Mode « Hors service »** : second interrupteur dans Contenus ; la porte affiche « Indisponible » sans toucher à l'agenda.
3. **Tableau d'accueil** : type d'écran « Tableau des salles » (même application) listant toutes les salles : libre maintenant, libre jusqu'à… (l'élément `board` existe déjà).
4. **Salle libre la plus proche** sur la porte d'une salle occupée.
5. **Thème de la porte** (clair/sombre, logo de l'organisation, langue, taille de police) dans l'assistant.
6. **Statistiques d'occupation** par salle et par jour.
7. **Alerte** « salle occupée sans présence » (lot ultérieur, si capteur Home Assistant : lien possible avec les capteurs de présence).

---

## 10. Décisions ouvertes

1. Valider la maquette `salle-app-parcours.html` (structure de la page, assistants, onglets).
2. Option C (Contenus + onglet Réservations) : confirmée ? *(recommandé : oui)*
3. « Écran de salle » **visible grisé** sans l'application (recommandé) ou masqué ?
4. Un écran de salle **compte** dans la limite d'écrans du plan ? *(recommandé : oui)*
5. Un **même écran** peut-il afficher **plusieurs salles** (tableau) ? *(recommandé : tableau = autre type, plus tard)*
6. Que devient l'interrupteur « Salle » des assistants d'agenda (§147) ? *(recommandé : masqué, remplacé par « Gérée par Réservation de salle »)*
7. Réservation « plus tard » : sur l'écran, ou seulement par QR ? *(recommandé : maintenant sur l'écran, plus tard par QR)*
11. **« + 15 min » et « Terminer » sur la tablette** : hors V1 (recommandé, car Linkivi ne modifie pas les rendez-vous) ? La **création** depuis la tablette reste-t-elle prévue (lots 3-4) ?
12. Onglet Réservations : plus tard, ajouter la vue Semaine, l'origine des réservations et l'annulation des seules réservations créées par la tablette ? *(à décider après usage ; hors V1)*
8. Statistiques d'occupation : tous les plans ou Pro ?
9. Libération automatique si personne n'arrive : lot 7 ? *(recommandé)*
10. Lien avec le plan du bâtiment Home Assistant (§195-208) : hors périmètre ?

---

## 11. À vérifier avant ou pendant l'implémentation

1. **Contenu des écrans de salle sans publication** : comment `PlayerFeed` fournit une composition implicite (lire `Compositions.Publish`, `PlayerFeed`, `Notifier`) ; sinon, fabriquer une vraie composition à la création de l'écran.
2. **Tactile** : le player Android transmet-il les touches à `composition.js` (hors mode borne) ?
3. **Déclaration d'une nouvelle application avec sa page** : voir `Apps/AppMenu.cs` (applications virtuelles `cal-*`, groupes), `AppService.Install`, `AppDetail.razor` (`isVirtual`), `AppCatalog` ; vérifier comment ajouter un onglet propre à une application sans casser les autres.
4. **Limites de licence sur le nombre d'écrans** (rien trouvé par recherche rapide).
5. **Détection des salles** : annuaire des salles Microsoft (le texte d'aide de la source `m365` le mentionne ; vérifier l'implémentation) ; ressources Google Workspace (agendas de ressource).
6. **Droits d'écriture réels** (Exchange salle, ressource Google, vérification de l'application OAuth par Google) : voir v1 §5 phase 3 et §11.
7. **Mode simple** : où masquer les réglages avancés (§174).
8. Les modifications **non commitées** de `AppDetail.razor`, `AppMenu.cs`, `AppService.cs`, `WizardShell.razor` (voir §0) interagissent probablement avec ce chantier.

---

## 12. Fichiers du dépôt concernés

`Linkii.Poc/Apps/AppMenu.cs`, `AppService.cs`, `SharedCalendars.cs`, `CalendarConnectors.cs`, `MicrosoftAuth.cs`, `GoogleAuth.cs`, `ConnectedAccounts.cs` · `Components/Pages/AppDetail.razor`, `Screens.razor`, `Components/CalendarWizard.razor`, `CalendarSourceDialog.razor`, `WizardShell.razor`, `ScreenSettingsDialog.razor`, `KioskDrawer.razor` (modèle du tiroir d'écran dédié) · `Models.cs` (`SharedCalendar`, `Screen`), `Compositions.cs` (`RoomType`, `Publish`), `Notifier.cs`, `wwwroot/player/composition.js` · `Program.cs` (`HaCommand` l.1013, `/q/{code}` l.572, `/api/player/…` l.763+) · `DEV-POC.md` §146-147, §162, §168-170, §174, §206.
