# Compile le player et fabrique l'installeur : artifacts\LinkiiPlayer-<version>-x64.msi
# Usage : .\build-installer.ps1 [-Version 0.1.0]
param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

# autonome (le poste n'a pas besoin d'installer .NET) ; WebView2 vient avec Edge sur Windows 11 et Windows 10 à jour
dotnet publish "$root\Linkii.WindowsPlayer.csproj" -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publish
if ($LASTEXITCODE) { throw 'publication échouée' }

dotnet build "$root\installer\Installer.wixproj" -c Release -p:PublishDir="$publish/" -p:ProductVersion=$Version -o "$root\artifacts"
if ($LASTEXITCODE) { throw "installeur échoué" }
Get-ChildItem "$root\artifacts\*.msi" | ForEach-Object { "{0}  ({1:N1} Mo)" -f $_.FullName, ($_.Length / 1MB) }
