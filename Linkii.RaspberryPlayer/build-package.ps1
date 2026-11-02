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
(Get-Content .\install.sh -Raw) -replace "`r`n", "`n" | Set-Content "$out\install.sh" -NoNewline -Encoding utf8
tar -czf "$PSScriptRoot\artifacts\linkivi-pi-$version.tar.gz" -C "$PSScriptRoot\artifacts" "linkivi-pi-$version"
Write-Host "artifacts\linkivi-pi-$version.tar.gz"
