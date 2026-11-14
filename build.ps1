param(
    [string]$GameInstall = 'C:/Program Files (x86)/Steam/steamapps/common/StellarDrive Demo'
)
$ErrorActionPreference = 'Stop'
$contentPath=Join-Path $PSScriptRoot 'content/functions.json'
$contentBytes=[IO.File]::ReadAllBytes($contentPath)
if($contentBytes.Length -ge 3 -and $contentBytes[0] -eq 239 -and $contentBytes[1] -eq 187 -and $contentBytes[2] -eq 191){
    throw 'Embedded functions.json must be UTF-8 without BOM; the pinned content loader treats a BOM as JSON data.'
}
try{$content=Get-Content -LiteralPath $contentPath -Raw | ConvertFrom-Json}catch{throw 'Embedded functions.json is not valid JSON.'}
$pluginSource=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src/Plugin.cs') -Raw
$versionMatch=[regex]::Match($pluginSource,'MelonInfo\([^,\r\n]+,\s*"[^"]+",\s*"(?<version>\d+\.\d+\.\d+)"')
if(!$versionMatch.Success){throw 'Cannot determine the plugin release version from MelonInfo.'}
if([string]$content.version -cne $versionMatch.Groups['version'].Value){
    throw 'Embedded content version must match the MelonInfo release version.'
}

$managed = Join-Path $GameInstall 'StellarDrive_Data/Managed'
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCmd) { $dotnetCmd.Source } else { $null }
if (-not $dotnet) { $dotnet = 'C:/Program Files/dotnet/dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'No dotnet CLI was found on PATH or at the default install location.' }
$dotnetRoot = Split-Path $dotnet -Parent
$latestSdk = (& $dotnet --list-sdks 2>$null |
    ForEach-Object { ($_ -split '\s')[0] } |
    Where-Object { $_ -match '^\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_ } -Descending |
    Select-Object -First 1)
if (-not $latestSdk) { throw 'No installed .NET SDK compiler was found.' }
$compiler = Join-Path $dotnetRoot "sdk/$latestSdk/Roslyn/bincore/csc.dll"
if (-not (Test-Path -LiteralPath $compiler)) { throw "Roslyn compiler not found at $compiler" }

$work = Join-Path $PSScriptRoot 'work'
New-Item -ItemType Directory -Path $work -Force | Out-Null
$output = Join-Path $work 'StellarFunctions.dll'
$sources = Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -Recurse | ForEach-Object FullName
$arguments = @('-nologo', '-target:library', '-langversion:latest', '-optimize+', '-deterministic+', '-nostdlib+', "-out:$output")
$arguments += "-resource:$(Join-Path $PSScriptRoot 'content/functions.json'),content.functions.json"
$arguments += $sources
foreach ($name in @(
    'mscorlib', 'System', 'System.Core', 'netstandard', 'System.Runtime',
    'Core', 'Research', 'Research.Interface', 'Players', 'Players.Interface', 'Ships', 'Ships.Interface', 'WorldTracking.Interface',
    'Assembly-CSharp',
    'Saving', 'Saving.Interface', 'Saving.Serialization', 'Managers.Interface', 'UI',
    'UnityEngine.CoreModule', 'UnityEngine.PhysicsModule', 'UnityEngine.UIModule', 'UnityEngine.UIElementsModule', 'UnityEngine.JSONSerializeModule', 'UnityEngine.UI', 'UnityEngine.TextCoreFontEngineModule', 'UnityEngine.TextRenderingModule', 'Unity.TextMeshPro', 'Unity.InputSystem', 'FishNet.Runtime', 'Newtonsoft.Json', 'Unity.Localization'
)) {
    $path = Join-Path $managed "$name.dll"
    if (-not (Test-Path -LiteralPath $path)) { throw "Required game reference is missing: $path" }
    $arguments += "-reference:$path"
}
foreach ($path in @(
    (Join-Path $GameInstall 'MelonLoader/net35/MelonLoader.dll'),
    (Join-Path $GameInstall 'MelonLoader/net35/0Harmony.dll')
)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required mod reference is missing: $path" }
    $arguments += "-reference:$path"
}

& $dotnet $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'StellarFunctions compilation failed.' }
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
@{ path = $output; sha256 = $hash; createdUtc = [DateTime]::UtcNow.ToString('o'); purpose = 'Param/Return/Call implementation candidate'; } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'build.json') -Encoding utf8
Get-FileHash -LiteralPath $output -Algorithm SHA256
