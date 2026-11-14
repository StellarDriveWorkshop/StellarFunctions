# Third-party notices

StellarFunctions is original source code licensed under the MIT License
(see [LICENSE](LICENSE)). It contains no copied third-party source code or
bundled assets.

## Runtime dependencies (not distributed)

The mod compiles against and runs inside these components. They are
**not** included in this repository or the released DLL:

| Component | Role | License |
|---|---|---|
| StellarDrive Demo 0.8.3 (game assemblies) | Required runtime; provides all referenced types | Proprietary — owned by CuriousOwl Games |
| MelonLoader 0.7.3 (Mono) | Mod loader | MIT |
| 0Harmony | Runtime patching (bundled with MelonLoader) | MIT |
| Unity Engine 6000.3.24f1 modules | Game engine APIs | Proprietary — Unity Technologies |
| FishNet | Networking (bundled with the game) | See FishNet upstream |
| Newtonsoft.Json | JSON (bundled with the game) | MIT |

## Optional integrations (not distributed, not required)

- **Ship Schematics 1.0.2** — separate mod; StellarFunctions integrates with
  its reviewed public API at runtime when present.
- **MoreMath 1.3.0** — separate mod; an optional pinned adapter enables its
  operations inside virtual calls.
- **SDModKit** — not required and not referenced. StellarFunctions was
  reimplemented independently of SDModKit; no SDModKit code or assets are
  included.

## Assets

All meshes, materials and textures used by function parts are game assets
referenced by name at runtime (donor-part meshes). `content/functions.json`
is original project data.
