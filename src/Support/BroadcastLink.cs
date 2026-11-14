using System;
using FishNet;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;

namespace StellarFunctions.Support;

// Reconciles one FishNet broadcast handler with the live NetworkManager.
// FishNet keys broadcasts by typeof(T).FullName, so callers must keep their
// broadcast structs in their original declaring types — relocating or
// renaming the type changes the wire identity even with identical fields.
//
// The link owns only registration lifecycle: discovery of
// InstanceFinder.NetworkManager, unregister-then-register on instance
// change, and uninstall. Payload serialization, recipient selection,
// authentication policy and dispatch stay with the caller.
public sealed class BroadcastLink<T>
    where T : struct, IBroadcast
{
    private readonly bool _serverSide;
    private readonly Action<NetworkConnection, T, Channel> _serverHandler;
    private readonly Action<T, Channel> _clientHandler;
    private readonly bool _requireAuthentication;
    private NetworkManager _network;

    private BroadcastLink(
        bool serverSide,
        Action<NetworkConnection, T, Channel> serverHandler,
        Action<T, Channel> clientHandler,
        bool requireAuthentication
    )
    {
        _serverSide = serverSide;
        _serverHandler = serverHandler;
        _clientHandler = clientHandler;
        _requireAuthentication = requireAuthentication;
    }

    // Server receive path: ServerManager.RegisterBroadcast with the given
    // authentication requirement (matches FishNet's per-connection handler).
    public static BroadcastLink<T> ForServer(
        Action<NetworkConnection, T, Channel> handler,
        bool requireAuthentication = true
    ) => new BroadcastLink<T>(true, handler, null, requireAuthentication);

    // Client receive path: ClientManager.RegisterBroadcast (no auth flag in
    // this FishNet version).
    public static BroadcastLink<T> ForClient(Action<T, Channel> handler) =>
        new BroadcastLink<T>(false, null, handler, false);

    // Reconciles against the current NetworkManager. Returns true exactly on
    // the call that (re)registered on a different manager instance, so
    // callers can reset per-manager state. A missing manager is a no-op that
    // keeps the previous registration target, matching prior behavior.
    public bool Tick()
    {
        NetworkManager next = InstanceFinder.NetworkManager;
        if (next == null || next == _network)
            return false;
        if (_network != null)
            Unregister(_network);
        Register(next);
        _network = next;
        return true;
    }

    public void Uninstall()
    {
        if (_network != null)
            Unregister(_network);
        _network = null;
    }

    private void Register(NetworkManager manager)
    {
        if (_serverSide)
            manager.ServerManager.RegisterBroadcast<T>(_serverHandler, _requireAuthentication);
        else
            manager.ClientManager.RegisterBroadcast<T>(_clientHandler);
    }

    private void Unregister(NetworkManager manager)
    {
        try
        {
            if (_serverSide)
                manager.ServerManager.UnregisterBroadcast<T>(_serverHandler);
            else
                manager.ClientManager.UnregisterBroadcast<T>(_clientHandler);
        }
        catch { }
    }
}
