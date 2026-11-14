using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using System.Security.Cryptography;
using HarmonyLib;
using MelonLoader;
using Ships;
using Ships.Interface.Model.Parts;
using Ships.Services;

namespace StellarFunctions;

// Optional adapter, pinned to the reviewed MoreMath 1.3.0 build. Discovery and
// hashing occur only at registration; no mod/resource searches in signal ticks.
internal static class MoreMathCompatibility
{
    internal const string ReviewedSha256="c5b09b8552c8982a98a5c74397016319338cbd1de788d17724761e3a6311ab5b";
    private static HarmonyLib.Harmony _harmony;
    internal static bool Ready {get;private set;}
    internal static void Install()
    {
        if(_harmony!=null)return;
        var assembly=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="MoreMath");
        if(assembly==null)return;
        try
        {
            using var sha=SHA256.Create();using var stream=File.OpenRead(assembly.Location);
            string hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
            if(assembly.ManifestModule.ModuleVersionId!=new Guid("f56344e8-7c57-4436-9dd9-41c146e350ae")||hash!=ReviewedSha256)
                throw new NotSupportedException("Requires the reviewed MoreMath 1.3.0 build.");
            var prefix=AccessTools.Method(assembly.GetType("MoreMath.SignalConnectionsServerUpdaterPatch",true),"Prefix");
            if(prefix==null||prefix.ReturnType!=typeof(bool))throw new MissingMethodException("MoreMath dispatcher prefix");
            _harmony=new HarmonyLib.Harmony("stellar.functions.moremath-1.3");
            _harmony.Patch(prefix,transpiler:new HarmonyMethod(typeof(MoreMathCompatibility),nameof(InsertDispatch)));
            Ready=true;
        }
        catch(Exception error)
        {
            _harmony?.UnpatchSelf();_harmony=null;Ready=false;
            MelonLogger.Warning("Functions MoreMath adapter unavailable: "+error.Message);
        }
    }
    private static IEnumerable<CodeInstruction> InsertDispatch(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
    {
        var code=instructions.ToList();var locals=__originalMethod.GetMethodBody().LocalVariables;
        var ships=locals.Where(l=>l.LocalType==typeof(TrackedShipServer)).ToArray();
        var parts=locals.Where(l=>l.LocalType==typeof(StatefulPart)).ToArray();
        var getter=typeof(StatefulPart).GetProperty("State")?.GetGetMethod(true);
        var field=AccessTools.Field(typeof(StatefulPart),"State");
        var reads=code.Select((op,i)=>new{op,i}).Where(x=>(getter!=null&&x.op.Calls(getter))||
            (field!=null&&x.op.opcode==OpCodes.Ldfld&&Equals(x.op.operand,field))).ToArray();
        if(ships.Length!=1||parts.Length!=1||reads.Length!=1)throw new NotSupportedException("MoreMath dispatcher IL changed.");
        code.InsertRange(reads[0].i+1,new[]{new CodeInstruction(OpCodes.Ldloc,ships[0].LocalIndex),
            new CodeInstruction(OpCodes.Ldloc,parts[0].LocalIndex),
            new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(NativeSignalBridge),"DispatchCustomState"))});
        return code;
    }
    internal static void Uninstall(){Ready=false;_harmony?.UnpatchSelf();_harmony=null;}
}
