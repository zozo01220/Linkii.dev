# Linkivi et l'intelligence artificielle : vue d'ensemble

Dernière mise à jour : 10 novembre 2026. Le détail chronologique est dans `DEV-POC.md` (§225). Maquette validée : `maquettes/ia-pistes.html`.

Principe : l'IA propose, la personne décide. Rien n'est remplacé, publié ou exécuté sans un clic, et chaque aide reste optionnelle. L'IA n'est jamais dans le player : seulement dans le back-office.

## Fonctionnement commun

- **Fournisseur** : Anthropic, SDK officiel NuGet `Anthropic` 13.8.0, modèle par défaut `claude-haiku-5-5` (réglable dans la console). Réflexion coupée selon le modèle (`Ai.ThinkingFor` : Haiku `disabled`, Sonnet 5.5 `between_tools`, Opus 5.5 et Fable rien à envoyer), effort faible. Adresse de l'API remplaçable par `Linkii:Ai:BaseUrl` (passerelle, serveur de test).
- **Une seule clé pour toute la plateforme**, saisie une fois dans la console (Admin › Réglages › Intelligence artificielle), chiffrée avec `SecretBox`. Un interrupteur d'urgence coupe tout.
- **Plans** : réservé à Pro et au-dessus. Essential voit les boutons avec la pastille Pro et une invitation. Essai gratuit : IA active, même quota que Pro (réglable, interrupteur dans la console).
- **Quotas** : 1 000 demandes par mois et par organisation (Pro et essai), quota particulier par organisation dans sa fiche (Admin › Clients) pour Enterprise. Au-delà, l'IA se met en pause jusqu'au 1er du mois, le reste de Linkivi continue. Plafond de dépense global de la plateforme : 25 $ par mois. Cadence : 12 demandes par minute et par organisation.
- **Suivi** : demandes, jetons et coût estimé par organisation et par mois (`Db.AiUsage`). Page Admin › Clients : colonne « IA ce mois-ci », orange dès 70 %, rouge dès 90 %.
- **Réglages du client** (Création › Intelligence artificielle) : quatre interrupteurs (aide à l'écriture, relecture, médiathèque intelligente, assistant) et la jauge de consommation.
- **Code** : `Ai.cs` (`AiService` : état, quota, appel, comptage), une classe par fonction (ci-dessous).

## Les cinq fonctions

| Fonction | Où | Code | Ce qu'elle fait |
|---|---|---|---|
| **1. Aide à l'écriture** | Bouton « ✦ Améliorer » sous les textes (bandeaux, messages défilants, champs de texte des applications, barre d'un texte de composition) | `Components/AiImprove.razor`, `AiService.Rewrite`, `compo-editor.js` | Corrige et resserre en un clic, puis « Plus court », « Plus chaleureux », « Plus formel », « Traduire… » (en, de, it, es). La proposition s'affiche dans le panneau de l'assistant (le champ est cerclé de bleu tant qu'il y est relié). « Remplacer » et « Revenir à l'ancien ». Les champs `{{…}}` des compositions sont conservés. |
| **2. Relecture avant publication** | Page d'une liste de diffusion | `AiReview.cs`, `PlaylistEditor.razor` | Une demande par relecture (textes et images réduites) : dates passées, textes de modèle oubliés, fautes ; textes trop petits repérés sans IA. Bandeau, pastille sur « Publier » ; les remarques s'ouvrent dans le panneau de l'assistant (« Voir dans l'assistant », ou « Publier » avec des remarques : Ouvrir, Ne pas publier, Ignorer, « Publier quand même »). Résultat gardé en mémoire tant que rien ne change. |
| **3. Médiathèque intelligente** | Médiathèque, fiche d'une image | `AiMedia.cs`, `Media.razor`, `MediaPreviewPanel.razor` | À l'import, en arrière-plan : description, mots-clés, texte lu sur l'image, texte de modèle oublié (champs `MediaItem.Ai*`). Recherche par ce que montrent les images, avec synonymes. « Analyser mes images » pour l'existant. Description et mots-clés modifiables. |
| **4. Créer des compositions** | Compositions › Ajouter › « Créer avec l'IA » ; Premiers pas › « Mon site web » | `AiCompose.cs`, `CompositionsPage.razor`, `WelcomeWizard.razor` | Une phrase donne une composition (paysage ou portrait) ; l'adresse du site donne trois compositions (accueil, présentation, infos pratiques) et importe la photo du site. L'IA dessine des textes et des formes et peut placer une image d'illustration : une image de la médiathèque qui parle du sujet, sinon une photo neutre d'internet (Pexels, sans personne, importée dans la médiathèque avec le crédit du photographe : `WebImages.cs`). Le serveur contrôle positions, couleurs, polices, tailles. « Créer avec l'IA » s'ouvre dans le panneau de l'assistant. |
| **5. Assistant** | Panneau ancré à droite : bouton « Assistant » de la barre du haut ou Ctrl + J | `AiAssistant.cs`, `AiPanel.cs`, `Components/AssistantDock.razor`, `wwwroot/aipanel.js` | Conversation gardée (on enchaîne). Répond d'après l'état de l'organisation (écrans, listes et leurs contenus, compositions, images, calendriers, historique récent) et propose des actions cochées une à une, avec « Annuler » quand elles peuvent se défaire : écrans (message, pause, reprise, renommer, liste par défaut, variables, bandeaux, redémarrer, éteindre l'écran, réveiller), listes en brouillon (créer, renommer, dupliquer, ajouter, retirer, déplacer, durées, transitions, activer, relecture), images (renommer, décrire, rechercher), compositions (créer depuis une phrase, modifier tout ce qu'on veut dans une composition, renommer, dupliquer, remplacer un texte, corriger ou reformuler les textes d'une composition ou d'une liste), calendriers (créer, événement, assigner), délai d'alerte. Enchaîne dans une seule réponse ce qu'une demande implique : « programme la compo X de 9 h à 11 h sur l'écran Y » donne créer la liste (ou reprendre une liste qui convient), y mettre la compo, créer le calendrier, ajouter le créneau, l'assigner à l'écran. Compositions avec une image (choisie, de la médiathèque ou d'internet) et recherche d'une photo neutre sur internet pour la médiathèque. Pendant la demande, le panneau montre où on en est (étapes, réponse reçue au fil de l'eau, temps, « Arrêter »), puis l'action en cours et un bilan. Ne publie pas, ne supprime rien. |

## Le panneau de l'assistant (§230)

Toute l'IA qu'on demande passe par un seul endroit : le panneau ancré à droite (maquette `maquettes/ia-assistant-panneau.html`).

- **Ancré, jamais par-dessus** : ouvert, il pousse la page (`body.ai-docked`). Sur ordinateur, voiles, tiroirs de droite et fenêtres plein écran s'arrêtent à son bord, les fenêtres centrées se recentrent ; il reste utilisable pendant qu'une fenêtre est ouverte. Petit écran : plein écran (l'app mobile sera revue plus tard).
- **Ouvert d'une page à l'autre** : service `AssistantPanel` (portée de la session), composant `AssistantDock` dans `MainLayout`. Fermé au tout premier lancement ; ouvert/fermé, largeur (340 à 560 px, bord gauche à glisser) et discussion en cours retenus par le navigateur (`aipanel.js`, `linkiiPrefs`).
- **La page ouverte** : pastille « Sur cette page » ; l'IA reçoit le nom et l'adresse de la page, sauf si on retire la pastille.
- **Cartes des pages** : une page pose sa carte avec `Panel.Show(clé, demande, origine, contenu, résumé)` et la retire avec `Panel.Release(clé)` (au plus tard en se fermant). Le contenu reste celui de la page (ses boutons appellent la page) ; la page appelle `Panel.Refresh()` quand il change, `Panel.Save()` quand le résumé change. Il reste le résumé dans la discussion. Cartes actuelles : Améliorer (`AiImprove`, éditeur de compositions), relecture (`PlaylistEditor`), Créer avec l'IA (`CompositionsPage`). **Toute nouvelle fonction IA d'une page passe par une carte**, pas par une fenêtre.
- **Discussions gardées** (`Db.AiChats`, règles dans `AiChats`) : par personne et par organisation, visibles par leur auteur seul (ni l'administrateur ni les autres membres) ; effacées 30 jours après le dernier échange, au plus 50, les épinglées gardées ; titre fait tout seul, renommable. « Discussions récentes » (horloge) : rubriques, recherche, épingler, renommer, effacer, tout effacer. Une discussion reprise se continue : l'IA reçoit les anciens échanges en résumé et les 6 derniers en entier ; « Annuler » n'est plus proposé pour les anciennes actions.
- **Hors du panneau** : la description automatique des images (rien n'est demandé), « Décrire cette image » de la médiathèque, la composition depuis un site des Premiers pas, et les Premiers pas avec l'assistant (leur discussion est quand même rangée dans les discussions gardées).

## Premiers pas avec l'assistant (§232)

L'étape 1 des Premiers pas devient une zone de l'IA quand l'organisation y a droit : une adresse de site ou une phrase, puis l'assistant prépare tout en direct (maquette `maquettes/premiers-pas-assistant-ia.html`).

- **Code** : `AiStart.cs` (`AiStarter` : lecture du site, demande à l'IA, logo, photos, pages, météo, liste, retouches, discussion gardée), `StartTemplates.cs` (contenu `StartBrief` et modèles de pages), `Components/AiStartView.razor` (discussion, écran en direct, « C'est prêt »), `wwwroot/start-ai.js`, hébergé par `WelcomeWizard.razor`.
- **L'IA écrit, Linkivi met en page** : une demande rend le contenu (textes, couleurs, police, choix du logo et des photos, en voyant les images réduites du site) ; les pages viennent de modèles Linkivi, donc toujours propres. Les retouches (plus chaleureux, plus court, demande libre, anglais) modifient ce contenu, puis les pages sont redessinées ; portrait et logo sans IA ; chaque retouche s'annule.
- **Aperçu sur téléphone** : QR vers le player en mode aperçu, lien de 24 h gardé dans la base (`PreviewGrants.Share`), fichiers servis avec `?pv=jeton`.
- **Coût** : 1 demande pour préparer, 1 de plus pour regarder les photos d'internet (seulement s'il y en a à choisir), 1 par retouche de l'IA. « Autres photos », « Autre mise en page », « Ajouter mes photos », portrait et logo ne coûtent rien.

### Des écrans adaptés à l'activité (§248)

Demande du 13 novembre 2026 : l'assistant faisait toujours les mêmes écrans, et un kebab se retrouvait illustré par une barque.

- **Jamais d'image hors sujet** : le repli sur les paysages d'exemple de Linkivi est retiré. Une page sans photo qui convienne garde un décor aux couleurs de l'activité, et l'assistant demande les photos de la personne.
- **Une photo par page** (`StartBrief.PagePhotos`) : dans sa réponse, l'IA dit pour chaque page ce que la photo doit montrer, en français (nom dans la médiathèque) et en anglais (recherche) : champ `images`, lu par `AiStarter.Queries`. Les photos d'un site passent d'abord ; la banque d'images complète les pages qui restent.
- **L'IA regarde avant de garder** (`AiStarter.FindPhotos`, `Rank`) : cinq candidates par page, vignettes réduites à 384 px, une seule demande pour toutes les pages. Elle rend les numéros qui conviennent, la meilleure d'abord ; une liste vide laisse la page sans photo. Si cette demande échoue, l'ordre de la banque est gardé.
- **Pastilles** : « Autres photos » prend la candidate suivante de chaque page (`ImportNext`), « Autre mise en page » retire au sort variantes et décor (`StartTemplates.Shuffle`), « Ajouter mes photos » pose jusqu'à six fichiers de la personne sur les pages. Chacune s'annule.
- **Mises en page** : trois variantes pour Accueil et Services, deux pour les autres (`StartBrief.Layouts`, `StartTemplates.Variants`), tirées au sort à la préparation. Cinq décors pour les fonds sans photo (`StartBrief.Decor` : cercles, bandes, arches, points, cadre), choisis par l'IA selon le ton.
- **Pages selon l'activité** : en plus d'Accueil, Services et Merci (toujours là), l'IA garde jusqu'à quatre pages facultatives parmi liste avec prix (`prices`), offre du moment (`offer`), infos pratiques (`info`), chiffres clés (`numbers`), avis (`quote`) et actualité (`news`). Noms génériques, sans vocabulaire de métier.
- **Ajouter une variante ou un type de page** : une fonction de dessin dans `StartTemplates` (paysage et portrait), son entrée dans `Kinds`, `Has`, `Name`, `Variants`, le contrôle dans `AiStarter.Fix`, la forme JSON et la règle dans `BriefSystem` / `Rules`, et `Diff` dans `AiStartView.razor`. Le test « Toutes les mises en page et tous les décors tiennent dans l'écran » couvre la nouvelle page sans autre réglage.

## Listes interactives des bornes par l'assistant (§249)

L'assistant crée et modifie les listes interactives (ce qu'un visiteur touche sur une borne), avec tout ce que fait leur éditeur : pages, tuiles, textes, images, boutons de navigation, mise en forme, action de chaque tuile, QR code, dates d'affichage, format.

- **Opérations** : `ilist_create`, `ilist_edit`, `ilist_rename`, `ilist_duplicate`, `kiosk_set_ilist` (donner la liste à une borne). Repère `T1`, `NT1` pour une liste créée dans la réponse.
- **Deux demandes à l'IA** : l'assistant propose l'action ; après confirmation, `AiInteractive.cs` fait construire la liste entière. L'IA ne voit que des repères (P pages, E éléments, I images, O contenus).
- **Fusion et contrôle** : `AiInteractive.Merge` garde ce que l'IA ne renvoie pas, puis `InteractiveLists.Apply` contrôle le tout comme un enregistrement de l'éditeur. « Annuler » remet l'ancienne liste.
- **Limites voulues** : pas de suppression d'une liste entière, pas de transformation d'un écran en borne, pas de publication.
- **Toute nouvelle fonction de l'éditeur de liste interactive** s'ajoute à la consigne `AiInteractive.System` et à la ligne `ilist_edit` de la consigne de `AiAssistant`.

## Garde-fous

- Pas de clé en clair, jamais dans le navigateur. Les images envoyées sont réduites (1 024 px, JPEG).
- L'assistant ne voit que des repères courts (S1, L1, C1, M1, K1) : un repère inconnu est ignoré. Une liste ou un calendrier qu'il crée dans la réponse reçoit un repère neuf (`ref` : NL1, NK1 ; le numéro suivant, L5 après L1 à L4, marche aussi) et un identifiant tiré dès la proposition, pour que les actions suivantes du même lot le visent ; si sa création est décochée, elles échouent avec un message clair. Il passe par `TenantStore` et les services de l'application (service à portée de la session) : mêmes droits, périmètre d'aires et écrans que les pages. Commandes limitées à `restart-player`, `screen-off`, `wake`, `reboot` ; jamais `poweroff`, jamais de suppression ni de publication. Les corrections de texte gardent les champs `{{…}}` (un texte dont un champ change est laissé tel quel).
- Les compositions créées par l'IA passent par `Compositions.Clean` comme toutes les autres.
- Composition modifiée par l'assistant (§231) : la réponse de l'IA est fusionnée par `AiCompose.Merge` (un élément gardé garde ses autres champs, blocs reliés aux données jamais créés, images seulement par repère connu, réponse vide refusée) ; droits de la personne (« Peut utiliser » : refusé, à dupliquer) ; « Annuler » remet l'ancien document ; l'éditeur ouvert reprend la nouvelle version, l'ancienne reste dans Ctrl+Z.
- Images d'internet : une clé Pexels de la plateforme (console), coupable par organisation ; seuls les mots qui décrivent l'image partent chez Pexels ; photos dont la description parle de personnes écartées ; téléchargement depuis `images.pexels.com` seulement, sans redirection, 15 Mo au plus ; crédit du photographe gardé sur l'image (`MediaItem.Credit`).
- Premiers pas (§248) : rien d'inventé. Un prix inconnu s'écrit « [Prix] » ; une offre, un chiffre ou une actualité n'existent que s'ils sont annoncés ; un avis n'est gardé que s'il vient d'un site (`Sanitize` le retire pour une simple phrase). Longueurs, nombre de lignes et nombre de pages facultatives sont bornés par `Sanitize`. Les vignettes montrées à l'IA viennent du même hôte contrôlé (`WebImages.Thumb`, 2 Mo au plus).
- Résultat de la relecture et analyses des images : une demande par image ou par relecture, jamais refaite sans changement.

## État de vérification

- **Essayé avec un faux serveur Anthropic** (hors dépôt) : saisie de la clé, test de connexion, Améliorer, quota atteint, colonne par client, relecture, éditeur de compositions, analyse et recherche d'images, création de compositions, Premiers pas depuis un site (example.com), assistant (conversation gardée, 11 sortes d'actions confirmées, annulation, refus d'un écran hors ligne).
- **Jamais essayé avec la vraie API** : réflexion désactivée + effort faible acceptés par Haiku 5.5, authentification, qualité des descriptions, des mises en page et des propositions de l'assistant, enchaînement complet d'une programmation (§227 : testé seulement par `AiAssistantTests`, sans IA), réponse en flux de l'assistant (§228), choix d'une image par l'IA et vraie banque Pexels (§229 : essayés avec un faux serveur seulement), panneau de l'assistant et reprise d'une ancienne discussion (§230 : faux serveur seulement ; jamais avec plusieurs membres de la même organisation).
- **Écrans adaptés à l'activité (§248)** : vérifiés par `AiStartTests` seulement (28 tests : rien ne sort de la page, deux textes ne se recouvrent pas, pour toutes les variantes, décors, polices et formats). Aucun rendu regardé à l'écran ; jamais lancé avec la vraie IA ni une vraie clé Pexels ; pertinence des photos et pastilles non essayées.
- **Jamais essayé** : bouton sous les champs des applications (AppForm), publication depuis la page Écrans (non interceptée par la relecture), vraies images, photo du site importée, action de l'assistant sur un écran en ligne.
- **Listes interactives (§249)** : création essayée avec un faux serveur IA et confirmée par l'utilisateur ; modification, copie, attribution à une borne et rechargement de l'éditeur jamais essayés dans le navigateur.

## Pas fait

- Recadrage portrait automatique des images (le centre n'est ni calculé ni utilisé : les images des listes passent par l'application « image » du player).
- Choisir soi-même parmi plusieurs photos d'internet (grille de résultats), autres banques d'images.
- Alerte à 80 % du plafond de dépense, publication par l'assistant, `show_list`, changement d'aire ou de zone d'un écran, déplacer une image dans un dossier, grille de veille et luminosité, rotation d'image par l'assistant.
- La maquette dit encore 20 demandes pour l'essai (le code : 1 000).
