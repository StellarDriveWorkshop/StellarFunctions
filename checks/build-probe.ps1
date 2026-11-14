param([string]$GameInstall='C:/Program Files (x86)/Steam/steamapps/common/StellarDrive Demo',[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot
$out=if($OutputDirectory){$OutputDirectory}else{Join-Path $project 'work'}
New-Item -ItemType Directory -Path $out -Force | Out-Null
$candidate=Join-Path $project 'work/StellarFunctions.dll'
if(!(Test-Path -LiteralPath $candidate)){throw 'Build the candidate mod first (build.ps1).'}
$probe=Join-Path $out 'MenuResearchProbe.dll'
$dotnetCmd=Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet=if($dotnetCmd){$dotnetCmd.Source}else{$null}
if(!$dotnet){$dotnet='C:/Program Files/dotnet/dotnet.exe'}
if(!(Test-Path -LiteralPath $dotnet)){throw 'No dotnet CLI was found on PATH or at the default install location.'}
$dotnetRoot=Split-Path $dotnet -Parent
$latestSdk=& $dotnet --list-sdks 2>$null | ForEach-Object {($_ -split '\s')[0]} | Where-Object {$_ -match '^\d+\.\d+\.\d+$'} | Sort-Object {[version]$_} -Descending | Select-Object -First 1
if(!$latestSdk){throw 'No installed .NET SDK compiler was found.'}
$compiler=Join-Path $dotnetRoot "sdk/$latestSdk/Roslyn/bincore/csc.dll"
if(!(Test-Path -LiteralPath $compiler)){throw "Roslyn compiler not found at $compiler"}
$arguments=@('-nologo','-target:library','-langversion:latest','-optimize+','-deterministic+','-nostdlib+',"-out:$probe","-reference:$candidate")
foreach($file in Get-ChildItem -LiteralPath (Join-Path $GameInstall 'StellarDrive_Data/Managed') -Filter '*.dll'){$arguments+=('-reference:'+$file.FullName)}
foreach($file in @((Join-Path $GameInstall 'MelonLoader/net35/MelonLoader.dll'),(Join-Path $GameInstall 'MelonLoader/net35/0Harmony.dll'))){$arguments+=('-reference:'+$file)}
$arguments+=@('MenuResearchProbe','DedicatedResearchChecks','UiRedesignChecks','ResponsiveEditorChecks','BroadcastMenuLifecycleChecks','BroadcastStateChecks','SourcePresentationChecks','DirectoryTransportChecks','TransmitterAssetChecks','TransmitterCodecChecks','TransmitterDirectoryChecks','TransmitterRuntimeChecks','CallSizingCodecChecks','CallCatalogueChecks','ReleaseMetadataChecks','NestedBroadcastLifecycleChecks','MoreMathAdapterChecks','MoreMathLifecycleChecks','CallSizingNativeChecks','CallSizingAuthorityChecks','CallSizingDefaultsChecks','CallSocketAuditChecks') | ForEach-Object { Join-Path $PSScriptRoot ($_.ToString()+'.cs') }
& $dotnet $compiler @arguments
if($LASTEXITCODE -ne 0){throw 'Menu research probe compilation failed.'}
Get-FileHash -LiteralPath $probe -Algorithm SHA256
