using System.Collections.Generic;
using Research.Interface.Model;
using UnityEngine;

namespace StellarFunctions.Support;

// Support-owned research contract. Functions maps its pack research
// definitions into these specs; the research service applies them to the
// native ResearchSettingsList without knowing the pack format. Part names
// stay unresolved here: the service resolves them through the caller-
// supplied PartResolver so support never depends on the pack registry.
public sealed class TechSpec
{
    public ushort Id;
    public string Name;
    public Vector2Int Position;
    public bool Hidden;
    public ushort GrantedBy;
    public string GrantedByName;
    public IReadOnlyList<ushort> Requires;
    public IReadOnlyList<string> UnlockParts;
    public IReadOnlyList<ResearchUnlockableFeature> UnlockFeatures;
}
