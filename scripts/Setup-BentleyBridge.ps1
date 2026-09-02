#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RepositoryRoot = 'C:\AITools\BentleyBridge',
    [string]$GitHubRepository = 'bogiboo/BentleyBridge',
    [string]$WorkspaceCfg = 'C:\ProgramData\Bentley\GEODEZIJA_RAZVOJ\Configuration\WorkSpaces\ETAZIRANJE_RAZVOJ.cfg'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string]$Message) {
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Require-Command([string]$Name, [string]$InstallHint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Naredba '$Name' nije pronađena. $InstallHint"
    }
}

function Write-NewTextFile([string]$Path, [string]$Content) {
    if (-not (Test-Path -LiteralPath $Path)) {
        $parent = Split-Path -Parent $Path
        if ($parent -and -not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
    }
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Pokreni PowerShell kao administrator. Skripta mora pisati u C:\ i napraviti backup WorkSpace CFG-a.'
}

Write-Step "Prvo stvaram osnovnu mapu $RepositoryRoot"
New-Item -ItemType Directory -Path $RepositoryRoot -Force | Out-Null

Require-Command git 'Instaliraj Git for Windows pa ponovno pokreni skriptu.'
Require-Command gh 'Instaliraj GitHub CLI: winget install --id GitHub.cli'

Write-Step 'Provjera GitHub prijave'
& gh auth status
if ($LASTEXITCODE -ne 0) {
    throw 'GitHub CLI nije prijavljen. Pokreni: gh auth login'
}

Write-Step "Priprema $RepositoryRoot"
$directories = @(
    (Join-Path $RepositoryRoot 'src\BRIDGE_RUN'),
    (Join-Path $RepositoryRoot 'config'),
    (Join-Path $RepositoryRoot 'schemas'),
    (Join-Path $RepositoryRoot 'examples'),
    (Join-Path $RepositoryRoot 'scripts'),
    (Join-Path $RepositoryRoot 'docs'),
    (Join-Path $RepositoryRoot 'runtime\current'),
    (Join-Path $RepositoryRoot 'runtime\inbox'),
    (Join-Path $RepositoryRoot 'runtime\results'),
    (Join-Path $RepositoryRoot 'runtime\logs'),
    (Join-Path $RepositoryRoot 'runtime\backups')
)
$directories | ForEach-Object { New-Item -ItemType Directory -Path $_ -Force | Out-Null }

$gitignore = @'
# Visual Studio / build
.vs/
bin/
obj/
*.user
*.suo

# Local Bentley runtime: DLL, tasks, results, logs and backups
runtime/current/*
runtime/inbox/*
runtime/results/*
runtime/logs/*
runtime/backups/*
!runtime/current/.gitkeep
!runtime/current/BRIDGE_RUN.dll
!runtime/current/BRIDGE_RUN.dll.sha256
!runtime/inbox/.gitkeep
!runtime/results/.gitkeep
!runtime/logs/.gitkeep
!runtime/backups/.gitkeep

# Local configuration and secrets
*.local.json
*.local.ps1
*.log
'@

$readme = @'
# BentleyBridge

Generički managed C# AddIn za OpenCities Map Ultimate 2025. Bridge se učitava u Bentleyjev proces i na ručnu naredbu `BRIDGE_RUN` čita strogo definirane JSON naloge.

## Granica sustava

`BRIDGE_RUN.dll` je pomoćni infrastrukturni alat. Nije aplikacija Etažiranje, nije njezin poslovni modul i ne smije sadržavati poslovna pravila Etažiranja. Aplikacija ostaje u zasebnom repozitoriju `bogiboo/Etaziranje`; Bridge smije primati samo generičke, provjerene Bentley operacije kroz JSON.

## Stalne lokalne putanje

- AddIn: `C:\AITools\BentleyBridge\runtime\current\BRIDGE_RUN.dll`
- Ulazni nalog: `C:\AITools\BentleyBridge\runtime\inbox\current-task.json`
- Rezultati: `C:\AITools\BentleyBridge\runtime\results\`
- Logovi: `C:\AITools\BentleyBridge\runtime\logs\`

Izvedbeni sadržaj mape `runtime` ne sprema se u Git, osim kanonskog `BRIDGE_RUN.dll` i njegova SHA-256 zapisa. Izvorni kod, JSON shema, primjeri, konfiguracija i build skripte također su verzionirani.

## Načelo rada

1. Claude ili ChatGPT pripremi izvorni kod ili JSON nalog.
2. Build skripta isporuči DLL izravno u `runtime\current`.
3. WorkSpace `ETAZIRANJE_RAZVOJ` uključuje `config\BentleyBridge.cfg`.
4. Korisnik u OpenCitiesu pokrene `BRIDGE_RUN`.
5. Bridge provjeri i izvrši samo poznatu operaciju te zapiše rezultat.

Prvi razvojni korak je Hello World bez pristupa DGN-u. Identifikator u `MS_DGNAPPS` mora se potvrditi prvim kontroliranim testom.
'@

$bridgeCfg = @'
# BentleyBridge - ukljucuje ga ETAZIRANJE_RAZVOJ.cfg
# Stalna putanja omogućuje build/deploy bez ručnog kopiranja.
MS_ADDINPATH > C:/AITools/BentleyBridge/runtime/current/

# CEKA POTVRDU PRVIM PROTOTIPOM:
# vrijednost mora odgovarati stvarnom Bentley MdlTaskID-u AddIna.
MS_DGNAPPS > BRIDGE_RUN
'@

$schema = @'
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://github.com/bogiboo/BentleyBridge/schemas/task.schema.json",
  "title": "BentleyBridge task",
  "type": "object",
  "additionalProperties": false,
  "required": ["schemaVersion", "taskId", "operation"],
  "properties": {
    "schemaVersion": { "const": 1 },
    "taskId": { "type": "string", "minLength": 1 },
    "operation": { "type": "string", "enum": ["SHOW_MESSAGE"] },
    "parameters": { "type": "object" }
  }
}
'@

$helloTask = @'
{
  "schemaVersion": 1,
  "taskId": "BEN-HELLO-001",
  "operation": "SHOW_MESSAGE",
  "parameters": {
    "message": "Hello World — BentleyBridge"
  }
}
'@

Write-NewTextFile (Join-Path $RepositoryRoot '.gitignore') $gitignore
Write-NewTextFile (Join-Path $RepositoryRoot 'README.md') $readme
Write-NewTextFile (Join-Path $RepositoryRoot 'config\BentleyBridge.cfg') $bridgeCfg
Write-NewTextFile (Join-Path $RepositoryRoot 'schemas\task.schema.json') $schema
Write-NewTextFile (Join-Path $RepositoryRoot 'examples\hello-world.task.json') $helloTask

@('current', 'inbox', 'results', 'logs', 'backups') | ForEach-Object {
    Write-NewTextFile (Join-Path $RepositoryRoot "runtime\$_\.gitkeep") ''
}
Write-NewTextFile (Join-Path $RepositoryRoot 'src\BRIDGE_RUN\.gitkeep') ''
Write-NewTextFile (Join-Path $RepositoryRoot 'docs\.gitkeep') ''

# Spremi i ovu instalacijsku skriptu unutar repozitorija.
$runningScript = $MyInvocation.MyCommand.Path
if ($runningScript) {
    $repositoryScript = Join-Path $RepositoryRoot 'scripts\Setup-BentleyBridge.ps1'
    if ([System.IO.Path]::GetFullPath($runningScript) -ne [System.IO.Path]::GetFullPath($repositoryScript)) {
        Copy-Item -LiteralPath $runningScript -Destination $repositoryScript -Force
    }
}

Write-Step 'Povezivanje WorkSpace CFG-a uz backup'
if (-not (Test-Path -LiteralPath $WorkspaceCfg -PathType Leaf)) {
    throw "WorkSpace CFG nije pronađen: $WorkspaceCfg"
}

$includeLine = '%include C:/AITools/BentleyBridge/config/BentleyBridge.cfg'
$workspaceText = [System.IO.File]::ReadAllText($WorkspaceCfg)
if ($workspaceText -notmatch [regex]::Escape($includeLine)) {
    $backupStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupPath = "$WorkspaceCfg.backup-$backupStamp"
    Copy-Item -LiteralPath $WorkspaceCfg -Destination $backupPath

    $newline = if ($workspaceText.Contains("`r`n")) { "`r`n" } else { "`n" }
    $addition = "$newline# BentleyBridge managed AddIn$newline$includeLine$newline"
    [System.IO.File]::AppendAllText($WorkspaceCfg, $addition, [System.Text.Encoding]::Default)
    Write-Host "Backup: $backupPath" -ForegroundColor Yellow
} else {
    Write-Host 'WorkSpace već uključuje BentleyBridge.cfg; nije mijenjan.' -ForegroundColor DarkGray
}

Write-Step 'Inicijalizacija lokalnog Git repozitorija'
Push-Location $RepositoryRoot
try {
    if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot '.git'))) {
        & git init -b main
        if ($LASTEXITCODE -ne 0) { throw 'git init nije uspio.' }
    }

    & git add --all
    & git diff --cached --quiet
    if ($LASTEXITCODE -ne 0) {
        & git commit -m 'Initialize BentleyBridge structure'
        if ($LASTEXITCODE -ne 0) {
            throw 'Početni commit nije uspio. Provjeri git user.name i user.email.'
        }
    } else {
        Write-Host 'Nema novih lokalnih promjena za commit.' -ForegroundColor DarkGray
    }

    # `gh repo view` za nepostojeći repo piše na stderr, što Windows PowerShell
    # uz ErrorActionPreference=Stop pretvara u iznimku. Popis repozitorija
    # omogućuje sigurnu provjeru bez očekivane pogreške.
    $repositoryOwner = $GitHubRepository.Split('/')[0]
    $knownRepositories = @(& gh repo list $repositoryOwner --limit 1000 --json nameWithOwner --jq '.[].nameWithOwner')
    if ($LASTEXITCODE -ne 0) { throw 'Provjera postojećih GitHub repozitorija nije uspjela.' }
    $remoteExists = ($knownRepositories -contains $GitHubRepository)

    & git remote get-url origin 1>$null 2>$null
    $originExists = ($LASTEXITCODE -eq 0)

    if (-not $remoteExists) {
        if ($originExists) {
            throw "Lokalni origin već postoji, ali GitHub repozitorij $GitHubRepository nije pronađen. Provjeri origin prije nastavka."
        }
        & gh repo create $GitHubRepository --public --source $RepositoryRoot --remote origin --description 'Generic JSON-driven managed AddIn bridge for Bentley OpenCities Map'
        if ($LASTEXITCODE -ne 0) { throw 'Stvaranje javnog GitHub repozitorija nije uspjelo.' }
    } elseif (-not $originExists) {
        & git remote add origin "https://github.com/$GitHubRepository.git"
    }

    & git push -u origin main
    if ($LASTEXITCODE -ne 0) { throw 'Push na origin/main nije uspio.' }
}
finally {
    Pop-Location
}

Write-Step 'Gotovo'
Write-Host "Lokalni repozitorij: $RepositoryRoot"
Write-Host "GitHub: https://github.com/$GitHubRepository"
Write-Host "WorkSpace CFG: $WorkspaceCfg"
Write-Host 'Sljedeće: izgraditi Hello World AddIn BRIDGE_RUN.dll i potvrditi MdlTaskID/autoload naredbom BRIDGE_RUN.' -ForegroundColor Green
