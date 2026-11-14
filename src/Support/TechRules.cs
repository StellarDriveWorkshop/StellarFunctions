using System.Collections.Generic;

namespace StellarFunctions.Support;

// Pure research rules: spec validation, id-conflict planning, ownership-
// aware unlock-array edits and the grant decision. No Unity or game types so
// checks/core exercises the same code the game runs.
public static class TechRules
{
    public static bool ValidSpec(ushort id, string name) => id != 0 && !string.IsNullOrEmpty(name);

    // Per-spec application decision, first-wins: a spec applies only when its
    // id is not already owned by the native/foreign list and no earlier spec
    // in the same set claimed it. Returns one entry per input spec id.
    public static List<bool> PlanApply(IReadOnlyList<ushort> specIds, IEnumerable<ushort> existingIds)
    {
        var existing = new HashSet<ushort>(existingIds);
        var seen = new HashSet<ushort>();
        var plan = new List<bool>(specIds.Count);
        foreach (ushort id in specIds)
        {
            bool apply = !existing.Contains(id) && seen.Add(id);
            plan.Add(apply);
        }
        return plan;
    }

    // Entries to append to a native tech's unlockedParts: wanted references
    // not already present, deduplicated, order preserved.
    public static List<T> MergeAdditions<T>(IReadOnlyList<T> existing, IReadOnlyList<T> wanted)
        where T : class
    {
        var have = new HashSet<T>(existing ?? new T[0]);
        var add = new List<T>();
        foreach (T item in wanted)
            if (item != null && have.Add(item))
                add.Add(item);
        return add;
    }

    // Ownership-aware restore: drop only the references this service added.
    // Foreign additions to the same array and the relative order of
    // everything else are preserved.
    public static T[] RemoveOwned<T>(IReadOnlyList<T> current, IEnumerable<T> owned)
        where T : class
    {
        if (current == null)
            return null;
        var drop = new HashSet<T>(owned);
        var kept = new List<T>(current.Count);
        foreach (T item in current)
            if (!drop.Contains(item))
                kept.Add(item);
        return kept.ToArray();
    }

    // A hidden grant is due when the prerequisite is researched and the
    // target is not.
    public static bool GrantDue(bool prerequisiteResearched, bool targetResearched) =>
        prerequisiteResearched && !targetResearched;
}
