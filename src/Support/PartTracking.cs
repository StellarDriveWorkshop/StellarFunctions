using System.Collections.Generic;
using System.Linq;

namespace StellarFunctions.Support;

// Pure per-ship part-presence tracking. Decides which part ids are newly
// present (added edges) and which previously seen ids disappeared (removed
// edges) for one ship. No Unity or game types; checks/core exercises it.
public sealed class PartTracking
{
    private static readonly List<ushort> None = new List<ushort>();
    private readonly Dictionary<uint, HashSet<ushort>> _seen = new Dictionary<uint, HashSet<ushort>>();

    public int ShipCount => _seen.Count;

    public void Clear() => _seen.Clear();

    // True when the ship still has tracked ids awaiting removal events.
    public bool HasPending(uint shipId) => _seen.TryGetValue(shipId, out HashSet<ushort> set) && set.Count != 0;

    // Records partId as present; returns true only on the first observation
    // (the added edge). Repeated marks are suppressed.
    public bool Mark(uint shipId, ushort partId)
    {
        if (!_seen.TryGetValue(shipId, out HashSet<ushort> set))
        {
            _seen[shipId] = set = new HashSet<ushort>();
        }

        return set.Add(partId);
    }

    // Drops every tracked id absent from present and returns the removed ids
    // in observation order. A ship whose set empties is removed entirely so
    // long-running servers do not retain one entry per ship ever seen.
    public IReadOnlyList<ushort> CollectRemoved(uint shipId, IReadOnlyCollection<ushort> present)
    {
        if (!_seen.TryGetValue(shipId, out HashSet<ushort> set) || set.Count == 0)
        {
            return None;
        }

        var removed = set.Where(id => !present.Contains(id)).ToList();
        foreach (ushort id in removed)
        {
            set.Remove(id);
        }
        // Drop emptied ship entries so long-running servers do not retain
        // one set per ship ever seen; a later re-add starts a fresh edge.
        if (set.Count == 0)
        {
            _seen.Remove(shipId);
        }

        return removed;
    }
}
