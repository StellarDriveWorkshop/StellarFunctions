# StellarFunctions architecture

StellarFunctions 1.0.0 is organized into three layers with a strict
dependency direction: Functions consumes Support; Support knows nothing
about Functions content.

```
src/
  Support/     reusable mechanisms (no Functions knowledge)
  Content/     Functions content framework — pack model, registry, actions,
               menus, research specs (StellarFunctions.Content), plus native
               lookups & install bootstrap (Game.cs / SupportLink.cs —
               StellarFunctions.Game)
  *.cs         Functions gameplay: parts, Call sizing, editor, directory,
               persistence, screens provider, state records,
               compatibility installs
```

## `StellarFunctions.Support` — reusable mechanisms

Owns everything that could serve another mod unchanged:

- **Registration / materialization** (`Registration`, `PartFactory`,
  `PartBuild`, `PartVisuals`, `PartSockets`, `NativeAssets`): turns content
  specs (`PartSpec`/`MeshSpec`/`SocketSpec` contracts) into native
  `PartSettings` assets with meshes, sockets, screens and build cost.
- **Catalogue & build-menu injection** (`Catalogue`, `MenuCatalog`,
  `CatalogueMerge`, `MenuPlan`): Harmony patches that merge materialized
  parts into the native part dictionary and build menu, preserving foreign
  entries.
- **Screens** (`Screens`, `ScreenSurface`, `ScreenText`,
  `MeasuredScreenLine`, `ScreenProfileStats`, `ScreenProfiler`): channel-
  provider screen registry, TMP construction, measured fit-then-scroll text
  layout, and the opt-in CPU profiler.
- **Lifecycle & services** (`BehaviourHost`, `ResearchService`,
  `BroadcastLink`, `NativeServices`, `LogGate`, `FailureBudget`,
  `PartTracking`, `LivePart`, `IPartBehaviour`): server-authoritative part
  ticks/actions, research application, FishNet broadcast registration
  reconciled to the live NetworkManager, cached native service access safe
  with zero local players.
- **Pure helpers** (`SpecRules`, `TechRules`, `TechSpec`, `MeshPlan`,
  `FitMath`, `SeatMath`, `SocketSeating`, `ItemCatalog`): unit-testable
  rules and math with no engine dependencies where possible.

## `StellarFunctions.Content` — Functions content framework

- `ContentPack`/`ContentPartDef`/`ContentPart`/`SocketDef`/`MeshDef`/
  `ContentResearchDef` (Model.cs): the pack definition model parsed from the
  embedded `content/functions.json`.
- `ContentRegistry`/`ContentRuntime`/`PartBehaviour`: pack loading,
  part/behaviour binding and the world lifecycle, adapting Functions
  behaviours onto `Support.IPartBehaviour`.
- `ContentActions`: the client→server part-action FishNet broadcast with
  server-side chest-interaction authorization.
- `ContentMenus`/`ContentConfigMenu`/`MenuLayout`: per-part configuration
  menu integration.
- `ContentLog`: scoped logging with once-only dedup.
- `ResearchSpecs`: maps pack research definitions onto
  `Support.ResearchService`.

## `StellarFunctions.Game` — native lookups & bootstrap

- `PlayerAccess`/`PartLookup`: local-player and native part-settings
  lookups (dedicated-server safe).
- `SupportLink` (internal): the one-time bootstrap `Registration` invokes —
  installs the catalogue and build-menu patches and reports readiness so
  materialization never proceeds on a partial install.

## `StellarFunctions` (root) — gameplay

- Part behaviours: `CallPartBehaviour`, `DefinitionPartBehaviour`,
  `TransmitterPartBehaviour`.
- `CallSizing`/`CallFaceText`/`CallFaceOrientation`: the three Call housing
  variants, face text fitting and camera-facing orientation.
- `FunctionEditor`/`FunctionEdits`/`FunctionGraph`/`FunctionDirectory`/
  `FunctionCatalog`/`FunctionCallState`/`FunctionVisualContext`/
  `FunctionSourcePresentation`: the in-game function editor, saved-definition
  directory and client presentation. `FunctionDefinitionStates.cs` holds the
  `FunctionPartState` record hierarchy (`FunctionParamState`,
  `FunctionReturnState`, `FunctionTransmitterState`).
- `FunctionStateCodec`/`NativeFunctionPersistence`/`NativeFunctionRuntime`/
  `NativeFunctionCompiler`/`NativeStateSerialization`/`NativeSignalBridge`/
  `NativeTemplateHooks`: save/load codec, beacon-state persistence,
  virtual-call execution inside the native signal pass, and native
  serialization/template hooks.
- `SchematicFunctionIntegration`, `MoreMathCompatibility`,
  `MoreMathOperations`: optional pinned integrations.
- `FunctionSocketPositionFix`, `PartSettingsLookupFix`: narrow native
  compatibility fixes.
- `Plugin`: MelonLoader entry point — installs everything, ticks research,
  networking reconciliation, editor and the screen profiler.

## Compatibility notes

- Part IDs 62005/62006/62007/62009 and research 62008 are stable contracts.
- `FunctionParamState`/`FunctionReturnState`/`FunctionCallState`/
  `FunctionTransmitterState` and the `@StellarFunctions/1:` persistence
  prefix define the save format.
- FishNet broadcast types are keyed by `FullName`; the action broadcast is
  `StellarFunctions.Content.ContentActions+ActionBroadcast`. 1.0.0 clients
  and servers must run matching builds.
