# Relais Linkii

Petit service à installer sur un PC ou un serveur **du réseau de l'organisation**. Il lit pour Linkii les bases de données (SQL Server,
PostgreSQL, MySQL / MariaDB) et les API ou fichiers qui ne sont pas accessibles depuis internet. Il **appelle** le serveur Linkii en HTTPS
(connexion sortante) : rien à ouvrir dans le pare-feu. Il ne fait que lire (requêtes SELECT, appels GET).

## Installation (Windows)

1. Décompressez `LinkiiRelay-win-x64.zip` dans `C:\Linkii\Relay`.
2. Dans Linkii : **Intégrations › Sources de données › Relais › Installer un relais**, copiez le code.
3. Dans une invite de commandes **administrateur** :

```
cd C:\Linkii\Relay
Linkii.Relay.exe pair CODE --server https://votre-linkii
sc.exe create LinkiiRelay binPath= "C:\Linkii\Relay\Linkii.Relay.exe" start= auto
sc.exe start LinkiiRelay
```

Pour lire SQL Server avec l'authentification Windows, faites tourner le service sous un compte qui a le droit de lecture sur la base
(`sc.exe config LinkiiRelay obj= DOMAINE\compte password= …`).

## Installation (Linux)

```
sudo mkdir -p /opt/linkii-relay && sudo unzip LinkiiRelay-linux-x64.zip -d /opt/linkii-relay
sudo useradd --system linkii-relay && sudo chown -R linkii-relay /opt/linkii-relay
sudo -u linkii-relay /opt/linkii-relay/Linkii.Relay pair CODE --server https://votre-linkii
sudo cp /opt/linkii-relay/linkii-relay.service /etc/systemd/system/ && sudo systemctl enable --now linkii-relay
```

## Configuration

`relay.json`, à côté du programme : serveur, jeton (propre à ce relais), nom, et `allowedHosts` : laissé vide, le relais lit tout serveur que
Linkii lui demande ; sinon seulement ceux de la liste (`"sql-salles.local"`, `"10.0.2.*"`, `"*.interne.ch"`). `Linkii.Relay status` affiche la
configuration. Supprimé dans Linkii, le relais voit son jeton refusé et ne fait plus rien.

## Publication

`publish.ps1` : exécutables autonomes Windows et Linux, archives copiées dans `Linkii.Poc/downloads`.
