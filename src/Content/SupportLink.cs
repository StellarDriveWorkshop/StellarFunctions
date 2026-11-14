using System;
using System.Linq;
using StellarFunctions.Support;

namespace StellarFunctions.Game;

// Functions-owned bootstrap for the support part pipeline. Owns the
// one-time wiring that every registration path depends on: the catalogue
// and build-menu Harmony installs. Support.Registration invokes this
// through its Bootstrap delegate so no registration path can bypass
// initialization.
internal static class SupportLink
{
    private static bool _installed;

    // Retry-safe: _installed latches only when both installs verify their
    // patches. Each Install is itself idempotent — an already-installed
    // subsystem returns true immediately — so a partial failure retries
    // only the missing half on the next call.
    public static bool EnsureInstalled()
    {
        if (_installed)
            return true;
        bool catalogue = Catalogue.Install();
        bool menu = MenuCatalog.Install();
        _installed = catalogue && menu;
        return _installed;
    }

    // ContentPartDef -> PartSpec mapping stays Functions-side: the spec
    // contract is support-owned but the pack definition shape is not.
    public static void RegisterDefinition(Content.ContentPartDef def)
    {
        if (def == null)
            return;
        EnsureInstalled();
        Registration.Register(
            new PartSpec
            {
                Id = def.Id,
                Asset = def.Asset,
                Label = def.Label,
                Description = def.Description,
                Donor = def.Donor,
                StateClass = def.State,
                Mass = def.Mass,
                Snapping = def.Snapping,
                SnappingDeclared = def.SnappingDeclared,
                BoundsCenter = def.BoundsCenter,
                BoundsSize = def.BoundsSize,
                ClearDonorVisual = def.ClearDonorVisual,
                BoundsAuto = def.BoundsAuto,
                SocketsDeclared = def.SocketsDeclared,
                FitInteraction = def.FitInteraction,
                Menu = def.Menu,
                Attached = def.Attached,
                BuildMenuAfter = def.BuildMenuAfter,
                Cost = def.Cost,
                Meshes = def.Meshes.Select(MapMesh).ToList(),
                Sockets = def.Sockets.Select(MapSocket).ToList(),
            }
        );
    }

    private static MeshSpec MapMesh(Content.MeshDef mesh) =>
        new MeshSpec
        {
            Shape = mesh.Shape,
            DonorPath = mesh.DonorPath,
            Material = mesh.Material,
            Position = mesh.Position,
            Euler = mesh.Euler,
            Scale = mesh.Scale,
            Tint = mesh.Tint,
            Text = mesh.Text,
            Screen = mesh.Screen,
            Font = mesh.Font,
            TextColor = mesh.TextColor,
            Columns = mesh.Columns,
            Speed = mesh.Speed,
        };

    private static SocketSpec MapSocket(Content.SocketDef socket) =>
        new SocketSpec
        {
            Id = socket.Id,
            Type = socket.Type,
            Position = socket.Position,
            Facing = socket.Facing,
            Relayed = socket.Relayed,
        };
}
