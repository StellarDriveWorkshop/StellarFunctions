using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using Ships;
using Ships.Cables;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Settings;
using Ships.Services;

namespace StellarFunctions;

internal static class NativeSignalBridge
{
    private const string ShipsSha256 = "9fab668a4f9f5784246444cca799a6495a3d6b2bdd9065d926b3e191da931dfd";
    private static HarmonyLib.Harmony _harmony;

    [ThreadStatic]
    private static SignalPass _currentPass;
    private static SignalPass _lastCompletedPass;
    private static long _nextPassId;

    internal static SignalPass CurrentPass => _currentPass;
    internal static long LastCompletedPassId => _lastCompletedPass?.Id ?? 0;

    internal static int LastPassEvaluationCount(uint shipId, ushort partId) =>
        _lastCompletedPass != null
        && _lastCompletedPass.Evaluations.TryGetValue(new CallKey(shipId, partId), out int count)
            ? count
            : 0;

    internal static void Install(string harmonyId)
    {
        if (_harmony != null)
            return;

        VerifyAssembly(typeof(SignalConnectionsServerUpdater).Assembly, ShipsSha256);

        MethodInfo updateValues = ExactMethod(
            typeof(SignalConnectionsServerUpdater),
            "UpdateSignalValues",
            typeof(void),
            typeof(float)
        );
        MethodInfo updateGroup = ExactMethod(
            typeof(SignalConnectionGroupValues),
            "UpdateGroupSignalValue",
            typeof(void),
            typeof(uint)
        );
        MethodInfo readSocket = ExactMethod(
            typeof(SignalConnectionGroupValues),
            "GetSocketSignal",
            typeof(double),
            typeof(EntityPartSocket)
        );
        MethodInfo buildDefaultState = ExactMethod(
            typeof(PartStateFactory),
            "BuildDefaultState",
            typeof(IStatefulPartState),
            typeof(PartSettings)
        );

        _harmony = new HarmonyLib.Harmony(harmonyId);
        _harmony.Patch(
            buildDefaultState,
            prefix: new HarmonyMethod(typeof(NativeSignalBridge), nameof(BuildCustomPartState))
        );
        _harmony.Patch(
            updateValues,
            prefix: new HarmonyMethod(typeof(NativeSignalBridge), nameof(BeginSignalPass)),
            finalizer: new HarmonyMethod(typeof(NativeSignalBridge), nameof(EndSignalPass))
        );
        _harmony.Patch(
            updateGroup,
            prefix: new HarmonyMethod(typeof(NativeSignalBridge), nameof(EvaluateCallsBeforeOutputGroup))
        );
        _harmony.Patch(readSocket, prefix: new HarmonyMethod(typeof(NativeSignalBridge), nameof(ReadCallOutputSocket)));
        _harmony.Patch(
            AccessTools.Method(typeof(SignalConnectionsServerUpdater), "ExecuteOperations", new[] { typeof(float) }),
            transpiler: new HarmonyMethod(typeof(NativeSignalBridge), nameof(InsertCustomDispatch))
        );
    }

