# Prompt de reprise — à coller dans l'autre compte Claude

> Les fichiers cités sont dans le dépôt `C:\DEV\En-cours\Linkii` (dossiers `docs/reservation-salle/` et `maquettes/`). Au 9 nov. 2026 ils ne sont **pas commités** : l'autre compte doit travailler sur ce même dossier, ou ils doivent d'abord être commités et poussés.

---

## Prompt à coller

```
Tu reprends l'implémentation de l'application « Réservation de salle » de Linkii (dépôt C:\DEV\En-cours\Linkii, .NET / Blazor, projet serveur Linkii.Poc). Une analyse complète a déjà été faite par un autre compte Claude. Ne la refais pas : lis-la et implémente.

LECTURE OBLIGATOIRE, dans cet ordre, avant toute action :
1. docs/reservation-salle/ANALYSE-v2.md : le produit, le parcours, les lots, les décisions. Le §0 (règles du projet) et le §1 (décisions de l'utilisateur, dont les corrections 7 à 9 qui priment sur tout) sont impératifs.
2. maquettes/salle-app-parcours.html : la maquette interactive validée comme référence visuelle (ouvre-la dans le navigateur ; 5 vues : Store, installation, page de l'application, Écrans, vue d'ensemble). Respecte-la : l'utilisateur exige que l'interface suive la maquette.
3. docs/reservation-salle/ANALYSE.md (v1) : seulement pour le moteur technique (§2 existant, §4 où écrire la réservation, §6 règles métier, §7 architecture, §8 sécurité, §9 données, §11 vérifications). Ses recommandations « Pro » sont annulées : tous les plans.
4. DEV-POC.md §146, §147, §206 (et §137, §168-170 pour la Borne interactive, qui est le modèle du type d'écran).

RÉSUMÉ DU PRODUIT (ne remplace pas la lecture) :
- Flux utilisateur : (1) installer l'application « Réservation de salle » depuis le Store ; l'assistant d'installation en 6 étapes importe les agendas des applications d'agenda (Google, Microsoft, ICS, CalDAV, Exchange) et crée les salles ; (2) installer le player sur la tablette ; (3) dans Écrans › Ajouter › « Écran de salle », assistant 4 étapes : Code d'appairage (première étape) → Salle → Affichage → Terminé. Pas d'étape « appareil ».
- Le type d'écran « Écran de salle » n'existe que si l'application est installée (entrée visible mais grisée sinon). Tous les plans.
- Page /apps/{id} : Contenus (les salles, état en direct, interrupteur Actif), onglet « Réservations » propre à l'application, Configuration, Infos.
- Linkivi ne modifie JAMAIS un rendez-vous (ils sont dans les comptes Microsoft, Google…). L'onglet Réservations n'affiche que le statut des salles (libre ou occupée, de quelle heure à quelle heure), en lecture seule, sans titres, sans action.

PÉRIMÈTRE DE CETTE REPRISE (niveau « affichage seul », sans aucune écriture dans les agendas) :
- Lot 1 : application + assistant d'installation + page /apps/{id} (Contenus, Configuration, Infos).
- Lot 2 : type d'écran « Écran de salle » + assistant 4 étapes + onglet « Salles » + carte allégée + porte intégrée sans publication.
- Lot 5 : onglet Réservations (statut seul, lecture seule).
NE FAIS PAS les lots 3 et 4 (écriture Microsoft/Google, réservation depuis la tablette) ni le reste, sauf si l'utilisateur le demande ensuite. Dans l'assistant d'installation, l'étape « Réservation » s'affiche mais seul « Afficher seulement » est actif ; « Afficher et réserver » est présenté grisé « Bientôt ».

RÈGLES DE TRAVAIL (de l'utilisateur, impératives) :
- Respecte les 7 règles du §0 (une application = une entrée du groupe « Applications » ; Contenus en liste, sans bouton « Nouveau contenu », interrupteur Actif ; « Configurer » ; etc.), des interrupteurs jamais des cases à cocher, un vocabulaire générique, un mode simple réduit.
- L'utilisateur est très sensible à la consommation et aux allers-retours : ne teste rien et n'ouvre rien sans nécessité, ne pose pas de questions qui ont une recommandation dans le §10 (applique la recommandation et note-la dans DEV-POC.md). Pose une question courte seulement si c'est vraiment ambigu.
- Avant de coder : lis `git status` et `git diff` : l'arbre contient des modifications NON commitées d'autres chantiers (AppDetail.razor +200 lignes, AppMenu.cs, AppService.cs, MainLayout.razor, WizardShell.razor, Models.cs, app.css, Ico.razor, assistants Drive/Calendrier/Canva, DriveFolderTree.razor…). Comprends-les, ne les écrase pas, ne stage que les fichiers de ce chantier, ne commite et ne pousse que si l'utilisateur le demande.
- Résous d'abord les « À vérifier » du §11 de l'ANALYSE-v2 qui concernent ces lots (§11-1 contenu de l'écran sans publication, §11-3 déclaration d'une application avec sa page et un onglet propre, §11-4 limites d'écrans) et signale en une ligne ce que tu as trouvé.
- Termine chaque lot par `dotnet build` sans erreur, les tests Linkii.Poc.Tests, puis une section numérotée dans DEV-POC.md (§209 et suivants) qui dit honnêtement ce qui n'a PAS été essayé (vrai Google / Microsoft, vraie tablette Android). Ne dis jamais « vérifié » pour ce qui ne l'est pas.
- Si tu dois t'écarter de la maquette ou de l'analyse, dis-le avant, en une phrase.

Commence par me dire en cinq lignes ce que tu as compris et par quoi tu commences, puis exécute le lot 1.
```

---

## Ordre conseillé pour l'utilisateur

1. Valider la maquette `maquettes/salle-app-parcours.html` (surtout : assistant d'installation, onglet Réservations en lecture seule, assistant d'écran « Code d'abord »).
2. Répondre (ou laisser appliquer les recommandations) aux décisions du §10 de l'`ANALYSE-v2.md`, en particulier les n° 6 (interrupteur « Salle » des assistants d'agenda), 11 (« + 15 / Terminer » hors V1 ; création depuis la tablette conservée) et 4 (un écran de salle compte comme un écran).
3. Coller le prompt ci-dessus dans l'autre compte, dans le même dossier de projet.
4. Après le lot 5 : décider d'engager les lots 3-4 (écriture Microsoft / Google : le plus risqué, à essayer sur de vrais comptes).
