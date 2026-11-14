using System;
using System.Collections.Generic;

namespace StellarFunctions.Support;

// Pure catalogue-merge planner. Decides which registered entries are missing
// from a provider dictionary and which same-id entries belong to foreign
// content. Contains no Unity or game types so checks/core exercises the same
// code the game runs.
public static class CatalogueMerge
{
    public sealed class Result<T>
    {
        // Null when every registered id is already present: callers must not
        // rewrite the dictionary in that case.
        public Dictionary<ushort, T> Merged;

        // Ids whose existing entry is not our owned object. Never overwritten.
        public List<ushort> Conflicts;
    }

    public static Result<T> Plan<T>(IReadOnlyDictionary<ushort, T> existing, IReadOnlyDictionary<ushort, T> registered)
    {
        var result = new Result<T>();
        if (existing == null || registered == null || registered.Count == 0)
            return result;
        foreach (var pair in registered)
        {
            if (!existing.TryGetValue(pair.Key, out var current))
            {
                if (result.Merged == null)
                {
                    result.Merged = new Dictionary<ushort, T>(existing.Count + registered.Count);
                    foreach (var entry in existing)
                        result.Merged[entry.Key] = entry.Value;
                }
                result.Merged[pair.Key] = pair.Value;
            }
            else if (!ReferenceEquals(current, pair.Value))
            {
                result.Conflicts ??= new List<ushort>();
                result.Conflicts.Add(pair.Key);
            }
        }
        return result;
    }
}
