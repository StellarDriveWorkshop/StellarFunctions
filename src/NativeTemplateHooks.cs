using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Core.Services;
using HarmonyLib;
using Ships;
using Ships.Cables;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Parts.MemoryRegister;
using Ships.Services;

namespace StellarFunctions;

internal static class NativeTemplateHooks
{
    private static HarmonyLib.Harmony _harmony;

    [ThreadStatic]
    private static int _nativeWriteDepth;
    private static readonly HashSet<PartContext> DependencyWalk = new HashSet<PartContext>();
    private static readonly FieldInfo Reload = AccessTools.Field(
        typeof(SignalConnectionsServerUpdater),
        "_reloadCacheOnNextTick"
    );
    private static MethodInfo _recurse;
    private static Type _operationType;
    private static FieldInfo _kind,
        _part,
        _group;
    private static object _groupKind;
    private static object _partKind;
    private static FieldInfo _queue;
    private static MethodInfo _dequeue;

    internal static void Install()
    {
        if (_harmony != null)
            return;
        _harmony = new HarmonyLib.Harmony("stellar.functions.templates");
        if (Reload == null || Reload.FieldType != typeof(bool))
            throw new MissingFieldException("Native signal cache invalidation");
        MethodInfo execute = AccessTools.Method(
            typeof(SignalConnectionsServerUpdater),
            "ExecuteOperations",
            new[] { typeof(float) }
        );
        MethodInfo update = AccessTools.Method(
            typeof(TrackedShipServer),
            "UpdatePartState",
            new[] { typeof(ushort), typeof(IStatefulPartState) }
        );
        MethodInfo memory = AccessTools.Method(typeof(MemoryRegistersUpdater), "Update", Type.EmptyTypes);
        MethodInfo create = AccessTools.Method(
            typeof(ConnectionsTracker),
            "CreateConnection",
            new[] { typeof(SocketConnection), typeof(CableType) }
        );
        _recurse = AccessTools.Method(typeof(SignalConnectionsServerUpdater), "RecurseQueueEvaluation");
        if (execute == null || update == null || memory == null || create == null || _recurse == null)
            throw new MissingMethodException("Native template integration methods");
        _operationType = _recurse.GetParameters()[0].ParameterType;
        _kind = AccessTools.Field(_operationType, "Operation");
        _part = AccessTools.Field(_operationType, "Part");
        _group = AccessTools.Field(_operationType, "CableGroupId");
        if (
            _kind == null
            || !_kind.FieldType.IsEnum
            || _part?.FieldType != typeof(PartContext)
            || _group?.FieldType != typeof(uint)
        )
            throw new MissingFieldException("Native signal operation shape");
        _groupKind = Enum.Parse(_kind.FieldType, "EvaluateCableGroup");
        _partKind = Enum.Parse(_kind.FieldType, "EvaluatePart");
        _queue = AccessTools.Field(typeof(SignalConnectionsServerUpdater), "_operationsToTraverse");
        _dequeue = _queue == null ? null : AccessTools.Method(_queue.FieldType, "Dequeue", Type.EmptyTypes);
        var build = AccessTools.Method(
            typeof(SignalConnectionsServerUpdater),
            "BuildOperationsStackCache",
            Type.EmptyTypes
        );
        if (_queue == null || _dequeue?.ReturnType != _operationType || build == null)
            throw new MissingMethodException("Native default-only Call scheduling contract");
        _harmony.Patch(build, postfix: new HarmonyMethod(typeof(NativeTemplateHooks), nameof(SeedDefaultOnlyCalls)));
        _harmony.Patch(
            execute,
            prefix: new HarmonyMethod(typeof(NativeTemplateHooks), nameof(BeginNativeWrites)),
            finalizer: new HarmonyMethod(typeof(NativeTemplateHooks), nameof(EndNativeWrites))
        );
        _harmony.Patch(
            memory,
            prefix: new HarmonyMethod(typeof(NativeTemplateHooks), nameof(BeginMemory)),
            finalizer: new HarmonyMethod(typeof(NativeTemplateHooks), nameof(EndNativeWrites))
        );
        _harmony.Patch(
            update,
            prefix: new HarmonyMethod(typeof(NativeTemplateHooks), nameof(FilterNativeWrite)),
            postfix: new HarmonyMethod(typeof(NativeTemplateHooks), nameof(AfterStateUpdate))
        );
        _harmony.Patch(create, prefix: new HarmonyMethod(typeof(NativeTemplateHooks), nameof(FilterRelay)));
        _harmony.Patch(
            _recurse,
            prefix: new HarmonyMethod(typeof(NativeTemplateHooks), nameof(QueueExternalDependencies))
        );
        MethodInfo remove = AccessTools.Method(
            typeof(TrackedShipServer),
            "RemoveStatefulPart",
            new[] { typeof(StatefulPart) }
        );
        if (remove == null)
            throw new MissingMethodException("Native part removal boundary");
        _harmony.Patch(remove, postfix: new HarmonyMethod(typeof(NativeTemplateHooks), nameof(AfterPartRemoval)));
    }

