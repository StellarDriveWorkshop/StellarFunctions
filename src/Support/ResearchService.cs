using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Research;
using Research.Interface.Model;
using Research.Interface.Values;
using Ships.Interface.Settings;
using UnityEngine;

namespace StellarFunctions.Support;

// Applies TechSpecs to the native research list and grants hidden techs once
// their prerequisite completes. Server-authoritative grants go through
// ResearchNetworkedState; nothing here depends on a local player, client UI
// or another mod's registry.
//
// Ownership: the service only ever removes ResearchSettings objects it
// created and only removes the PartSettings references it appended to a
// native tech's unlockedParts. Foreign additions to the same array are
// preserved. The applied ResearchSettingsList instance is tracked, so
// reapplying against a different list cannot leave stale entries behind.
public static class ResearchService
{
    internal const float GrantInterval = 1f;
    private const string Scope = "research";

    private sealed class PendingGrant
    {
        public string TechName;
        public List<PartSettings> Parts;
    }

    private static readonly List<ResearchSettings> Added = new List<ResearchSettings>();
    private static readonly Dictionary<ushort, ushort> Grants = new Dictionary<ushort, ushort>();
    private static readonly Dictionary<ushort, PendingGrant> PendingGrants = new Dictionary<ushort, PendingGrant>();
    private static readonly Dictionary<ResearchSettings, List<PartSettings>> AddedUnlocks =
        new Dictionary<ResearchSettings, List<PartSettings>>();
    private static float _nextGrant;
    private static ResearchNetworkedState _state;
    private static ResearchSettingsList _appliedList;
    private static IReadOnlyList<TechSpec> _pending;
    private static bool _applied;

    // Functions supplies part-name resolution (content asset name, native
    // asset name or numeric id) so support never depends on the pack registry.
    public static Func<string, PartSettings> PartResolver { get; set; }

    public static IReadOnlyList<ResearchSettings> All => Added;
    public static bool Applied => _applied;
    public static ResearchSettingsList AppliedList => _appliedList;

    public static void Apply(IEnumerable<TechSpec> specs)
    {
        var materialized = specs as IReadOnlyList<TechSpec> ?? specs?.ToList();
        _pending = materialized;
        _applied = false;
        // Mirror of the legacy ordering: parts must be materialized before
        // their names can resolve to PartSettings for unlock arrays.
        Registration.EnsureMaterialized();
        var list = Resources.FindObjectsOfTypeAll<ResearchSettingsList>().FirstOrDefault();
        if (list == null || list.list == null)
        {
            NativeServices.Warn(
                Scope,
                "research settings list not found yet, custom techs will retry on the next tick"
            );
            return;
        }
        Revert();
        _appliedList = list;
        var ids = (materialized ?? (IReadOnlyList<TechSpec>)Array.Empty<TechSpec>())
            .Select(s => s?.Id ?? (ushort)0)
            .ToList();
        var plan = TechRules.PlanApply(ids, list.list.Where(s => s != null).Select(s => s.id));
        for (int i = 0; i < ids.Count; i++)
        {
            TechSpec spec = materialized[i];
            if (spec == null)
                continue;
            if (!plan[i])
            {
                NativeServices.Warn(Scope, spec.Name + ": research id " + spec.Id + " already taken, skipped");
                continue;
            }
            try
            {
                ApplyOne(spec, list);
            }
            catch (Exception error)
            {
                NativeServices.Error(Scope, (spec?.Name ?? "?") + ": research apply failed", error);
            }
        }
        ResearchLayout.Settle(list);
        _applied = true;
    }

