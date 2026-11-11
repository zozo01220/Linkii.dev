# Linkivi et l'intelligence artificielle : vue d'ensemble

Dernière mise à jour : 10 novembre 2026. Le détail chronologique est dans `DEV-POC.md` (§225). Maquette validée : `maquettes/ia-pistes.html`.

Principe : l'IA propose, la personne décide. Rien n'est remplacé, publié ou exécuté sans un clic, et chaque aide reste optionnelle. L'IA n'est jamais dans le player : seulement dans le back-office.

## Fonctionnement commun

- **Fournisseur** : Anthropic, SDK officiel NuGet `Anthropic` 13.8.0, modèle par défaut `claude-haiku-5-5` (réglable dans la console). Réflexion désactivée, effort faible. Adresse de l'API remplaçable par `Linkii:Ai:BaseUrl` (passerelle, serveur de test).
- **Une seule clé pour toute la plateforme**, saisie une fois dans la console (Admin › Réglages › Intelligence artificielle), chiffrée avec `SecretBox`. Un interrupteur d'urgence coupe tout.
- **Plans** : réservé à Pro et au-dessus. Essential voit les boutons avec la pastille Pro et une invitation. Essai gratuit : IA active, même quota que Pro (réglable, interrupteur dans la console).
- **Quotas** : 1 000 demandes par mois et par organisation (Pro et essai), quota particulier par organisation dans sa fiche (Admin › Clients) pour Enterprise. Au-delà, l'IA se met en pause jusqu'au 1er du mois, le reste de Linkivi continue. Plafond de dépense global de la plateforme : 25 $ par mois. Cadence : 12 demandes par minute et par organisation.
- **Suivi** : demandes, jetons et coût estimé par organisation et par mois (`Db.AiUsage`). Page Admin › Clients : colonne « IA ce mois-ci », orange dès 70 %, rouge dès 90 %.
- **Réglages du client** (Création › Intelligence artificielle) : quatre interrupteurs (aide à l'écriture, relecture, médiathèque intelligente, assistant) et la jauge de consommation.
- **Code** : `Ai.cs` (`AiService` : état, quota, appel, comptage), une classe par fonction (ci-dessous).

## Les cinq fonctions

| Fonction | Où | Code | Ce qu'elle fait |
|---|---|---|---|
| **1. Aide à l'écriture** | Bouton « ✦ Améliorer » sous les textes (bandeaux, messages défilants, champs de texte des applications, barre d'un texte de composition) | `Components/AiImprove.razor`, `AiService.Rewrite`, `compo-editor.js` | Corrige et resserre en un clic, puis « Plus court », « Plus chaleureux », « Plus formel », « Traduire… » (en, de, it, es). « Remplacer » et « Revenir à l'ancien ». Les champs `{{…}}` des compositions sont conservés. |
| **2. Relecture avant publication** | Page d'une liste de diffusion | `AiReview.cs`, `PlaylistEditor.razor` | Une demande par relecture (textes et images réduites) : dates passées, textes de modèle oubliés, fautes ; textes trop petits repérés sans IA. Bandeau, pastille sur « Publier », fenêtre « Avant de publier » (Ouvrir, Ne pas publier, Ignorer). Résultat gardé en mémoire tant que rien ne change. |
| **3. Médiathèque intelligente** | Médiathèque, fiche d'une image | `AiMedia.cs`, `Media.razor`, `MediaPreviewPanel.razor` | À l'import, en arrière-plan : description, mots-clés, texte lu sur l'image, texte de modèle oublié (champs `MediaItem.Ai*`). Recherche par ce que montrent les images, avec synonymes. « Analyser mes images » pour l'existant. Description et mots-clés modifiables. |
| **4. Créer des compositions** | Compositions › Ajouter › « Créer avec l'IA » ; Premiers pas › « Mon site web » | `AiCompose.cs`, `CompositionsPage.razor`, `WelcomeWizard.razor` | Une phrase donne une composition (paysage ou portrait) ; l'adresse du site donne trois compositions (accueil, présentation, infos pratiques) et importe la photo du site. L'IA ne dessine que des textes et des formes ; le serveur contrôle positions, couleurs, polices, tailles. |
| **5. Assistant** | Bouton « Assistant » de la barre du haut | `AiAssistant.cs`, `Components/AiAssistantDialog.razor` | Conversation gardée (on enchaîne). Répond d'après l'état de l'organisation (écrans, listes et leurs contenus, compositions, images, calendriers, historique récent) et propose des actions cochées une à une, avec « Annuler » quand elles peuvent se défaire : écrans (message, pause, reprise, renommer, liste par défaut, variables, bandeaux, redémarrer, éteindre l'écran, réveiller), listes en brouillon (créer, renommer, dupliquer, ajouter, retirer, déplacer, durées, transitions, activer, relecture), images (renommer, décrire, rechercher), compositions (créer depuis une phrase, remplacer un texte, corriger ou reformuler les textes d'une composition ou d'une liste), calendriers (créer, événement, assigner), délai d'alerte. Ne publie pas, ne supprime rien. |

## Garde-fous

- Pas de clé en clair, jamais dans le navigateur. Les images envoyées sont réduites (1 024 px, JPEG).
- L'assistant ne voit que des repères courts (S1, L1, C1, M1, K1) : un repère inconnu est ignoré. Il passe par `TenantStore` et les services de l'application (service à portée de la session) : mêmes droits, périmètre d'aires et écrans que les pages. Commandes limitées à `restart-player`, `screen-off`, `wake`, `reboot` ; jamais `poweroff`, jamais de suppression ni de publication. Les corrections de texte gardent les champs `{{…}}` (un texte dont un champ change est laissé tel quel).
- Les compositions créées par l'IA passent par `Compositions.Clean` comme toutes les autres.
- Résultat de la relecture et analyses des images : une demande par image ou par relecture, jamais refaite sans changement.

## État de vérification

- **Essayé avec un faux serveur Anthropic** (hors dépôt) : saisie de la clé, test de connexion, Améliorer, quota atteint, colonne par client, relecture, éditeur de compositions, analyse et recherche d'images, création de compositions, Premiers pas depuis un site (example.com), assistant (conversation gardée, 11 sortes d'actions confirmées, annulation, refus d'un écran hors ligne).
- **Jamais essayé avec la vraie API** : réflexion désactivée + effort faible acceptés par Haiku 5.5, authentification, qualité des descriptions, des mises en page et des propositions de l'assistant.
- **Jamais essayé** : bouton sous les champs des applications (AppForm), publication depuis la page Écrans (non interceptée par la relecture), vraies images, photo du site importée, action de l'assistant sur un écran en ligne.

## Pas fait

- Recadrage portrait automatique des images (le centre n'est ni calculé ni utilisé : les images des listes passent par l'application « image » du player).
- Alerte à 80 % du plafond de dépense, publication par l'assistant, `show_list`, changement d'aire ou de zone d'un écran, déplacer une image dans un dossier, grille de veille et luminosité, rotation d'image par l'assistant.
- La maquette dit encore 20 demandes pour l'essai (le code : 1 000).
