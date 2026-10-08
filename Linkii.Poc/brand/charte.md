# Linkii — Charte graphique (option B · Corporate)

> Validée le 6 octobre 2026. Ton : rigoureux, institutionnel, sobre.

## Logo
- Wordmark minuscule `linkii`, IBM Plex Sans 500, interlettrage -0,03 em.
- Les points des deux « i » sont **carrés** ; le point du second « i » est en **Signal**.
- Icône d'app : les deux « i » blancs sur carré Nuit (rayon 4 px).
- Fond clair : wordmark Nuit. Fond sombre : wordmark Blanc, point Signal inchangé.

## Couleurs

| Rôle | Nom | Hex | Usage |
|---|---|---|---|
| Encre | Nuit | `#0B1F3A` | Texte principal, bouton principal, fond du player |
| Secondaire | Acier | `#5A6B80` | Texte secondaire, libellés |
| Bordure | Brume | `#DDE3EA` | Bordures, séparateurs |
| Fond | Gris clair | `#F5F7FA` | Fond de page du back-office |
| Surface | Blanc | `#FFFFFF` | Cartes, tableaux, champs |
| Accent | Signal | `#3B82C4` | Point du logo, sélection, focus, liens — jamais en aplat large |

### États des écrans

| État | Point | Fond pastille | Texte pastille |
|---|---|---|---|
| En ligne | `#1E8E5A` | `#E1F0E8` | `#155A38` |
| Mise à jour | `#B7791F` | `#FBF0DC` | `#7A4F12` |
| Hors ligne | `#C8372D` | `#FBE4E2` | `#8A231C` |

### Player (fond Nuit)
- Bordures des cases du code : `#23395A` ; case active : Signal.
- Texte secondaire : `#9AAABD`.

## Typographie
- **IBM Plex Sans** (400 / 500) pour toute l'interface. Jamais de 600/700.
- **JetBrains Mono** 500 pour le code d'appairage et les données chiffrées.
- Échelle : titre 28 · sous-titre 20 · corps 15 · libellé 13 · légende 12 (px).
- Casse de phrase partout (« Ajouter un écran », pas « Ajouter Un Écran »).

## Composants
- Rayon : **4 px** (boutons, pastilles, cartes) ; 3 px pour les cases du code.
- Bordures 1 px Brume, aucune ombre, aucun dégradé.
- Bouton principal : fond Nuit, texte Blanc — un seul par vue.
- Bouton secondaire : fond Blanc, bordure `#C5CED9`, texte Nuit.
- Focus : anneau 2 px Signal.

## Icônes d'applications (exception colorée)
Ajouté le 7 octobre 2026, à la demande : chaque app a une **pastille ronde de couleur** avec un pictogramme blanc (traits 2 px). C'est la seule exception à la règle « pas d'aplat de couleur » : elle aide à reconnaître les apps d'un coup d'œil.

| App | Couleur | Pictogramme |
|---|---|---|
| Texte / annonce | `#4F6BED` | Aa |
| Flux RSS | `#F2541B` | ondes RSS |
| Calendrier partagé | `#2FA66A` | calendrier |
| Sources de calendriers (Microsoft 365, Google, CalDAV, ICS, Exchange) | `#2FA66A` | carrés, G, serveur, lien, enveloppe |
| Dossier Drive · sources Google Drive, OneDrive | `#0F6CBD` | dossier · G, nuage |
| Horloge | `#0B1F3A` (Nuit) | horloge |
| Météo | `#3D63F2` | soleil |
| Page web | `#3A9399` | globe |
| Image · Diaporama images | `#F0A92E` | image · pile d'images |
| Vidéo · Diaporama vidéos | `#7B5CC4` | lecture · lecture encadrée |
| YouTube | `#E3261D` | lecture |
| Canva | `#00A3B4` | palette |
| Autre / inconnue | `#5A6B80` (Acier) | emoji du manifeste |

Source : `Components/AppIcon.razor`.
