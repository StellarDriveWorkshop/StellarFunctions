# Building StellarFunctions

## Requirements

- Windows with PowerShell 5.1 or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0); version
  10.0.102 was used for the documented build. `build.ps1` resolves the
  highest installed SDK through `dotnet` on `PATH` (or the standard install
  location).
- A StellarDrive Demo **0.8.3** install — the build references the game's
  managed assemblies and MelonLoader/0Harmony from the install directory. The
  install must have **MelonLoader 0.7.3 (Mono)** installed. Game files are
  **not** redistributed.

## Build

```powershell
./build.ps1                                   # default Steam install path
./build.ps1 -GameInstall 'D:\Games\StellarDrive Demo'
```

Output: `work/StellarFunctions.dll` plus `work/build.json` (path, SHA-256,
timestamp). The script enforces that `content/functions.json`'s version
matches the `MelonInfo` release version and that the JSON has no BOM. It
normalizes the embedded JSON to LF line endings so checkout line endings do
not change the DLL. Reproducing a release also requires matching compiler and
reference assembly inputs.

## Tests

- **Pure checks** (no game needed):

  ```powershell
  cd checks/core
  dotnet run -c Release
  ```

  Covers the compiler/executor, screen text fitting, catalogue/menu merge
  planning, spec rules and profiler statistics.

- **Native probe** (requires the game install; compiles an in-game
  verification mod):

  ```powershell
  ./checks/build-probe.ps1
  ```

  Produces `work/MenuResearchProbe.dll` by default (or the directory passed
  with `-OutputDirectory`). It self-verifies part registration, screens,
  research, networking and sizing when loaded by MelonLoader in an isolated
  test copy of the game. The probe script accepts `-GameInstall`.

- **Persistence JSON check** (PowerShell 7 required; uses the installed game's
  Newtonsoft.Json assembly without launching the game):

  ```powershell
  ./checks/persistence-json-check.ps1 -GameInstall 'D:\Games\StellarDrive Demo'
  ```

## C# formatting

The repository pins CSharpier as a local .NET tool. Restore it once, then
format or check the maintained C# source and checks before submitting changes:

```powershell
dotnet tool restore
dotnet csharpier format src checks
dotnet csharpier check src checks
```

The configuration uses a 120-column wrapping target and four-space indentation.
Generated files, build outputs, and XML project files are excluded. Editor
integrations should use this repository's pinned CSharpier version.

## Releases

Release artifacts are versioned directories (`releases/v<major>.<minor>.<patch>/`)
containing the DLL and `SHA256SUMS`. Numbered artifacts are never overwritten.
Bump `MelonInfo` in `src/Plugin.cs` and `content/functions.json` together —
the build fails if they disagree.

See [docs/RELEASING.md](docs/RELEASING.md) for the proposed public repository
release procedure.
