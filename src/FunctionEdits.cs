using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Players;
using Players.Interface.Model;
using Ships;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Services;
using StellarFunctions.Content;
using UnityEngine;
using WorldTracking.Interface.Services;

namespace StellarFunctions;

// ContentActions identifies the authenticated player. Recheck live reach and
// exact native exclusive-use state here, including callbacks from an open menu.
internal static class FunctionEdits
{
    private sealed class EditRate
    {
        internal float At,
            Tokens;
    }

    private static readonly Dictionary<uint, EditRate> LastEdit = new Dictionary<uint, EditRate>();

    internal static bool CanReach(TrackedPlayerServer player, TrackedShipServer ship, StatefulPart part)
    {
        if (player == null || ship == null || part == null || part.State is not FunctionPartState f || f.IsHeldByPlayer)
            return false;
        var transpose = ServiceLocator.GetService<IWorldTransposeServer>();
        if (transpose == null)
            return false;
        Vector3 target = transpose.Position(ship.RelativeSpaceId, player.SpaceId, part.LocalPosition);
        return float.IsFinite(target.x)
            && float.IsFinite(target.y)
            && float.IsFinite(target.z)
            && float.IsFinite(player.Position.x)
            && float.IsFinite(player.Position.y)
            && float.IsFinite(player.Position.z)
            && (target - player.Position).sqrMagnitude <= 36f;
    }

    internal static bool Authorize(ContentPart part, TrackedPlayerServer player, string action)
    {
        if (
            action == null
            || action.Length > 16384
            || player == null
            || part.Part?.State is not FunctionPartState f
            || !f.IsInUse
        )
            return false;
        PlayerState state = player.State;
        if (
            state.Type != PlayerStateType.InteractingWithChest
            || state.EntityId != part.ShipId
            || state.PartId != part.PartId
        )
            return false;
        var ships = ServiceLocator.GetService<ShipsServerTracker>();
        if (ships == null || !ships.TryGetTrackedShip(part.ShipId, out var ship) || !CanReach(player, ship, part.Part))
            return false;
        float now = Time.realtimeSinceStartup;
        if (!LastEdit.TryGetValue(player.Id, out var rate))
            LastEdit[player.Id] = rate = new EditRate { At = now, Tokens = 4 };
        rate.Tokens = Math.Min(4, rate.Tokens + Math.Max(0, now - rate.At) * 8);
        rate.At = now;
        if (rate.Tokens < 1)
            return false;
        rate.Tokens -= 1;
        return true;
    }

    internal static string Revision(FunctionPartState state) =>
        NativeFunctionCompiler.Hash(
            state is FunctionParamState p
                ? p.ParamId
                    + ":"
                    + p.Name
                    + ":"
                    + p.Index
                    + ":"
                    + p.DefaultValue?.ToString("R", CultureInfo.InvariantCulture)
            : state is FunctionReturnState r ? r.DefinitionId + ":" + r.Name + ":" + r.Sources + ":" + r.Broadcast
            : state.StableId.ToString()
        );

    internal static string Payload(
        FunctionPartState state,
        string name,
        int index,
        string defaultText,
        JObject sources
    ) =>
        "edit-definition:"
        + new JObject
        {
            ["id"] = state.StableId.ToString("N"),
            ["revision"] = Revision(state),
            ["name"] = name,
            ["index"] = index,
            ["default"] = defaultText ?? "",
            ["sources"] = sources ?? new JObject(),
        }.ToString(Formatting.None);

    internal static FunctionPartState Apply(FunctionPartState original, string action)
    {
        if (action == null || !action.StartsWith("edit-definition:", StringComparison.Ordinal))
            throw new FunctionFault("INVALID EDIT");
        JObject edit = NativeFunctionCompiler.ParseObject(action.Substring(16), 16368);
        if ((string)edit["id"] != original.StableId.ToString("N") || (string)edit["revision"] != Revision(original))
            throw new FunctionFault("EDIT CONFLICT: REOPEN");
        string name = ((string)edit["name"] ?? "").Trim();
        FunctionPartState next;
        if (original is FunctionParamState param)
        {
            string text = ((string)edit["default"] ?? "").Trim();
            double? value = null;
            if (text.Length > 64)
                throw new FunctionFault("INVALID DEFAULT");
            if (text.Length > 0)
            {
                if (
                    !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                    || !double.IsFinite(number)
                )
                    throw new FunctionFault("INVALID DEFAULT");
                value = number;
            }
            next = param with { Name = name, Index = (int?)edit["index"] ?? 0, DefaultValue = value };
        }
        else if (original is FunctionReturnState returned)
        {
            if (edit["sources"] is not JObject sources)
                throw new FunctionFault("INVALID SOURCES");
            ValidateSources(sources);
            next = returned with { Name = name, Sources = sources.ToString(Formatting.None) };
        }
        else
            throw new FunctionFault("INVALID EDIT TARGET");
        FunctionStateCodec.Validate(next);
        return next;
    }

    internal static void ValidateSources(JObject sources)
    {
        if (sources.Properties().Any(p => p.Name != "channels" && p.Name != "parts"))
            throw new FunctionFault("INVALID SOURCE KIND");
        int count = 0;
        foreach (JProperty section in sources.Properties())
        {
            if (section.Value is not JObject entries)
                throw new FunctionFault("INVALID SOURCES");
            foreach (JProperty source in entries.Properties())
            {
                if (
                    ++count > 32
                    || source.Name.Length > 64
                    || source.Name.Any(char.IsControl)
                    || source.Value is not JObject target
                    || target.Properties().Any(p => p.Name != "ship" && p.Name != "part" && p.Name != "socket")
                )
                    throw new FunctionFault("INVALID SOURCE");
                if (
                    target["ship"]?.Type != JTokenType.Integer
                    || target["part"]?.Type != JTokenType.Integer
                    || target["socket"]?.Type != JTokenType.Integer
                    || (long)target["ship"] < 0
                    || (long)target["ship"] > uint.MaxValue
                    || (long)target["part"] < 0
                    || (long)target["part"] > ushort.MaxValue
                    || (long)target["socket"] < 0
                    || (long)target["socket"] > byte.MaxValue
                )
                    throw new FunctionFault("INVALID SOURCE ADDRESS");
                if (section.Name == "parts")
                {
                    string[] key = source.Name.Split(':');
                    if (
                        key.Length != 2
                        || !uint.TryParse(key[0], out uint ship)
                        || !ushort.TryParse(key[1], out ushort part)
                        || ship != (uint)target["ship"]
                        || part != (ushort)target["part"]
                    )
                        throw new FunctionFault("SOURCE ADDRESS MISMATCH");
                }
            }
        }
    }

    internal static void Reset() => LastEdit.Clear();
}
