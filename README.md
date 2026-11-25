# StellarFunctions

**StellarDrive Functions 2.0.0** — reusable ship circuits
for [StellarDrive](https://store.steampowered.com/app/4689730/) Demo 0.8.3.

StellarFunctions adds programmable function parts that turn ship wiring into
reusable templates with private per-call state.

## Parts

| Part | ID | What it does |
|---|---|---|
| **Param** | 62006 | Declares a named input with a default value. Params feed values into calls. |
| **Return** | 62007 | Emits a named output value back to the caller. |
| **Call** | 62005 | Runs a private virtual copy of a function template — including its own Memory cells and local labels — inside the native server signal pass. 1, 2 or 3 inputs. |
| **Function Transmitter** | 62009 | Broadcasts a function template to other ships/structures so remote Calls can run it. |

A hidden research entry (62008) unlocks the set. All parts show live status
on their faces; long names shrink to a readable size, then scroll.

Selecting a new function sizes the Call to match its inputs. A function with
no parameters uses the one-input housing. You can also choose the size manually.
Both methods preserve plugged cables and direct contacts on sockets that stay
in place. If shrinking would disconnect an input, the diagram highlights it.
Confirm to break that contact or remove the indicated cable, or cancel to keep
the current size and connections. Canceling this resize keeps the selected function.

The Call keeps its current size if the housing is obstructed, a newly exposed
socket would make a connection, or the game cannot verify the wiring. Omitted
inputs use their parameter defaults, or zero if there is no default.

## Install

1. Close the game and any server completely before replacing files.
2. Install **MelonLoader 0.7.3 (Mono)** if not already present.
3. Download `StellarFunctions.dll` and `SHA256SUMS` from [Releases](https://github.com/StellarDriveWorkshop/StellarFunctions/releases).
   Copy the verified DLL into `Mods/` on the **host/dedicated server
   and every joining client**. All sides must run the same build.
4. SDModKit is **not required** — this build is standalone. It coexists with
   SDModKit and other mods.

### Supported versions

- StellarDrive Demo **0.8.3** (Unity 6000.3.24f1)
- MelonLoader **0.7.3** Mono
- DedicatedServer **1.4.2** for dedicated-server play (not needed for solo)

### Optional integrations

- **Ship Schematics 1.0.2** — optional integration for preserving function
  metadata on schematic capture and placement. The adapter accepts the reviewed
  DLL with SHA-256 `11b6409966980cd2af603def9a880634300dbde21347f41b96429692dd68133d`;
  other builds are unsupported. Functions blocks the capture and placement
  entry points it can identify. If the log reports incomplete guard coverage,
  remove the incompatible DLL before using schematics. Native world saves
  remain available.
- **MoreMath 1.3.0** — optional adapter for MoreMath operations inside virtual
  calls. It activates only for the reviewed DLL with SHA-256
  `c5b09b8552c8982a98a5c74397016319338cbd1de788d17724761e3a6311ab5b` and
  expected module identity; other builds leave the adapter unavailable.

## Upgrading to 2.0.0

**Breaking change for existing circuits.** Calls with two or three inputs have
updated models, and their output moves one slot to sit opposite Input 1. Direct
connections at the old position will break. Back up your world before upgrading,
then reconnect affected circuits and check for unintended connections at the new
output position.

Update the host or dedicated server and every joining client to the same build.

## Building from source

See [BUILDING.md](BUILDING.md). The mod compiles against the installed
game's managed assemblies — no game files are redistributed.

## Architecture

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the Support/Functions
ownership boundaries.

## Issues

Report bugs and suggestions in the [Issues tab](https://github.com/StellarDriveWorkshop/StellarFunctions/issues).
