# Linkivi pour Raspberry Pi OS 64 bits : paquet linkivi-pi-<version>.tar.gz (programme autonome arm64 + install.sh)
#   .\build-package.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$version = ([xml](Get-Content .\Linkii.RaspberryPlayer.csproj)).Project.PropertyGroup.Version
$out = Join-Path $PSScriptRoot "artifacts\linkivi-pi-$version"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish -c Release -r linux-arm64 --self-contained -p:PublishSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { throw 'publication échouée' }
Remove-Item "$out\*.pdb" -ErrorAction SilentlyContinue
# fins de ligne Unix pour le script
$sh = (Get-Content .\install.sh -Raw) -replace "`r`n", "`n"
[IO.File]::WriteAllText("$out\install.sh", $sh, (New-Object Text.UTF8Encoding $false))   # UTF-8 sans BOM : sinon « #!/bin/bash » n'est pas reconnu
tar -czf "$PSScriptRoot\artifacts\linkivi-pi-$version.tar.gz" -C "$PSScriptRoot\artifacts" "linkivi-pi-$version"
# proposé au téléchargement par le back-office (/download/linux)
$downloads = Join-Path $PSScriptRoot '..\Linkii.Poc\downloads'
New-Item -ItemType Directory -Force $downloads | Out-Null
Get-ChildItem "$downloads\linkivi-pi-*.tar.gz" -ErrorAction SilentlyContinue | Remove-Item -Force
Copy-Item "$PSScriptRoot\artifacts\linkivi-pi-$version.tar.gz" $downloads
Write-Host "artifacts\linkivi-pi-$version.tar.gz"