    private static void ApplyOne(TechSpec spec, ResearchSettingsList list)
    {
        if (!TechRules.ValidSpec(spec.Id, spec.Name))
        {
            NativeServices.Warn(Scope, "tech spec id " + spec.Id + " / '" + spec.Name + "' invalid, skipped");
            return;
        }
        var parts = new List<PartSettings>();
        foreach (string name in spec.UnlockParts ?? Enumerable.Empty<string>())
        {
            PartSettings resolved = PartResolver?.Invoke(name);
            if (resolved == null)
                NativeServices.Warn(Scope, spec.Name + ": unlocked part " + name + " not found");
            else if (!parts.Contains(resolved))
                parts.Add(resolved);
        }
        var requirements = new List<ResearchSettings>();
        foreach (ushort required in spec.Requires ?? Enumerable.Empty<ushort>())
        {
            ResearchSettings found = list.list.FirstOrDefault(s => s != null && s.id == required);
            if (found == null)
            {
                NativeServices.Warn(
                    Scope,
                    spec.Name
                        + ": required tech "
                        + required
                        + " not found, this node would sit at the root of the tree"
                );
                continue;
            }
            requirements.Add(found);
        }
        var features =
            spec.UnlockFeatures ?? (IReadOnlyList<ResearchUnlockableFeature>)Array.Empty<ResearchUnlockableFeature>();
        var settings = ScriptableObject.CreateInstance<ResearchSettings>();
        // Failure rollback scope: a throw anywhere below must not leave an
        // untracked ScriptableObject, a registered-but-untracked list entry,
        // stale grant mappings or a partially merged native unlock array.
        ResearchSettings mergedTech = null;
        PartSettings[] mergedBefore = null;
        try
        {
            settings.name = spec.Name.Replace(" ", string.Empty);
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.id = spec.Id;
            settings.techName = spec.Name;
            settings.unlockedParts = parts.ToArray();
            settings.unlockedFeatures = features.ToArray();
            settings.unlockedGuideEntriesIds = new uint[0];
            settings.requirements = requirements.ToArray();
            // resourceCost is Items.Model.ItemInstance[]; Items is not a compile
            // reference, so the empty array is injected through reflection. Leave
            // the field untouched if the type cannot be resolved.
            Array emptyCost =
                ItemCatalog.ItemInstanceType != null ? Array.CreateInstance(ItemCatalog.ItemInstanceType, 0) : null;
            if (emptyCost != null)
                AccessTools.Field(typeof(ResearchSettings), "resourceCost").SetValue(settings, emptyCost);
            settings.positionInTechTree = spec.Position;
            settings.availableInDemo = true;
            settings.hideFromTechTree = spec.Hidden;

            list.list.Add(settings);
            Added.Add(settings);

            ushort grantedBy = spec.GrantedBy;
            ResearchSettings nativeTech = null;
            if (grantedBy == 0 && !string.IsNullOrEmpty(spec.GrantedByName))
            {
                nativeTech = list.list.FirstOrDefault(s => s != null && s.techName == spec.GrantedByName);
                if (nativeTech != null)
                    grantedBy = nativeTech.id;
                else
                    PendingGrants[spec.Id] = new PendingGrant { TechName = spec.GrantedByName, Parts = parts };
            }
            if (grantedBy != 0)
                Grants[spec.Id] = grantedBy;
            if (nativeTech != null)
            {
                mergedTech = nativeTech;
                mergedBefore = nativeTech.unlockedParts;
                MergeUnlocks(nativeTech, parts);
            }
            NativeServices.Info(
                Scope,
                spec.Name + ": added as tech " + spec.Id + " unlocking " + parts.Count + " parts"
            );
        }
        catch
        {
            list?.list?.Remove(settings);
            Added.Remove(settings);
            // The instance was never published to a native system, so an
            // immediate destroy is safe and keeps the rollback synchronous.
            UnityEngine.Object.DestroyImmediate(settings);
            Grants.Remove(spec.Id);
            PendingGrants.Remove(spec.Id);
            if (mergedTech != null)
                mergedTech.unlockedParts = mergedBefore;
            // AddedUnlocks keeps the merge record intentionally: its entries
            // are matched by reference against the live array at Revert, so a
            // rolled-back merge leaves only inert entries cleared next cycle.
            throw;
        }
    }

    // Appends our part references to a native tech's unlockedParts, tracking
    // exactly what was added so Revert removes only our contributions.
    private static void MergeUnlocks(ResearchSettings tech, List<PartSettings> parts)
    {
        if (tech == null || parts == null || parts.Count == 0)
            return;
        List<PartSettings> merged = TechRules.MergeAdditions(tech.unlockedParts, parts);
        if (merged.Count == 0)
            return;
        if (!AddedUnlocks.TryGetValue(tech, out List<PartSettings> ours))
            AddedUnlocks[tech] = ours = new List<PartSettings>();
        ours.AddRange(merged);
        tech.unlockedParts = (tech.unlockedParts ?? Array.Empty<PartSettings>()).Concat(merged).ToArray();
    }

