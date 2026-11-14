using System;
using System.Collections.Generic;

namespace StellarFunctions.Support;

// Pure build-menu placement planner. Decides which tab row a part enters and
// where inside that row, given the native tabsConfig shape. Contains no Unity
// or game types so checks/core exercises the same code the game runs.
public static class MenuPlan
{
    public const int DefaultRowCapacity = 8;

    // tabs -> rows -> parts. Rows whose parts list is null are treated as
    // unusable, matching the native menu's sparse row layout.
    public static bool TryPlace<TRow, TPart>(
        IEnumerable<IReadOnlyList<TRow>> tabs,
        Func<TRow, IReadOnlyList<TPart>> parts,
        Func<TPart, bool> isAnchor,
        int capacity,
        out int tabIndex,
        out int rowIndex,
        out int insertIndex
    )
    {
        tabIndex = -1;
        rowIndex = -1;
        insertIndex = -1;
        if (tabs == null || parts == null || isAnchor == null || capacity <= 0)
            return false;

        var tabList = new List<IReadOnlyList<TRow>>();
        foreach (var tab in tabs)
            if (tab != null)
                tabList.Add(tab);
        if (tabList.Count == 0)
            return false;

        int anchorTab = -1,
            anchorRow = -1;
        for (int t = 0; t < tabList.Count && anchorRow < 0; t++)
        {
            var rows = tabList[t];
            if (rows == null)
                continue;
            for (int r = 0; r < rows.Count; r++)
            {
                var rowParts = parts(rows[r]);
                if (rowParts == null)
                    continue;
                foreach (var part in rowParts)
                    if (isAnchor(part))
                    {
                        anchorTab = t;
                        anchorRow = r;
                        break;
                    }
                if (anchorRow >= 0)
                    break;
            }
        }

        int targetTab = -1,
            targetRow = -1;
        if (anchorRow >= 0)
        {
            var anchorParts = parts(tabList[anchorTab][anchorRow]);
            if (anchorParts != null && anchorParts.Count < capacity)
            {
                targetTab = anchorTab;
                targetRow = anchorRow;
            }
        }
        if (targetRow < 0)
        {
            // Prefer the roomiest row inside the anchor's tab (or the last tab
            // when no anchor exists), then fall back to any row with space.
            int preferredTab = anchorTab >= 0 ? anchorTab : tabList.Count - 1;
            targetTab = FindRoomiest(tabList, parts, capacity, preferredTab, out targetRow);
            if (targetRow < 0)
                targetTab = FindRoomiest(tabList, parts, capacity, -1, out targetRow);
            if (targetRow < 0)
                return false;
        }

        var targetParts = parts(tabList[targetTab][targetRow]);
        insertIndex = targetParts.Count;
        for (int i = 0; i < targetParts.Count; i++)
            if (isAnchor(targetParts[i]))
            {
                insertIndex = i + 1;
                break;
            }
        tabIndex = targetTab;
        rowIndex = targetRow;
        return true;
    }

    private static int FindRoomiest<TRow, TPart>(
        List<IReadOnlyList<TRow>> tabs,
        Func<TRow, IReadOnlyList<TPart>> parts,
        int capacity,
        int onlyTab,
        out int rowIndex
    )
    {
        int bestTab = -1,
            bestRow = -1,
            bestCount = -1;
        for (int t = 0; t < tabs.Count; t++)
        {
            if (onlyTab >= 0 && t != onlyTab)
                continue;
            var rows = tabs[t];
            if (rows == null)
                continue;
            for (int r = 0; r < rows.Count; r++)
            {
                var rowParts = parts(rows[r]);
                if (rowParts == null || rowParts.Count >= capacity)
                    continue;
                // Roomiest first; ties keep the earliest row (matches the
                // previous OrderByDescending(...).FirstOrDefault() behavior).
                if (rowParts.Count > bestCount)
                {
                    bestCount = rowParts.Count;
                    bestTab = t;
                    bestRow = r;
                }
            }
        }
        rowIndex = bestRow;
        return bestTab;
    }
}
