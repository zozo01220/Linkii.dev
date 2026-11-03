# Outil de déploiement des players Linkivi : augmente la version, compile, puis copie les installeurs sur le serveur Linux.
#   Windows  : Linkivi-<version>-x64.msi   → /download/windows
#   Android  : Linkivi-<version>-debug.apk → /download/android
#   Raspberry: linkivi-pi-<version>.tar.gz → /download/linux
#
# Usage :
#   .\deploy-players.ps1                              tous les players, version corrective +1 (0.3.1 → 0.3.2)
#   .\deploy-players.ps1 -Targets windows,pi          seulement ceux-là
#   .\deploy-players.ps1 -Bump minor                  0.3.1 → 0.4.0   (patch | minor | major | none : garder la version)
#   .\deploy-players.ps1 -WindowsVersion 0.4.0        imposer la version d'un player (-AndroidVersion, -PiVersion)
#   .\deploy-players.ps1 -BuildOnly                   compiler sans copier sur le serveur
#   .\deploy-players.ps1 -DeployOnly                  copier les derniers installeurs déjà compilés
#   .\deploy-players.ps1 -DryRun                      montrer ce qui serait fait, sans rien modifier
#   .\deploy-players.ps1 -Server utilisateur@hote -RemoteDir /chemin/downloads
#
# Prérequis : client OpenSSH de Windows (ssh et scp) avec accès au serveur ; .NET 8 ; WiX (installeur Windows, via dotnet) ;
# Android Studio (son JDK sert à compiler l'APK), tar (Windows 10 l'a) pour le paquet Raspberry.
param(
    [ValidateSet('windows', 'android', 'pi')][string[]]$Targets = @('windows', 'android', 'pi'),
    [ValidateSet('patch', 'minor', 'major', 'none')][string]$Bump = 'patch',
    [string]$WindowsVersion,
    [string]$AndroidVersion,
    [string]$PiVersion,
    [string]$Server = 'root@192.168.1.249',
    [string]$RemoteDir = '/var/lib/linkii/downloads',
    [switch]$BuildOnly,
    [switch]$DeployOnly,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$utf8 = New-Object Text.UTF8Encoding $false
$RemoteDir = $RemoteDir.TrimEnd('/')

function Read-Text($path) { [IO.File]::ReadAllText($path) }
function Write-Text($path, $text) {   # le BOM d'origine est gardé (PowerShell 5.1 en a besoin pour lire les accents d'un .ps1)
    $bom = $false
    if (Test-Path $path) { $b = [IO.File]::ReadAllBytes($path); $bom = $b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF }
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $bom))
}

function Next-Version([string]$current, [string]$kind) {
    $v = [version]$current
    switch ($kind) {
        'major' { return '{0}.0.0' -f ($v.Major + 1) }
        'minor' { return '{0}.{1}.0' -f $v.Major, ($v.Minor + 1) }
        'patch' { return '{0}.{1}.{2}' -f $v.Major, $v.Minor, ([Math]::Max(0, $v.Build) + 1) }
        default { return $current }
    }
}

function Replace-Once($text, $pattern, $replacement) {
    $re = New-Object Text.RegularExpressions.Regex($pattern)
    if (-not $re.IsMatch($text)) { throw "Motif introuvable : $pattern" }
    $re.Replace($text, $replacement, 1)
}

# ---- Copie sur le serveur : nom temporaire, puis renommage (le téléchargement ne sert jamais un fichier à moitié copié) ----
function Send-File($file, [string]$removePatterns) {
    "  copie de {0} ({1:N1} Mo) vers ${Server}:$RemoteDir" -f $file.Name, ($file.Length / 1MB)
    if ($DryRun) { return }
    ssh $Server "mkdir -p '$RemoteDir'"
    if ($LASTEXITCODE) { throw 'connexion SSH échouée' }
    scp $file.FullName "${Server}:$RemoteDir/$($file.Name).part"
    if ($LASTEXITCODE) { throw 'copie échouée' }
    # la dernière version remplace les précédentes du même player
    ssh $Server "cd '$RemoteDir' && rm -f $removePatterns && mv '$($file.Name).part' '$($file.Name)' && ls -l '$($file.Name)'"
    if ($LASTEXITCODE) { throw 'mise en place échouée' }
}

