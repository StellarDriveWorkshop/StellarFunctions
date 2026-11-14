using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using FishNet.Serializing;
using HarmonyLib;
using Managers.Server.Network.Serialization;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;

namespace StellarFunctions;

internal static class NativeStateSerialization
{
    private const byte SchemaVersion = 4;
    private const string AssemblyCSharpSha256 = "ee687de85b7894dc21ca3943c5d58a8e19cc2848154c80291f047deca20a1fcf";
    private const string HarmonyId = "stellar.functions.state-serialization";
    private static HarmonyLib.Harmony _harmony;

    internal static void Install()
    {
        if (_harmony != null)
            return;

        VerifyAssembly(typeof(StatefulPartSerialization).Assembly, AssemblyCSharpSha256);
        MethodInfo writeState = ExactMethod("WriteState", typeof(void), typeof(Writer), typeof(IStatefulPartState));
        MethodInfo readNewState = ExactMethod(
            "ReadNewState",
            typeof(IStatefulPartState),
            typeof(IStatefulPartState),
            typeof(Reader)
        );

        _harmony = new HarmonyLib.Harmony(HarmonyId);
        _harmony.Patch(writeState, prefix: new HarmonyMethod(typeof(NativeStateSerialization), nameof(WriteCallState)));
        _harmony.Patch(
            readNewState,
            prefix: new HarmonyMethod(typeof(NativeStateSerialization), nameof(ReadCallState))
        );
    }

    internal static void Uninstall()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
    }

    private static MethodInfo ExactMethod(string name, Type returnType, params Type[] parameters)
    {
        MethodInfo method = AccessTools.Method(typeof(StatefulPartSerialization), name, parameters);
        if (method == null || method.ReturnType != returnType || !method.IsStatic)
            throw new MissingMethodException(
                typeof(StatefulPartSerialization).FullName,
                name + "(" + string.Join(",", Array.ConvertAll(parameters, type => type.FullName)) + ")"
            );
        return method;
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
                "Unsupported Assembly-CSharp.dll identity " + actual + "; expected " + expectedSha256
            );
    }

    private static bool WriteCallState(Writer writer, IStatefulPartState state)
    {
        if (!(state is FunctionPartState custom))
            return true;
        writer.WriteUInt8Unpacked(SchemaVersion);
        writer.WriteString(FunctionStateCodec.Encode(custom, false));
        return false;
    }

    private static bool ReadCallState(IStatefulPartState originalState, Reader reader, ref IStatefulPartState __result)
    {
        if (!(originalState is FunctionPartState))
            return true;

        byte schemaVersion = reader.ReadUInt8Unpacked();
        if (schemaVersion != SchemaVersion)
            throw new InvalidDataException("Unsupported function-part wire schema " + schemaVersion);

        __result = FunctionStateCodec.Decode(reader.ReadStringAllocated(), originalState.GetType(), false);
        return false;
    }
}
