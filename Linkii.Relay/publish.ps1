# Publie le relais Linkii et dépose les fichiers dans Linkii.Poc/downloads :
#  - LinkiiRelay-Setup.exe : l'installateur Windows (le relais lui-même, un seul exécutable autonome), servi par /download/relay/windows ;
#  - LinkiiRelay-linux-x64.zip : le relais Linux avec son service systemd, servi par /download/relay/linux-x64.
# Sur le serveur : copier ces deux fichiers dans le dossier downloads des données.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here '..\Linkii.Poc\downloads'
New-Item -ItemType Directory -Force $out | Out-Null
foreach ($rid in 'win-x64', 'linux-x64') {
    $dir = Join-Path $here "artifacts\$rid"
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    dotnet publish (Join-Path $here 'Linkii.Relay.csproj') -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $dir
    if ($rid -eq 'win-x64') {
        $setup = Join-Path $out 'LinkiiRelay-Setup.exe'
        Copy-Item (Join-Path $dir 'Linkii.Relay.exe') $setup -Force
        Write-Host "Installateur : $setup"
        $old = Join-Path $out 'LinkiiRelay-win-x64.zip'
        if (Test-Path $old) { Remove-Item -Force $old }   # ancienne archive Windows, remplacée par l'installateur
    } else {
        Copy-Item (Join-Path $here 'linkii-relay.service') $dir
        Copy-Item (Join-Path $here 'README.md') $dir
        $zip = Join-Path $out 'LinkiiRelay-linux-x64.zip'
        if (Test-Path $zip) { Remove-Item -Force $zip }
        Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $zip
        Write-Host "Archive : $zip"
    }
}
