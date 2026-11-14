param([string]$GameInstall='C:/Program Files (x86)/Steam/steamapps/common/StellarDrive Demo')
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'This check requires PowerShell 7 or later for AssemblyLoadContext.'}
$gameJson=Join-Path $GameInstall 'StellarDrive_Data/Managed/Newtonsoft.Json.dll'
$context=[System.Runtime.Loader.AssemblyLoadContext]::new('persistence-json-regression',$true)
try {
 $assembly=$context.LoadFromAssemblyPath($gameJson)
 $token=$assembly.GetType('Newtonsoft.Json.Linq.JToken')
 $from=$token.GetMethod('FromObject',[type[]]@([object]))
 $parse=$token.GetMethod('Parse',[type[]]@([string]))
 $serialize=$assembly.GetType('Newtonsoft.Json.JsonConvert').GetMethod('SerializeObject',[type[]]@([object]))
 $equals=$token.GetMethod('DeepEquals',[type[]]@($token,$token))
 $id=[guid]::NewGuid()
 $raw=$from.Invoke($null,[object[]]@($id))
 $saved=$parse.Invoke($null,[object[]]@($serialize.Invoke($null,[object[]]@($id))))
 $normalized=$parse.Invoke($null,[object[]]@($serialize.Invoke($null,[object[]]@($id))))
 $changed=$parse.Invoke($null,[object[]]@($serialize.Invoke($null,[object[]]@([guid]::NewGuid()))))
 if($equals.Invoke($null,[object[]]@($raw,$saved))){throw 'Prior comparison unexpectedly accepted GUID/string token mismatch'}
 if(!$equals.Invoke($null,[object[]]@($normalized,$saved))){throw 'JSON-normalized equal identity refused'}
 if($equals.Invoke($null,[object[]]@($changed,$saved))){throw 'Changed identity was hidden'}
 [pscustomobject]@{status='PASS';checks=3;scope='Exact game Newtonsoft assembly; serialization oracle only, no game execution';assembly_sha256=(Get-FileHash $gameJson -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json
} finally {$context.Unload()}
