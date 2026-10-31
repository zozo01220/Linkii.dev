# Copie le dernier installeur Windows (artifacts\Linkivi-<version>-x64.msi) sur le serveur Linux.
# Le serveur le sert ensuite sur /download/windows (dernier Linkivi-*.msi du dossier « downloads » des données).
# Usage : .\deploy-installer.ps1 -Server utilisateur@linkivi.duckdns.org -RemoteDir /chemin/des/donnees/downloads
#   -RemoteDir : le dossier « downloads » du dossier de données de Linkii (celui qui contient data.json).
# Prérequis : client OpenSSH de Windows (ssh et scp), accès SSH au serveur avec droit d'écriture dans ce dossier.
param(
    [Parameter(Mandatory)][string]$Server,
    [Parameter(Mandatory)][string]$RemoteDir,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$msi = if ($Version) { Get-Item (Join-Path $artifacts "Linkivi-$Version-x64.msi") }
       else { Get-ChildItem "$artifacts\Linkivi-*-x64.msi" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
if (-not $msi) { throw "Aucun installeur dans $artifacts : lancez d'abord build-installer.ps1" }

$RemoteDir = $RemoteDir.TrimEnd('/')
"Installeur : {0} ({1:N1} Mo)" -f $msi.Name, ($msi.Length / 1MB)
"Serveur    : ${Server}:$RemoteDir"

# dépôt sous un nom temporaire, puis renommage : /download/windows ne sert jamais un fichier à moitié copié
ssh $Server "mkdir -p '$RemoteDir'"
if ($LASTEXITCODE) { throw 'connexion SSH échouée' }
scp $msi.FullName "${Server}:$RemoteDir/$($msi.Name).part"
if ($LASTEXITCODE) { throw 'copie échouée' }

# la dernière version remplace les précédentes (y compris l'ancien nom LinkiiPlayer-*.msi)
ssh $Server "cd '$RemoteDir' && rm -f Linkivi-*.msi LinkiiPlayer-*.msi && mv '$($msi.Name).part' '$($msi.Name)' && ls -l Linkivi-*.msi"
if ($LASTEXITCODE) { throw 'mise en place échouée' }
"Terminé : https://linkivi.duckdns.org/download/windows"