    // Removes only what this service added: tracked ResearchSettings entries
    // from the applied list, tracked grant mappings, and the exact
    // PartSettings references appended to native unlock arrays. Entries or
    // unlock references added by other mods are left untouched.
    public static void Revert()
    {
        ResearchSettingsList list = _appliedList;
        if (list?.list != null)
            foreach (ResearchSettings settings in Added)
                list.list.Remove(settings);
        // The service owns these HideAndDontSave instances; destroy them so
        // repeated Apply/Revert cycles do not leak ScriptableObjects.
        foreach (ResearchSettings settings in Added)
            if (settings != null)
                UnityEngine.Object.Destroy(settings);
        Added.Clear();
        Grants.Clear();
        PendingGrants.Clear();
        foreach (var pair in AddedUnlocks)
        {
            PartSettings[] current = pair.Key.unlockedParts;
            if (current != null)
                pair.Key.unlockedParts = TechRules.RemoveOwned(current, pair.Value);
        }
        AddedUnlocks.Clear();
        _appliedList = null;
        _state = null;
    }

    // Grants pending hidden techs once their prerequisite is researched. Runs
    // from ContentActions.EnsureNetworking every plugin update on both sides;
    // only the server acts. Safe with zero local/connected players.
    public static void Tick()
    {
        if (!_applied && _pending != null && Time.unscaledTime >= _nextGrant)
        {
            _nextGrant = Time.unscaledTime + GrantInterval;
            Apply(_pending);
        }
        if (Time.unscaledTime < _nextGrant)
            return;
        _nextGrant = Time.unscaledTime + GrantInterval;
        ResolvePendingGrants();
        if (Grants.Count == 0 || !NativeServices.IsServer)
            return;
        if (_state == null)
        {
            _state = FindState();
            if (_state == null)
                return;
        }
        foreach (var grant in Grants)
            if (TechRules.GrantDue(_state.IsResearched(grant.Value), _state.IsResearched(grant.Key)))
                _state.ServerMarkResearchAsDone(grant.Key);
    }

    private static ResearchNetworkedState FindState() =>
        UnityEngine.Object.FindFirstObjectByType<ResearchNetworkedState>();

    // grantedByName techs that were absent at apply time are retried here so
    // a prerequisite added to the list later still produces a grant.
    private static void ResolvePendingGrants()
    {
        if (PendingGrants.Count == 0 || _appliedList?.list == null)
            return;
        foreach (var pair in PendingGrants.ToList())
        {
            ResearchSettings tech = _appliedList.list.FirstOrDefault(s =>
                s != null && s.techName == pair.Value.TechName
            );
            if (tech == null)
                continue;
            Grants[pair.Key] = tech.id;
            PendingGrants.Remove(pair.Key);
            // Late discovery must produce the same visible preview as an
            // early one: merge our parts into the prerequisite's unlocks.
            if (pair.Value.Parts != null)
                MergeUnlocks(tech, pair.Value.Parts);
        }
    }
}

// Minimal tech-tree settling: shown custom techs that collide with an
// occupied cell are moved to the nearest free cell. Hidden techs are never
// shown, so for hidden-only packs this is a no-op.
internal static class ResearchLayout
{
    public static void Settle(ResearchSettingsList list)
    {
        if (list?.list == null)
            return;
        var shown = list.list.Where(IsShown).ToList();
        foreach (var tech in ResearchService.All.Where(IsShown).OrderBy(t => t.id).ToList())
        {
            if (Free(tech.positionInTechTree, tech, shown))
                continue;
            int minX =
                tech.requirements != null && tech.requirements.Length != 0
                    ? tech.requirements.Where(IsShown).Select(r => r.positionInTechTree.x + 1).DefaultIfEmpty(0).Max()
                    : 0;
            Vector2Int? best = null;
            float bestScore = float.MaxValue;
            for (int x = minX; x <= 7; x++)
            for (int y = 0; y <= 6; y++)
            {
                var cell = new Vector2Int(x, y);
                if (!Free(cell, tech, shown))
                    continue;
                float score =
                    Mathf.Abs(x - tech.positionInTechTree.x) * 1.5f + Mathf.Abs(y - tech.positionInTechTree.y);
                if (score < bestScore)
                {
                    best = cell;
                    bestScore = score;
                }
            }
            if (best.HasValue)
                tech.positionInTechTree = best.Value;
        }
    }

    internal static bool IsShown(ResearchSettings settings) =>
        settings != null && settings.availableInDemo && !settings.hideFromTechTree;

    private static bool Free(Vector2Int cell, ResearchSettings tech, List<ResearchSettings> shown) =>
        shown.All(other => other == tech || other.positionInTechTree != cell);
}
