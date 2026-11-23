using System;
using System.Collections.Generic;
using System.Linq;
using FishNet;
using FishNet.Broadcast;
using FishNet.Managing;
using FishNet.Serializing;
using FishNet.Transporting;
using Newtonsoft.Json;
using Players;
using Players.Network;
using StellarFunctions.Content;
using UnityEngine;

namespace StellarFunctions;

// Menu-only metadata, sent to the authenticated editor, never to every Call tick.
internal static class FunctionDirectory
{
    internal sealed class Entry
    {
        public string Id,
            Name,
            Origin,
            Status,
            Revision,
            Parameters;
        public bool Broadcast,
            Published,
            Local;
    }

    internal sealed class Snapshot
    {
        public string Target,
            Session;
        public long Sequence;
        public bool Eligible,
            Truncated;
        public Entry[] Entries = Array.Empty<Entry>();
        public CallResizeWiring.Confirmation ResizeConfirmation;
    }

    private struct Reply : IBroadcast
    {
        public string Json;
    }

    private static bool _serialized;
    private static readonly Support.BroadcastLink<Reply> _link = Support.BroadcastLink<Reply>.ForClient(Receive);
    private static string _session = "";
    private static Guid _target;
    private static float _next;
    private const float ResponseTimeout = 5f;
    private static float _requestStarted;
    private static bool _requestPending;
    private static long _sequence,
        _accepted;
    internal static Snapshot Current { get; private set; }
    internal static string Session => _session;
    internal static long LastRequestedSequence => _sequence;
    internal static string Status => StatusAt(UnityEngine.Time.unscaledTime);

    internal static string StatusAt(float now)
    {
        if (_requestPending)
        {
            return now - _requestStarted >= ResponseTimeout ? "Server did not respond; retrying"
                : Current != null ? "Refreshing from server…"
                : "Waiting for server…";
        }

        return Current != null ? "Ready" : "Idle";
    }

    internal static void Begin(Guid target)
    {
        _target = target;
        _session = Guid.NewGuid().ToString("N");
        Current = null;
        _next = 0;
        _requestStarted = 0;
        _requestPending = false;
        _sequence = _accepted = 0;
    }

    internal static void Reset()
    {
        _target = Guid.Empty;
        _session = "";
        Current = null;
        _next = 0;
        _requestStarted = 0;
        _requestPending = false;
        _sequence = _accepted = 0;
    }

    // Called after edits that can change the visible directory. Poll sends on
    // the next editor tick, avoiding a one-second wait for the ordinary refresh.
    internal static void RequestRefresh()
    {
        if (_target == Guid.Empty)
        {
            return;
        }

        _next = UnityEngine.Time.unscaledTime + .1f;
        _requestStarted = UnityEngine.Time.unscaledTime;
        _requestPending = true;
    }

    internal static void Poll(ContentPart part)
    {
        EnsureNetworking();
        if (part.Part?.State is not FunctionPartState state)
        {
            return;
        }

        if (_target != state.StableId)
        {
            Begin(state.StableId);
        }

        float now = UnityEngine.Time.unscaledTime;
        if (now < _next)
        {
            return;
        }

        _next = now + 1f;
        if (!_requestPending && Current == null)
        {
            _requestPending = true;
            _requestStarted = now;
        }
        ContentActions.Send(part, "directory:" + _session + ":" + (++_sequence));
    }

    internal static void EnsureNetworking()
    {
        if (!_serialized)
        {
            GenericWriter<Reply>.SetWrite((writer, value) => writer.WriteString(value.Json));
            GenericReader<Reply>.SetRead(reader => new Reply { Json = reader.ReadStringAllocated() });
            _serialized = true;
        }
        // A manager change clears the snapshot: replies in flight belonged
        // to the previous session's transport.
        if (_link.Tick())
        {
            Current = null;
        }
    }

    internal static void Uninstall()
    {
        _link.Uninstall();
        Reset();
    }

    private static void Receive(Reply response, Channel channel) => Accept(response.Json);

