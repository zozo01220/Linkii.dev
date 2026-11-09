# Linkivi pour Home Assistant

Voir et commander ses écrans Linkivi depuis Home Assistant : redémarrer le player, redémarrer l'appareil, éteindre l'appareil, éteindre l'écran et le réveiller.
Chaque écran devient un **appareil** Home Assistant. À ne pas confondre avec l'intégration Home Assistant **de** Linkivi (lire et commander les appareils d'une maison sur les écrans) : ici, c'est l'inverse.

Prérequis : Home Assistant 2024.11 ou plus récent, organisation Linkivi au **plan Pro** (l'API et les commandes d'appareil en font partie).

## Installer

1. **Dans Linkivi** : Réglages › Développeurs › Nouvelle clé, modèle **Home Assistant** (accès « Compte » et « Écrans », dont « Commander les écrans »). Copiez la clé `lkv_…`, elle ne s'affiche qu'une fois.
   - Une clé créée par un administrateur commande tous les écrans. Une clé créée par un membre suit les droits de sa fiche : « Éteindre l'écran », « Redémarrer l'appareil » et « Éteindre l'appareil », sur les écrans qui lui sont attribués.
2. **Dans Home Assistant** : copiez le dossier `custom_components/linkivi` dans le dossier `config/custom_components/` de Home Assistant, puis redémarrez Home Assistant.
3. Paramètres › Appareils et services › Ajouter une intégration › **Linkivi** : adresse du serveur (par exemple `https://linkivi.com`) et clé d'API.

Les écrans ajoutés plus tard dans Linkivi apparaissent seuls (relevé toutes les 30 secondes).

## Ce que chaque écran offre

| Entité | Type | Rôle |
|---|---|---|
| **En ligne** | capteur binaire | L'écran a donné de ses nouvelles ces 90 dernières secondes. Attributs : état, dernier contact, aire, zone, liste, résolution. |
| **Écran** | interrupteur | Allumé = éveillé. Éteindre = veille manuelle jusqu'au réveil. Allumer = réveil d'**une heure**, puis les plages de veille et les calendriers reprennent la main (comme « Réveiller » dans Linkivi). Marche aussi quand l'écran est hors ligne : la consigne est lue à son retour. |
| **Redémarrer le player** | bouton | Relance l'application d'affichage. |
| **Redémarrer l'appareil** | bouton | Redémarre le PC, le Raspberry Pi ou l'appareil Android. |
| **Éteindre l'appareil** | bouton | Éteint l'appareil. **Linkivi ne peut pas le rallumer** : il faut le bouton ou le courant. |

Un bouton n'existe que si le matériel sait faire la commande :

| Appareil | Player | Écran | Redémarrer | Éteindre |
|---|---|---|---|---|
| Windows, Raspberry Pi | oui | oui | oui | oui |
| Android, Android TV | oui | oui | seulement si l'app est propriétaire de l'appareil (`dpm set-device-owner`) | jamais |
| Navigateur | oui | oui | non | non |

Un écran de **mur** se commande en entier : un ordre sur un écran du mur part sur tous.

## Exemples

```yaml
# Éteindre les écrans de l'accueil quand le dernier part, les rallumer le matin
automation:
  - alias: Écrans de l'accueil éteints le soir
    trigger:
      - platform: state
        entity_id: binary_sensor.presence_batiment
        to: "off"
        for: "00:15:00"
    action:
      - action: switch.turn_off
        target:
          entity_id: switch.accueil_ecran

  - alias: Redémarrer le player chaque nuit
    trigger:
      - platform: time
        at: "03:30:00"
    action:
      - action: button.press
        target:
          entity_id: button.accueil_redemarrer_le_player
```

Si Linkivi refuse (écran hors ligne, commande déjà en cours, appareil non compatible), Home Assistant affiche la raison donnée par Linkivi.

## Sous le capot

L'intégration n'appelle que l'API publique de Linkivi (voir `/docs/api` sur votre serveur) :

| Appel | Accès | Usage |
|---|---|---|
| `GET /api/v1/me` | `account:read` | Nom de l'organisation, au moment de l'ajout. |
| `GET /api/v1/screens` | `screens:read` | État des écrans, toutes les 30 s. Chaque écran liste dans `commands` ce que son matériel sait faire. |
| `POST /api/v1/screens/{id}/commands` | `screens:control` | `{"command": "restart-player" \| "reboot" \| "poweroff" \| "screen-off" \| "wake"}`, réponse `202`. |

Sans Home Assistant, ces appels servent aussi depuis un script (`curl -H "Authorization: Bearer lkv_…"`).

## Pas encore essayé

Écrit d'après l'API et la documentation de Home Assistant, **jamais lancé sur une vraie instance** : le code Python n'a même pas pu être exécuté (pas de Python sur le poste de développement). L'API de Linkivi, elle, est couverte par des tests (`ScreenCommandsApiTests`).
