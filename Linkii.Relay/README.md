# Relais Linkii

Petit service à installer sur un PC ou un serveur **du réseau de l'organisation**. Il lit pour Linkii les bases de données (SQL Server,
PostgreSQL, MySQL / MariaDB) et les API ou fichiers qui ne sont pas accessibles depuis internet. Il **appelle** le serveur Linkii en HTTPS
(connexion sortante) : rien à ouvrir dans le pare-feu. Il ne fait que lire (requêtes SELECT, appels GET) ; le seul POST qu'il accepte est l'appel de connexion d'une API (utilisateur et mot de passe, qui rend un jeton), à partir de la version 1.2.0.

## Installation (Windows)

1. Dans Linkii : **Intégrations › Sources de données › Relais › Installer un relais**. Téléchargez l'installateur Windows et notez le code
   (8 caractères, valable une heure).
2. Double-cliquez sur `LinkiiRelay-Setup.exe`, acceptez l'autorisation administrateur, saisissez le code.

L'installateur fait tout : appairage, copie dans `C:\Program Files\Linkii Relay`, service Windows `LinkiiRelay` (démarrage automatique,
redémarré en cas de panne), entrée dans « Applications et fonctionnalités », démarrage. Rien à créer à la main.
Windows peut afficher un avertissement SmartScreen (fichier non signé) : « Informations complémentaires » puis « Exécuter quand même ».

Relancé sur un poste déjà installé, il propose de réinstaller (ou ré-appairer avec un nouveau code) ou de désinstaller.

Désinstaller : Applications et fonctionnalités › Relais Linkii, ou relancer l'installateur et choisir « Désinstaller ». Supprimez aussi le
relais dans Linkii. Si la ligne de commande est préférée :

```
LinkiiRelay-Setup.exe install /code=ABCD-2345 [/server=https://votre-linkii] [/dir=D:\Linkii] [/silent]
LinkiiRelay-Setup.exe uninstall [/yes]
```

Pour lire SQL Server avec l'authentification Windows, le service doit tourner sous un compte qui a le droit de lecture sur la base
(`sc.exe config LinkiiRelay obj= DOMAINE\compte password= …`, ce compte doit avoir le droit « Ouvrir une session en tant que service »),
puis `sc.exe stop LinkiiRelay` et `sc.exe start LinkiiRelay`.

## Installation (Linux)

```
sudo mkdir -p /opt/linkii-relay && sudo unzip LinkiiRelay-linux-x64.zip -d /opt/linkii-relay
sudo useradd --system linkii-relay && sudo chown -R linkii-relay /opt/linkii-relay
sudo -u linkii-relay /opt/linkii-relay/Linkii.Relay pair CODE --server https://votre-linkii
sudo cp /opt/linkii-relay/linkii-relay.service /etc/systemd/system/ && sudo systemctl enable --now linkii-relay
```

## Configuration

`relay.json`, dans le dossier d'installation : serveur, jeton (propre à ce relais, lisible seulement par le système et les administrateurs
sous Windows), nom, et `allowedHosts` : laissé vide, le relais lit tout serveur que Linkii lui demande ; sinon seulement ceux de la liste
(`"sql-salles.local"`, `"10.0.2.*"`, `"*.interne.ch"`). `Linkii.Relay status` affiche la configuration. Supprimé dans Linkii, le relais voit
son jeton refusé et ne fait plus rien.

## Publication

`publish.ps1` produit `LinkiiRelay-Setup.exe` (le relais lui-même, un seul exécutable autonome Windows, qui sert d'installateur) et
`LinkiiRelay-linux-x64.zip`, dans `Linkii.Poc/downloads`, servis par `/download/relay/windows` et `/download/relay/linux-x64`.
Le serveur ajoute à la fin de l'exécutable téléchargé l'adresse du serveur (`#LINKII-SERVER=…`, sans effet sur le programme) : l'installateur ne
la demande donc pas. Un fichier pris directement dans le dossier `downloads` la demandera.
