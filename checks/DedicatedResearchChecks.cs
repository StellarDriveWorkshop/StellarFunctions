// No listener/local player. Real native research state and real SDK update;
// the fixture supplies the authoritative-server flag and native state lookup.
using System;
using System.Linq;
using HarmonyLib;
using Players.Interface.Services;
using Research;
using Research.Interface.Model;
using Research.Interface.Values;
using StellarFunctions.Content;
using StellarFunctions.Game;
using StellarFunctions.Support;
using UnityEngine;

namespace StellarFunctions;
internal static class DedicatedResearchChecks
{
    private static ResearchNetworkedState State;
    private static bool Server=true;
    private static bool ServerFlag(ref bool __result){__result=Server;return false;}
    private static bool FindState(ref ResearchNetworkedState __result){__result=State;return false;}
    internal static void Run(Action<bool,string> check)
    {
        var patch=new HarmonyLib.Harmony("stellar.functions.dedicated-research-regression");
        var owner=new GameObject("Private dedicated research state");owner.SetActive(false);State=owner.AddComponent<ResearchNetworkedState>();
        // ResearchService.IsServer is NativeServices.IsServer, a FishNet-backed
        // static; the fixture supplies the flag on the real update path, and
        // the native state lookup is supplied through the service's state field.
        patch.Patch(AccessTools.PropertyGetter(typeof(NativeServices),"IsServer"),prefix:new HarmonyMethod(typeof(DedicatedResearchChecks),nameof(ServerFlag)));
        patch.Patch(AccessTools.Method(typeof(ResearchService),"FindState"),prefix:new HarmonyMethod(typeof(DedicatedResearchChecks),nameof(FindState)));
        try
        {
            var list=Resources.FindObjectsOfTypeAll<ResearchSettingsList>().FirstOrDefault();
            ushort prereqId=list?.list?.FirstOrDefault(s=>s!=null&&s.techName=="Advanced Signal Processing")?.id??(ushort)0;
            check(prereqId!=0,"fixture resolves the Functions research prerequisite id");
            State.LoadState(new Seed{Researched=new ushort[]{32123}});
            var localPlayers=Support.NativeServices.CachedService<IPlayersProviderClient>();
            check(localPlayers==null||!localPlayers.TryGetLocalPlayerState(out _),"dedicated fixture has no local player");
            check(!State.IsResearched(62008),"existing world begins without the Functions hidden unlock");
            ResearchService.Apply(ResearchSpecs.FromPacks(ContentRegistry.Packs));
            AccessTools.Field(typeof(ResearchService),"_state").SetValue(null,State);
            AccessTools.Field(typeof(ResearchService),"_nextGrant").SetValue(null,0f);
            ResearchService.Tick();
            check(!State.IsResearched(62008),"research tick grants nothing before the prerequisite is researched");
            State.LoadState(new Seed{Researched=new ushort[]{prereqId,32123}});
            var plugin=new Plugin();
            void Tick(){AccessTools.Field(typeof(ResearchService),"_nextGrant").SetValue(null,0f);plugin.OnUpdate();}
            Tick();
            check(State.IsResearched(62008),"actual Functions plugin update grants its unlock on a headless server");
            check(State.Researched.OrderBy(x=>x).SequenceEqual(new ushort[]{prereqId,32123,62008}.OrderBy(x=>x)),"grant preserves unrelated research and adds only Functions");
            Tick();check(State.Researched.Count(id=>id==62008)==1,"repeated server update is idempotent");
            State.LoadState(new Seed{Researched=new ushort[]{32123}});Tick();
            check(!State.IsResearched(62008)&&!State.IsResearched(prereqId),"server does not bypass the research prerequisite");
            State.ServerMarkResearchAsDone(prereqId);Server=false;Tick();
            check(!State.IsResearched(62008),"non-authoritative client cannot grant research");
            Server=true;Tick();check(State.IsResearched(62008),"completing prerequisite later grants Functions without reloading");
            State.LoadState(new Seed{Researched=new ushort[]{prereqId}});Tick();
            check(State.IsResearched(62008),"newly loaded research state is reconciled again");
            State=null;Tick();check(true,"missing/loading research object is harmless");
        }
        finally{patch.UnpatchSelf();AccessTools.Field(typeof(ResearchService),"_state").SetValue(null,null);State=null;Server=true;UnityEngine.Object.DestroyImmediate(owner);}
    }
    private sealed class Seed:IResearchState{public ushort[] Researched{get;set;}}
}
