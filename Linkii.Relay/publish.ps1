# Publie le relais Linkii (Windows et Linux, autonome, un seul exécutable) et dépose les archives dans Linkii.Poc/downloads,
# servies par /download/relay/win-x64 et /download/relay/linux-x64. Sur le serveur : copier les deux zip dans le dossier downloads des données.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here '..\Linkii.Poc\downloads'
New-Item -ItemType Directory -Force $out | Out-Null
foreach ($rid in 'win-x64', 'linux-x64') {
    $dir = Join-Path $here "artifacts\$rid"
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    dotnet publish (Join-Path $here 'Linkii.Relay.csproj') -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $dir
    if ($rid -eq 'linux-x64') { Copy-Item (Join-Path $here 'linkii-relay.service') $dir }
    Copy-Item (Join-Path $here 'README.md') $dir
    $zip = Join-Path $out "LinkiiRelay-$rid.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $zip
    Write-Host "Archive : $zip"
}
