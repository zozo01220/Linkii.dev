# Copie le dernier APK du player (app\build\outputs\apk\debug\Linkivi-<version>-debug.apk) dans le dossier downloads du serveur Linux.
# Le serveur le sert ensuite sur /download/android (dernier Linkivi-*.apk du dossier).
# Usage : .\deploy-apk.ps1                         (serveur et dossier par défaut)
#         .\deploy-apk.ps1 -Server utilisateur@hote -RemoteDir /chemin/downloads
# Prérequis : client OpenSSH de Windows (ssh et scp), droit d'écriture dans le dossier du serveur.
param(
    [string]$Server = 'root@192.168.1.249',
    [string]$RemoteDir = '/var/lib/linkii/downloads'
)
$ErrorActionPreference = 'Stop'
$apk = Get-ChildItem (Join-Path $PSScriptRoot 'app\build\outputs\apk\debug\Linkivi-*-debug.apk') -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) { throw "Aucun APK : lancez d'abord gradlew :app:assembleDebug" }

$RemoteDir = $RemoteDir.TrimEnd('/')
"APK     : {0} ({1:N1} Mo)" -f $apk.Name, ($apk.Length / 1MB)
"Serveur : ${Server}:$RemoteDir"

# dépôt sous un nom temporaire, puis renommage : /download/android ne sert jamais un fichier à moitié copié
ssh $Server "mkdir -p '$RemoteDir'"
if ($LASTEXITCODE) { throw 'connexion SSH échouée' }
scp $apk.FullName "${Server}:$RemoteDir/$($apk.Name).part"
if ($LASTEXITCODE) { throw 'copie échouée' }

# la dernière version remplace les précédentes
ssh $Server "cd '$RemoteDir' && rm -f Linkivi-*.apk && mv '$($apk.Name).part' '$($apk.Name)' && ls -l Linkivi-*.apk"
if ($LASTEXITCODE) { throw 'mise en place échouée' }
"Terminé : https://linkivi.duckdns.org/download/android"