    internal static void Uninstall()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
        _currentPass = null;
        _lastCompletedPass = null;
    }

    private static void VerifyAssembly(Assembly assembly, string expectedSha256)
    {
        string path = assembly.Location;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("Cannot identify loaded native assembly " + assembly.GetName().Name);

        using SHA256 sha = SHA256.Create();
        using FileStream stream = File.OpenRead(path);
        string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Unsupported Ships.dll identity " + actual + "; expected " + expectedSha256
            );
    }

    private static MethodInfo ExactMethod(Type type, string name, Type returnType, params Type[] parameters)
    {
        MethodInfo method = AccessTools.Method(type, name, parameters);
        if (method == null || method.ReturnType != returnType)
            throw new MissingMethodException(
                type.FullName,
                name + "(" + string.Join(",", Array.ConvertAll(parameters, p => p.FullName)) + ")"
            );
        return method;
    }

    private static void BeginSignalPass(float deltaTime, out SignalPass __state)
    {
        NativeFunctionRuntime.Refresh();
        __state = _currentPass;
        _currentPass = new SignalPass(checked(++_nextPassId), deltaTime, __state);
    }

    private static Exception EndSignalPass(Exception __exception, SignalPass __state)
    {
        if (_currentPass != null)
            _lastCompletedPass = _currentPass;
        _currentPass = __state;
        return __exception;
    }

    private static void EvaluateCallsBeforeOutputGroup(SignalConnectionGroupValues __instance, uint groupId)
    {
        SignalPass pass = _currentPass;
        if (pass == null)
            return;

        __instance.GetGroupInputsAndOutputs(groupId, out List<EntityPartSocket> inputs, out _);
        ShipsServerTracker ships = null;
        foreach (EntityPartSocket socket in inputs)
        {
            // Most native groups have no Call output. ServiceLocator searches
            // Unity's tagged objects/components; do that only for socket 3,
            // once per group. Keep live group enumeration and per-pass ordering.
            if (socket.Socket.SocketId != 3)
                continue;
            if (ships == null)
                ships = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
            if (ships == null)
                throw new InvalidOperationException("ShipsServerTracker unavailable during native signal evaluation");
            if (!ships.TryGetTrackedShip(socket.EntityId, out TrackedShipServer ship))
                continue;
            if (
                !ship.TryGetStatefulPart(socket.Socket.PartId, out var part) || part.State is not FunctionCallState call
            )
                continue;
            EvaluateOnce(ship, part, call);
        }
    }

    private static void EvaluateOnce(TrackedShipServer ship, StatefulPart part, FunctionCallState call)
    {
        SignalPass pass = _currentPass;
        var address = new PartContext(ship.Id, part.Id);
        if (pass == null || NativeFunctionRuntime.IsOwned(address))
            return;
        var key = new CallKey(ship.Id, part.Id);
        if (!pass.EvaluatedCalls.Add(key))
            return;
        pass.Evaluations[key] = 1;
        var connections = Core.Services.ServiceLocator.GetService<ConnectionsServerTracker>();
        var inputs = new double[3];
        for (byte i = 0; i < call.InputCount; i++)
            inputs[i] = connections.GetSocketSignalValue(new EntityPartSocket(ship.Id, part.Id, i));
        FunctionCallState next = NativeFunctionRuntime.Evaluate(address, call, inputs, pass.DeltaTime, pass.Id);
        // Do not replicate a steady circuit solely because another tick elapsed.
        if (!Equals(call, next with { LastSignalPass = call.LastSignalPass }))
            ship.UpdatePartState(part.Id, next);
    }

    private static IEnumerable<CodeInstruction> InsertCustomDispatch(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod
    )
    {
        var code = instructions.ToList();
        var locals = __originalMethod.GetMethodBody().LocalVariables;
        var ship = locals.Where(l => l.LocalType == typeof(TrackedShipServer)).ToArray();
        var part = locals.Where(l => l.LocalType == typeof(StatefulPart)).ToArray();
        FieldInfo stateField = AccessTools.Field(typeof(StatefulPart), "State");
        MethodInfo getter = typeof(StatefulPart).GetProperty("State")?.GetGetMethod(true);
        var matches = code.Select((op, index) => new { op, index })
            .Where(x =>
                (stateField != null && x.op.opcode == OpCodes.Ldfld && Equals(x.op.operand, stateField))
                || (getter != null && x.op.Calls(getter))
            )
            .ToArray();
        if (ship.Length != 1 || part.Length != 1 || matches.Length != 1)
            throw new InvalidOperationException("Unsupported native signal dispatcher IL shape");
        code.InsertRange(
            matches[0].index + 1,
            new[]
            {
                new CodeInstruction(OpCodes.Ldloc, ship[0].LocalIndex),
                new CodeInstruction(OpCodes.Ldloc, part[0].LocalIndex),
                new CodeInstruction(
                    OpCodes.Call,
                    AccessTools.Method(typeof(NativeSignalBridge), nameof(DispatchCustomState))
                ),
            }
        );
        return code;
    }

    private sealed class SuppressedState : IStatefulPartState { }

    private static readonly IStatefulPartState Suppressed = new SuppressedState();

    private static IStatefulPartState DispatchCustomState(
        IStatefulPartState state,
        TrackedShipServer ship,
        StatefulPart part
    )
    {
        if (NativeFunctionRuntime.IsOwned(new PartContext(ship.Id, part.Id)))
            return Suppressed;
        if (state is FunctionCallState call)
        {
            EvaluateOnce(ship, part, call);
            return part.State;
        }
        return state;
    }

    private static bool ReadCallOutputSocket(EntityPartSocket entityPartSocket, ref double __result)
    {
        if (NativeFunctionRuntime.IsOwned(entityPartSocket.PartContext))
        {
            __result = 0;
            return false;
        }
        if (entityPartSocket.Socket.SocketId != 3)
            return true;

        ShipsServerTracker ships = Core.Services.ServiceLocator.GetService<ShipsServerTracker>();
        if (
            ships != null
            && ships.TryGetTrackedShip(entityPartSocket.EntityId, out TrackedShipServer ship)
            && ship.TryGetStatefulPart(entityPartSocket.Socket.PartId, out var part)
            && part.State is FunctionCallState call
        )
        {
            __result = call.Output;
            return false;
        }

        return true;
    }

    private static bool BuildCustomPartState(PartSettings __0, ref IStatefulPartState __result)
    {
        Type type = __0 == null ? null : FunctionStateCodec.StateType(__0.id);
        if (type == null)
            return true;
        string configuredType = type.FullName + ", " + type.Assembly.GetName().Name;
        if (__0 == null || !string.Equals(__0.internalStateClass, configuredType, StringComparison.Ordinal))
            return true;

        // The native factory only searches Ships.Interface.Model.Parts.State.
        // Resolve this mod-owned state directly so Call instances can be
        // constructed on both the server and clients.
        __result = (IStatefulPartState)Activator.CreateInstance(type);
        return false;
    }

    internal sealed class SignalPass
    {
        internal readonly long Id;
        internal readonly float DeltaTime;
        internal readonly HashSet<CallKey> EvaluatedCalls = new HashSet<CallKey>();
        internal readonly Dictionary<CallKey, int> Evaluations = new Dictionary<CallKey, int>();
        internal readonly SignalPass Previous;

        internal SignalPass(long id, float deltaTime, SignalPass previous)
        {
            Id = id;
            DeltaTime = deltaTime;
            Previous = previous;
        }
    }

    internal readonly struct CallKey : IEquatable<CallKey>
    {
        private readonly uint _shipId;
        private readonly ushort _partId;

        internal CallKey(uint shipId, ushort partId)
        {
            _shipId = shipId;
            _partId = partId;
        }

        public bool Equals(CallKey other) => _shipId == other._shipId && _partId == other._partId;

        public override bool Equals(object obj) => obj is CallKey other && Equals(other);

        public override int GetHashCode() => unchecked(((int)_shipId * 397) ^ _partId);
    }
}