# ---- Android : la compilation demande un JDK 17 ou plus ; celui d'Android Studio convient ----
function Use-Jdk17 {
    $ok = $false
    try { $out = (& java -version 2>&1 | Out-String); if ($out -match 'version "(\d+)') { $ok = [int]$Matches[1] -ge 17 } } catch { }
    if ($ok) { return }
    foreach ($c in @("$env:ProgramFiles\Android\Android Studio\jbr", "${env:ProgramFiles(x86)}\Android\Android Studio\jbr", "$env:LOCALAPPDATA\Programs\Android Studio\jbr")) {
        if (Test-Path "$c\bin\java.exe") { $env:JAVA_HOME = $c; "  JDK : $c"; return }
    }
    throw "Aucun JDK 17 : installez Android Studio ou définissez JAVA_HOME."
}

$results = @()
foreach ($target in $Targets) {
    ''
    "=== $target ==="
    $backup = @{}   # fichiers modifiés : restaurés si la compilation échoue
    try {
        switch ($target) {
            'windows' {
                $csproj = Join-Path $root 'Linkii.WindowsPlayer\Linkii.WindowsPlayer.csproj'
                $build = Join-Path $root 'Linkii.WindowsPlayer\build-installer.ps1'
                $art = Join-Path $root 'Linkii.WindowsPlayer\artifacts'
                $text = Read-Text $csproj
                $current = [regex]::Match($text, '<Version>([^<]+)</Version>').Groups[1].Value
                $version = if ($WindowsVersion) { $WindowsVersion } elseif ($DeployOnly) { $current } else { Next-Version $current $Bump }
                "  version : $current → $version"
                if (-not $DeployOnly -and $version -ne $current -and -not $DryRun) {
                    $backup[$csproj] = $text; $backup[$build] = Read-Text $build
                    Write-Text $csproj (Replace-Once $text '<Version>[^<]+</Version>' "<Version>$version</Version>")
                    Write-Text $build (Replace-Once $backup[$build] "(\[string\]\`$Version = ')[^']*(')" "`${1}$version`${2}")
                }
                if (-not $DeployOnly) { "  compilation (publication autonome + installeur)…"; if (-not $DryRun) { & $build -Version $version; if ($LASTEXITCODE) { throw 'compilation échouée' } } }
                if (-not $BuildOnly) {
                    $msi = Join-Path $art "Linkivi-$version-x64.msi"
                    if ($DryRun) { Send-File ([pscustomobject]@{ Name = "Linkivi-$version-x64.msi"; FullName = $msi; Length = 0 }) 'Linkivi-*.msi LinkiiPlayer-*.msi' }
                    else { if (-not (Test-Path $msi)) { throw "Installeur introuvable : $msi" }; Send-File (Get-Item $msi) 'Linkivi-*.msi LinkiiPlayer-*.msi' }
                }
            }
            'android' {
                $dir = Join-Path $root 'Linkii.AndroidPlayer'
                $gradle = Join-Path $dir 'app\build.gradle.kts'
                $text = Read-Text $gradle
                $current = [regex]::Match($text, 'versionName = "([^"]+)"').Groups[1].Value
                $code = [int][regex]::Match($text, 'versionCode = (\d+)').Groups[1].Value
                $version = if ($AndroidVersion) { $AndroidVersion } elseif ($DeployOnly) { $current } else { Next-Version $current $Bump }
                $newCode = if ($version -ne $current -and -not $DeployOnly) { $code + 1 } else { $code }
                "  version : $current → $version (code $code → $newCode)"
                if (-not $DeployOnly -and $version -ne $current -and -not $DryRun) {
                    $backup[$gradle] = $text
                    $t = Replace-Once $text 'versionName = "[^"]+"' "versionName = `"$version`""
                    $t = Replace-Once $t 'versionCode = \d+' "versionCode = $newCode"
                    $t = Replace-Once $t 'archivesName\.set\("Linkivi-[^"]+"\)' "archivesName.set(`"Linkivi-$version`")"
                    Write-Text $gradle $t
                }
                if (-not $DeployOnly) {
                    "  compilation de l'APK…"
                    if (-not $DryRun) {
                        Use-Jdk17
                        Push-Location $dir
                        try { & .\gradlew.bat :app:assembleDebug -q; if ($LASTEXITCODE) { throw 'compilation échouée' } } finally { Pop-Location }
                    }
                }
                if (-not $BuildOnly) {
                    $apk = Join-Path $dir "app\build\outputs\apk\debug\Linkivi-$version-debug.apk"
                    if ($DryRun) { Send-File ([pscustomobject]@{ Name = "Linkivi-$version-debug.apk"; FullName = $apk; Length = 0 }) 'Linkivi-*.apk' }
                    else { if (-not (Test-Path $apk)) { throw "APK introuvable : $apk" }; Send-File (Get-Item $apk) 'Linkivi-*.apk' }
                }
            }
            'pi' {
                $dir = Join-Path $root 'Linkii.RaspberryPlayer'
                $csproj = Join-Path $dir 'Linkii.RaspberryPlayer.csproj'
                $text = Read-Text $csproj
                $current = [regex]::Match($text, '<Version>([^<]+)</Version>').Groups[1].Value
                $version = if ($PiVersion) { $PiVersion } elseif ($DeployOnly) { $current } else { Next-Version $current $Bump }
                "  version : $current → $version"
                if (-not $DeployOnly -and $version -ne $current -and -not $DryRun) {
                    $backup[$csproj] = $text
                    Write-Text $csproj (Replace-Once $text '<Version>[^<]+</Version>' "<Version>$version</Version>")
                }
                if (-not $DeployOnly) { "  compilation (linux-arm64 autonome) et paquet…"; if (-not $DryRun) { & (Join-Path $dir 'build-package.ps1'); if ($LASTEXITCODE) { throw 'compilation échouée' } } }
                if (-not $BuildOnly) {
                    $pkg = Join-Path $dir "artifacts\linkivi-pi-$version.tar.gz"
                    if ($DryRun) { Send-File ([pscustomobject]@{ Name = "linkivi-pi-$version.tar.gz"; FullName = $pkg; Length = 0 }) 'linkivi-pi-*.tar.gz' }
                    else { if (-not (Test-Path $pkg)) { throw "Paquet introuvable : $pkg" }; Send-File (Get-Item $pkg) 'linkivi-pi-*.tar.gz' }
                }
            }
        }
        $results += [pscustomobject]@{ Player = $target; Version = $version; Etat = $(if ($DryRun) { 'simulé' } elseif ($BuildOnly) { 'compilé' } elseif ($DeployOnly) { 'copié' } else { 'compilé et copié' }) }
    }
    catch {
        foreach ($f in $backup.Keys) { Write-Text $f $backup[$f] }   # la version d'avant revient : rien n'a été publié
        "  ÉCHEC : $($_.Exception.Message)"
        $results += [pscustomobject]@{ Player = $target; Version = ''; Etat = "échec : $($_.Exception.Message)" }
    }
}

''
'=== Résumé ==='
$results | Format-Table -AutoSize | Out-String | Write-Host
if (-not $DryRun -and -not $BuildOnly) {
    'Téléchargements : https://linkivi.duckdns.org/download/windows  /download/android  /download/linux'
}
if ($results | Where-Object { $_.Etat -like 'échec*' }) { exit 1 }
