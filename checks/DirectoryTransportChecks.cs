using System;
using System.Reflection;
using UnityEngine;

namespace StellarFunctions;

// Deterministic state-machine checks for a missing/delayed directory reply.
// They do not start a server, add a listener or claim a network integration pass.
internal static class DirectoryTransportChecks
{
    private const BindingFlags StaticPrivate=BindingFlags.Static|BindingFlags.NonPublic;

    internal static void Run(Action<bool,string> check)
    {
        Type directory=typeof(FunctionReturnState).Assembly.GetType("StellarFunctions.FunctionDirectory",true);
        MethodInfo begin=directory.GetMethod("Begin",StaticPrivate);
        MethodInfo reset=directory.GetMethod("Reset",StaticPrivate);
        MethodInfo request=directory.GetMethod("RequestRefresh",StaticPrivate);
        MethodInfo statusAt=directory.GetMethod("StatusAt",StaticPrivate);
        MethodInfo localHostReply=directory.GetMethod("IsLocalHostReply",StaticPrivate);
        PropertyInfo current=directory.GetProperty("Current",StaticPrivate);
        PropertyInfo lastRequestedSequence=directory.GetProperty("LastRequestedSequence",StaticPrivate);
        FieldInfo sequence=directory.GetField("_sequence",StaticPrivate);
        FieldInfo next=directory.GetField("_next",StaticPrivate);
        FieldInfo pending=directory.GetField("_requestPending",StaticPrivate);
        FieldInfo started=directory.GetField("_requestStarted",StaticPrivate);
        try
        {
            begin.Invoke(null,new object[]{Guid.NewGuid()});
            check((string)statusAt.Invoke(null,new object[]{0f})=="Idle","directory starts idle before its first poll");
            request.Invoke(null,null);
            float requestedAt=Time.unscaledTime;
            check(current.GetValue(null)==null&&(float)next.GetValue(null)>=requestedAt+.09f&&(bool)pending.GetValue(null),
                "initial refresh schedules a near-immediate request without a cached directory");
            check(Math.Abs((float)started.GetValue(null)-requestedAt)<0.01f&&
                (string)statusAt.Invoke(null,new object[]{requestedAt+1f})=="Waiting for server…",
                "pending directory request reports waiting before its bounded response timeout");
            check((string)statusAt.Invoke(null,new object[]{requestedAt+5.1f})=="Server did not respond; retrying",
                "missing directory reply becomes visible after five seconds while polling can continue");

            object cached=Activator.CreateInstance(current.PropertyType, true);
            current.SetValue(null,cached);
            request.Invoke(null,null);
            float refreshingAt=Time.unscaledTime;
            check(ReferenceEquals(current.GetValue(null),cached)&&(float)next.GetValue(null)>=refreshingAt+.09f,
                "refresh preserves the last directory while scheduling a near-immediate update");
            check((string)statusAt.Invoke(null,new object[]{refreshingAt+1f})=="Refreshing from server…",
                "refresh status makes the retained directory visibly stale but usable");
            sequence.SetValue(null,23L);
            check((long)lastRequestedSequence.GetValue(null)==23L,
                "UI can read the last sent request sequence when deciding whether cached data is fresh");

            // The native service graph isn't available in this fixture, so this
            // tests the exact predicate used after PlayerAccess.LocalNetworkState
            // is read. On dedicated servers that state is null while LocalServerPlayer
            // falls back to ActivePlayers[0], which can be the remote sender.
            check(!(bool)localHostReply.Invoke(null,new object[]{true,false,"first-remote-player",null}),
                "dedicated server with no local client identity does not consume the first remote player's reply");
            check(!(bool)localHostReply.Invoke(null,new object[]{true,true,"first-remote-player","local-player"}),
                "listen-server routes a remote player's reply through the connection");
            check((bool)localHostReply.Invoke(null,new object[]{true,true,"local-player","local-player"}),
                "listen-server consumes its actual local client's reply locally");
        }
        finally{reset.Invoke(null,null);}
    }
}