    internal static void Uninstall()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
        _nativeWriteDepth = 0;
        DependencyWalk.Clear();
        NativeFunctionRuntime.Reset();
    }

    internal static void InvalidateSchedule()
    {
        var updater = ServiceLocator.GetService<SignalConnectionsServerUpdater>();
        if (updater != null)
            Reload.SetValue(updater, true);
    }

    private static void AfterPartRemoval() => NativeFunctionRuntime.Dirty();

    private static void SeedDefaultOnlyCalls(SignalConnectionsServerUpdater __instance)
    {
        var ships = ServiceLocator.GetService<ShipsServerTracker>();
        if (ships == null)
            return;
        foreach (var ship in ships.LoadedShipsWithChildren)
        foreach (var part in ship.StatefulParts.Where(p => p.State is FunctionCallState))
        {
            var address = new PartContext(ship.Id, part.Id);
            if (NativeFunctionRuntime.IsOwned(address))
                continue;
            object op = Activator.CreateInstance(_operationType);
            _kind.SetValue(op, _partKind);
            _part.SetValue(op, address);
            _recurse.Invoke(__instance, new[] { op });
        }
        object queue = _queue.GetValue(__instance);
        while (((ICollection)queue).Count > 0)
            _recurse.Invoke(__instance, new[] { _dequeue.Invoke(queue, null) });
    }

    private static void BeginNativeWrites(out int __state)
    {
        __state = _nativeWriteDepth;
        _nativeWriteDepth++;
    }

    private static void BeginMemory(out int __state)
    {
        NativeFunctionRuntime.CommitMemory();
        BeginNativeWrites(out __state);
    }

    private static Exception EndNativeWrites(Exception __exception, int __state)
    {
        _nativeWriteDepth = __state;
        return __exception;
    }

    private static bool FilterNativeWrite(
        TrackedShipServer __instance,
        ushort partId,
        IStatefulPartState state,
        out IStatefulPartState __state
    )
    {
        __state = __instance.TryGetStatefulPart(partId, out var part) ? part.State : null;
        return _nativeWriteDepth == 0
            || state is FunctionPartState
            || !NativeFunctionRuntime.IsOwned(new PartContext(__instance.Id, partId));
    }

    private static void AfterStateUpdate(
        TrackedShipServer __instance,
        ushort partId,
        IStatefulPartState state,
        IStatefulPartState __state,
        bool __runOriginal
    )
    {
        if (__runOriginal)
            NativeFunctionRuntime.NoteStateEdit(__instance.Id, partId, __state, state);
    }

    private static bool FilterRelay(ConnectionsTracker __instance, SocketConnection connection, CableType type)
    {
        if (
            type != CableType.Signal
            || connection.Type != SocketConnectionType.RelayedByTransmitter
            || !NativeFunctionRuntime.IsOurConnectionTracker(__instance)
        )
            return true;
        if (
            (connection.SocketA.HasValue && NativeFunctionRuntime.IsOwned(connection.SocketA.Value.PartContext))
            || (connection.SocketB.HasValue && NativeFunctionRuntime.IsOwned(connection.SocketB.Value.PartContext))
        )
        {
            NativeFunctionRuntime.NoteFilteredRelay(connection);
            return false;
        }
        return true;
    }

    private static void QueueExternalDependencies(SignalConnectionsServerUpdater __instance, object op)
    {
        if (!string.Equals(_kind.GetValue(op).ToString(), "EvaluatePart", StringComparison.Ordinal))
            return;
        PartContext part = (PartContext)_part.GetValue(op);
        if (NativeFunctionRuntime.IsOwned(part) || !DependencyWalk.Add(part))
            return;
        try
        {
            foreach (uint group in NativeFunctionRuntime.ExternalGroups(part))
            {
                object dependency = Activator.CreateInstance(_operationType);
                _kind.SetValue(dependency, _groupKind);
                _group.SetValue(dependency, group);
                _recurse.Invoke(__instance, new[] { dependency });
            }
            foreach (var source in NativeFunctionRuntime.ExternalParts(part))
            {
                object dependency = Activator.CreateInstance(_operationType);
                _kind.SetValue(dependency, _partKind);
                _part.SetValue(dependency, source);
                _recurse.Invoke(__instance, new[] { dependency });
            }
        }
        finally
        {
            DependencyWalk.Remove(part);
        }
    }
}
