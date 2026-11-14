using System;
using System.Collections.Generic;
using System.Linq;

namespace StellarFunctions;

internal static class FunctionCatalog
{
    private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    internal static void Clear()=>Entries.Clear();
    internal static void Replace(IEnumerable<NativeFunctionDefinition> definitions)
    {
        Entries.Clear();foreach(var def in definitions)Entries[def.State.DefinitionId.ToString("N")]=new Entry(def.State.DefinitionId.ToString("N"),def.State.Name,def.Assembly,NativeFunctionRuntime.Origin(def.Address.ShipId),NativeFunctionRuntime.IsPublished(def));
    }

    internal static IEnumerable<Entry> Visible()
    {
        return Entries.Values
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
    }
    internal static IEnumerable<Entry> Visible(uint ship)=>Visible().Where(entry=>(entry.Assembly==NativeFunctionRuntime.AssemblyOf(ship)||entry.Broadcast));

    internal sealed class Entry
    {
        internal readonly string Id;
        internal readonly string Name;
        internal readonly uint Assembly;
        internal readonly string Origin;
        internal readonly bool Broadcast;

        internal Entry(string id, string name,uint assembly=0,string origin="",bool broadcast=false)
        {
            Id = id;
            Name = name;
            Assembly=assembly;Origin=origin;Broadcast=broadcast;
        }
    }
}