    internal static bool Accept(string json)
    {
        if (
            json == null
            || json.Length > 196608
            || !ContentConfigMenu.IsOpen
            || ContentConfigMenu.Current.Part?.State is not FunctionPartState state
            || state.StableId != _target
        )
        {
            return false;
        }

        try
        {
            var data = JsonConvert.DeserializeObject<Snapshot>(
                json,
                new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None, MaxDepth = 8 }
            );
            if (
                data == null
                || data.Target != _target.ToString("N")
                || data.Session != _session
                || data.Sequence < _accepted
                || data.Sequence > _sequence
                || data.Entries == null
                || data.Entries.Length > 256
                || !CallResizeWiring.IsValidConfirmation(data.ResizeConfirmation)
            )
            {
                return false;
            }

            if (
                data.Entries.Any(e =>
                    e == null
                    || !Guid.TryParseExact(e.Id, "N", out var id)
                    || id == Guid.Empty
                    || !Text(e.Name, 64)
                    || !Text(e.Origin, 128)
                    || !Text(e.Status, 64)
                    || !Text(e.Revision, 64)
                    || !Text(e.Parameters, 256)
                )
            )
            {
                return false;
            }

            _accepted = data.Sequence;
            Current = data;
            _requestPending = false;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Text(string value, int max) =>
        value != null && value.Length <= max && !value.Any(char.IsControl);

    internal static Snapshot Build(ContentPart part, string session)
    {
        NativeFunctionRuntime.Refresh();
        var snapshot = new Snapshot
        {
            Target = ((FunctionPartState)part.Part.State).StableId.ToString("N"),
            Session = session,
            Eligible = NativeFunctionRuntime.HasTransmitter(part.ShipId),
        };
        uint assembly = NativeFunctionRuntime.AssemblyOf(part.ShipId);
        IEnumerable<NativeFunctionDefinition> source =
            part.Part.State is FunctionCallState
                ? FunctionCatalog
                    .Visible(part.ShipId)
                    .Select(e => NativeFunctionRuntime.TryDefinition(e.Id, part.ShipId, out var d) ? d : null)
                    .Where(d => d != null)
                : NativeFunctionRuntime.LocalDefinitions(part.ShipId);
        var list = source
            .OrderBy(d => d.Assembly != assembly)
            .ThenBy(d => d.State.Name, StringComparer.Ordinal)
            .ThenBy(d => d.State.DefinitionId)
            .Take(257)
            .ToArray();
        snapshot.Truncated = list.Length > 256;
        snapshot.Entries = list.Take(256)
            .Select(d => new Entry
            {
                Id = d.State.DefinitionId.ToString("N"),
                Name = d.State.Name,
                Origin = NativeFunctionRuntime.Origin(d.Address.ShipId),
                Status = d.Error ?? "TEMPLATE",
                Revision = FunctionEdits.Revision(d.State),
                Parameters =
                    d.Graph == null
                        ? "Invalid template"
                        : string.Join(
                            ", ",
                            d.Graph.Parameters.OrderBy(p => p.Index)
                                .Select(p =>
                                    p.Name
                                    + "="
                                    + (p.Default ?? 0).ToString("G6", System.Globalization.CultureInfo.InvariantCulture)
                                )
                        ),
                Broadcast = d.State.Broadcast,
                Published = NativeFunctionRuntime.IsPublished(d),
                Local = d.Assembly == assembly,
            })
            .ToArray();
        return snapshot;
    }

    // Caller already passed FunctionEdits.Authorize; only public descriptors are returned.
    internal static bool Handle(ContentPart part, TrackedPlayerServer player, string action)
    {
        if (!action.StartsWith("directory:", StringComparison.Ordinal))
        {
            return false;
        }

        var tokens = action.Substring(10).Split(':');
        if (
            tokens.Length != 2
            || !Guid.TryParseExact(tokens[0], "N", out var nonce)
            || nonce == Guid.Empty
            || !long.TryParse(tokens[1], out long sequence)
            || sequence < 1
        )
        {
            return true;
        }

        EnsureNetworking();
        var snapshot = Build(part, nonce.ToString("N"));
        snapshot.Sequence = sequence;
        snapshot.ResizeConfirmation = CallResizeWiring.GetConfirmation(player.Id, part, snapshot.Session);
        var response = new Reply { Json = JsonConvert.SerializeObject(snapshot, Formatting.None) };
        if (response.Json.Length > 196608)
        {
            return true;
        }

        object localClientId = LocalClientId();
        var network = InstanceFinder.NetworkManager;
        if (
            IsLocalHostReply(
                network?.IsServerStarted == true,
                network?.IsClientStarted == true,
                player.Id,
                localClientId
            )
        )
        {
            Accept(response.Json);
        }
        else if (network != null && network.ServerManager.Clients.TryGetValue(player.ConnectionId, out var connection))
        {
            network.ServerManager.Broadcast(connection, response, true, Channel.Reliable);
        }

        return true;
    }

    private static object LocalClientId()
    {
        foreach (var state in UnityEngine.Object.FindObjectsByType<PlayerNetworkState>(FindObjectsSortMode.None))
        {
            if (state != null && state.IsOwner)
            {
                return state.Id;
            }
        }

        return null;
    }

    // LocalServerPlayer falls back to ActivePlayers[0] on a headless server.
    // Only the actual owner client on a listen-server may consume a reply locally.
    internal static bool IsLocalHostReply(
        bool serverStarted,
        bool clientStarted,
        object senderId,
        object localClientId
    ) => serverStarted && clientStarted && senderId != null && localClientId != null && senderId.Equals(localClientId);
}
