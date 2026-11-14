using System.Linq;
using Players;
using Players.Network;
using Players.Services;
using Ships.Interface.Settings;
using UnityEngine;

namespace StellarFunctions.Game;

// Native player/part lookups. Service caching and scene invalidation live
// in Support.NativeServices; part materialization in Support.PartFactory.
public static class PlayerAccess
{
    private static PlayerNetworkState _localState;

    public static PlayerNetworkState LocalNetworkState
    {
        get
        {
            if (_localState == null)
                _localState = UnityEngine
                    .Object.FindObjectsByType<PlayerNetworkState>(FindObjectsSortMode.None)
                    .FirstOrDefault(s => s != null && s.IsOwner);
            return _localState;
        }
    }

    // The server-side tracked player representing the local host client.
    // On a dedicated server there is no local player; callers must tolerate null.
    public static TrackedPlayerServer LocalServerPlayer
    {
        get
        {
            var players = Support.NativeServices.CachedService<PlayersServerTracker>();
            if (players?.ActivePlayers == null || players.ActivePlayers.Count == 0)
                return null;
            PlayerNetworkState state = LocalNetworkState;
            if (state != null)
            {
                var match = players.ActivePlayers.FirstOrDefault(p => p != null && p.Id == state.Id);
                if (match != null)
                    return match;
            }
            return players.ActivePlayers.FirstOrDefault(p => p != null && p.IsHost) ?? players.ActivePlayers[0];
        }
    }

    public static void Invalidate() => _localState = null;
}

// Native part lookup by asset name or id across loaded PartSettings assets.
// The name cache delegates to Support.NativeAssets so both share one
// invalidation path; the numeric id scan stays here because Support has no
// equivalent (and must not substitute a registry-only lookup).
public static class PartLookup
{
    public static PartSettings ByAssetName(string name) => Support.NativeAssets.PartByName(name);

    public static void Invalidate() => Support.NativeAssets.Invalidate();

    public static PartSettings ById(ushort id)
    {
        foreach (var settings in Resources.FindObjectsOfTypeAll<PartSettings>())
            if (settings != null && settings.id == id)
                return settings;
        return null;
    }
}
