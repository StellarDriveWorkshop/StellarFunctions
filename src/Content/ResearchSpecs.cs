using System;
using System.Collections.Generic;
using System.Linq;
using Research.Interface.Model;
using Ships.Interface.Settings;
using StellarFunctions.Game;
using StellarFunctions.Support;

namespace StellarFunctions.Content;

// Functions-owned mapping from pack research definitions to support
// TechSpecs. Part names resolve through the registry first, then native
// lookups, then numeric ids — the same order the legacy research applier
// used. Support never sees ContentPack or ContentResearchDef.
public static class ResearchSpecs
{
    public static PartSettings ResolvePart(string name) =>
        ContentRegistry.PartByAsset(name)
        ?? Support.NativeAssets.PartByName(name)
        ?? (ushort.TryParse(name, out ushort id) ? PartLookup.ById(id) : null);

    public static List<TechSpec> FromPacks(IEnumerable<ContentPack> packs)
    {
        var specs = new List<TechSpec>();
        foreach (ContentPack pack in packs ?? Enumerable.Empty<ContentPack>())
        {
            foreach (ContentResearchDef def in pack.Research)
            {
                specs.Add(Map(pack, def));
            }
        }

        return specs;
    }

    private static TechSpec Map(ContentPack pack, ContentResearchDef def)
    {
        var features = new List<ResearchUnlockableFeature>();
        foreach (string name in def.Features)
        {
            if (
                Enum.TryParse(name, true, out ResearchUnlockableFeature feature)
                && Enum.IsDefined(typeof(ResearchUnlockableFeature), feature)
            )
            {
                features.Add(feature);
            }
            else
            {
                ContentLog.Warn(pack.Id, def.Name + ": unknown feature " + name);
            }
        }
        return new TechSpec
        {
            Id = def.Id,
            Name = def.Name,
            Position = def.Position,
            Hidden = def.Hidden,
            GrantedBy = def.GrantedBy,
            GrantedByName = def.GrantedByName,
            Requires = def.Requires.ToArray(),
            UnlockParts = def.Parts.ToArray(),
            UnlockFeatures = features,
        };
    }
}
