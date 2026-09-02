#requires -Version 5.1
<#
.SYNOPSIS
    Gradi BRIDGE_RUN.dll (net4.8, x64) i isporucuje ga u runtime\current s SHA-256 zapisom.

.DESCRIPTION
    - Koristi klasicni MSBuild projekt (bez SDK-style) preko `dotnet msbuild`.
    - $(MS) = OpenCities Map Ultimate install root; default ispod, moze se nadjacati parametrom.
    - Izlaz ide izravno u  C:\AITools\BentleyBridge\runtime\current\BRIDGE_RUN.dll
      (NE u $(MS)mdlapps, NE u Program Files).
    - Zapisuje  runtime\current\BRIDGE_RUN.dll.sha256  u formatu "<hash>  BRIDGE_RUN.dll".
    - Ne pokrece OpenCities, ne dira DGN, ne mijenja CFG.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot     = 'C:\AITools\BentleyBridge',
    [string]$MS           = 'C:\Program Files\Bentley\Map CONNECT Edition\MapUltimate\',
    [ValidateSet('Release','Debug')] [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Step($m){ Write-Host "`n==> $m" -ForegroundColor Cyan }

$proj     = Join-Path $RepoRoot 'src\BRIDGE_RUN\BRIDGE_RUN.csproj'
$outDir   = Join-Path $RepoRoot 'runtime\current'
$dll      = Join-Path $outDir 'BRIDGE_RUN.dll'
$shaFile  = Join-Path $outDir 'BRIDGE_RUN.dll.sha256'

if (-not (Test-Path -LiteralPath $proj))      { throw "Nema projekta: $proj" }
if (-not (Test-Path -LiteralPath $MS))        { throw "MS install root ne postoji: $MS" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "dotnet nije na PATH-u." }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# MS predajemo kao ENV varijablu (ne kao -p: global property):
#  - global property se ne moze normalizirati unutar csproj-a,
#  - env var se moze (csproj radi EnsureTrailingSlash),
#  - izbjegava se i native-arg "\"" bug s trailing backslashom.
$env:MS = ($MS.TrimEnd('\') + '\')

Step "Build ($Configuration | x64), MS=$env:MS"
& dotnet msbuild $proj `
    -nologo `
    -verbosity:minimal `
    -p:Configuration=$Configuration `
    -p:Platform=x64 `
    -t:Rebuild
if ($LASTEXITCODE -ne 0) { throw "dotnet msbuild vratio $LASTEXITCODE" }

if (-not (Test-Path -LiteralPath $dll)) { throw "Build nije proizveo $dll" }

Step 'SHA-256'
$hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLower()
"$hash  BRIDGE_RUN.dll" | Set-Content -LiteralPath $shaFile -Encoding ascii -NoNewline
Add-Content -LiteralPath $shaFile -Value "`n" -Encoding ascii

$fi = Get-Item -LiteralPath $dll
Step 'Rezultat'
Write-Host ("  DLL      : {0}" -f $dll)
Write-Host ("  Velicina : {0} B" -f $fi.Length)
Write-Host ("  SHA-256  : {0}" -f $hash)
Write-Host ("  Zapis    : {0}" -f $shaFile)

# Kratka provjera TFM / arhitekture iz PE/CLI metapodataka
$bytes = [System.IO.File]::ReadAllBytes($dll)
$txt   = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes)
$tfm   = [regex]::Match($txt, '\.NETFramework,Version=v[0-9.]+')
Write-Host ("  TFM      : {0}" -f ($(if($tfm.Success){$tfm.Value}else{'(nije nadjen string)'})))
Write-Host ""
Write-Host "Gotovo. Sljedece: commit runtime\current\BRIDGE_RUN.dll + .sha256 u bogiboo/BentleyBridge, pa Bozidarov kontrolirani test naredbom BRIDGE_RUN." -ForegroundColor Green
