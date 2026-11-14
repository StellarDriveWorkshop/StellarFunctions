// Native research and build-menu regression. No gameplay scene or listener.
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Core.Services;
using HarmonyLib;
using MelonLoader;
using Research;
using Research.Interface.Model;
using Research.Interface.Values;
using StellarFunctions.Content;
using StellarFunctions.Game;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships.Interface.Values;
using Ships.Interface.Model.Parts;
using Ships.Services;
using Tools.Interface.Services;
using UI.BuildMenu;
using UI.Interface.Model;
using UnityEngine;
using UnityEngine.SceneManagement;

[assembly:MelonInfo(typeof(MenuResearchProbe),"Private Functions Menu Research Probe","0.0.0","test",null)]
[assembly:MelonGame("CuriousOwlGames","StellarDrive")]
[assembly:MelonPriority(-10000)]
public sealed class MenuResearchProbe:MelonMod
{
    private string _output;private AsyncOperation _scene;private bool _done;private int _checks;private float _uiDeadline=-1f;
    private static readonly ushort[] Ids={62005,62006,62007,62009};
    public override void OnInitializeMelon()
    {
        var pointer=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"menu-probe-output.txt");
        _output=File.Exists(pointer)?File.ReadAllText(pointer).Trim():Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"menu-probe-output");
        Directory.CreateDirectory(_output);
        HarmonyInstance.Patch(AccessTools.Method(AccessTools.TypeByName("SteamManager"),"Awake"),prefix:new HarmonyMethod(typeof(MenuResearchProbe),nameof(NoSteam)));
    }
    private static bool NoSteam()=>false;
    private static bool ServerFlag(ref bool __result){__result=true;return false;}
    public override void OnUpdate()
    {
        if(_done||Time.realtimeSinceStartup<6)return;
        try
        {
            if(_scene==null){_scene=SceneManager.LoadSceneAsync("MainGame",LoadSceneMode.Additive);_scene.allowSceneActivation=false;return;}
            if(_scene.progress<.9f){if(Time.realtimeSinceStartup>40)throw new TimeoutException("Assets unavailable");return;}
            // The research canvas spawns asynchronously after the additive
            // scene reports ready; give it a bounded window before judging.
            if(_uiDeadline<0f)_uiDeadline=Time.realtimeSinceStartup+20f;
            if(Time.realtimeSinceStartup<_uiDeadline
                &&!Resources.FindObjectsOfTypeAll<UI.Research.ResearchCanvas>().Any()
                &&!Resources.FindObjectsOfTypeAll<UI.Research.ResearchInfo>().Any())return;
            _done=true;Run();File.WriteAllText(Path.Combine(_output,"success.txt"),"PASS "+_checks+" native research/menu checks");Application.Quit();
        }
        catch(Exception error){_done=true;File.WriteAllText(Path.Combine(_output,"failure.txt"),error.ToString());Application.Quit(1);}
    }
    private void Check(bool ok,string message){if(!ok)throw new Exception(message);_checks++;File.AppendAllText(Path.Combine(_output,"assertions.txt"),"PASS "+message+Environment.NewLine);}
    private void Run()
    {
        StellarFunctions.ReleaseMetadataChecks.Run(Check);
        Check(!SceneManager.GetSceneByName("MainGame").isLoaded,"gameplay scene remains unactivated; no world/listener started");
        // Bootstrap failure/retry: a throwing or false-reporting bootstrap
        // must block materialization observably, and restoring the real
        // delegate must recover on the next attempt without duplicating
        // wiring.
        var bootstrapField=typeof(StellarFunctions.Support.Registration).GetField("Bootstrap",BindingFlags.Public|BindingFlags.Static);
        var bootstrapState=AccessTools.Field(typeof(StellarFunctions.Support.Registration),"_bootstrap");
        var readyProp=typeof(StellarFunctions.Support.Registration).GetProperty("BootstrapReady",BindingFlags.Public|BindingFlags.Static);
        var realBootstrap=(Func<bool>)bootstrapField.GetValue(null);
        bootstrapState.SetValue(null,0);
        bootstrapField.SetValue(null,(Func<bool>)(()=>false));
        StellarFunctions.Support.Registration.EnsureMaterialized();
        Check(!(bool)readyProp.GetValue(null),"false-reporting bootstrap reports BootstrapReady=false and blocks materialization");
        bootstrapState.SetValue(null,0);
        bootstrapField.SetValue(null,(Func<bool>)(()=>throw new InvalidOperationException("probe bootstrap failure")));
        StellarFunctions.Support.Registration.EnsureMaterialized();
        Check(!(bool)readyProp.GetValue(null),"failed bootstrap reports BootstrapReady=false and blocks materialization");
        bootstrapField.SetValue(null,realBootstrap);
        StellarFunctions.Support.Registration.EnsureMaterialized();
        Check((bool)readyProp.GetValue(null),"bootstrap retry succeeds and latches BootstrapReady=true");
        StellarFunctions.Support.Registration.EnsureMaterialized();StellarFunctions.Support.ResearchService.Apply(StellarFunctions.Content.ResearchSpecs.FromPacks(ContentRegistry.Packs));
        var beforeStart=typeof(StellarFunctions.Support.MenuCatalog).GetMethod("BeforeStart",BindingFlags.Public|BindingFlags.Static);
        Check(beforeStart!=null&&beforeStart.GetParameters().FirstOrDefault()?.Name=="__instance","MenuCatalog.BeforeStart preserves the Harmony __instance injection signature");
        Check(StellarFunctions.Support.PartFactory.Holder!=null&&StellarFunctions.Support.PartFactory.Holder.gameObject.scene.name=="DontDestroyOnLoad","prefab holder lives under DontDestroyOnLoad for world replacement");
        var sizing=typeof(StellarFunctions.FunctionCallState).Assembly.GetType("StellarFunctions.CallSizing");
        Check(sizing!=null&&(bool)AccessTools.Property(sizing,"Ready").GetValue(null),"all native Call sizing lifecycle hooks installed successfully");
        var updateParts=AccessTools.Method(typeof(Ships.TrackedShipServer),"UpdateParts",new Type[]{typeof(float)});
        var patchInfo=updateParts==null?null:HarmonyLib.Harmony.GetPatchInfo(updateParts);
        var owners=patchInfo==null?new List<Patch>(0):patchInfo.Postfixes.ToList();
        int support=owners.Count(p=>p.owner=="stellarfunctions.support.runtime");
        int legacy=owners.Count(p=>p.owner=="stellar.functions.kit.runtime");
        Check(support==1&&legacy==0,"exactly one support-owned lifecycle dispatcher owns TrackedShipServer.UpdateParts");
        StellarFunctions.Support.BehaviourHost.Uninstall();
        var afterUninstall=updateParts==null?null:HarmonyLib.Harmony.GetPatchInfo(updateParts);
        int supportAfterUninstall=afterUninstall==null?0:afterUninstall.Postfixes.Count(p=>p.owner=="stellarfunctions.support.runtime");
        Check(supportAfterUninstall==0,"BehaviourHost.Uninstall removes only its own UpdateParts postfix");
        StellarFunctions.Support.BehaviourHost.Bind(65000,new LifecycleProbeBehaviour());
        var afterRebind=updateParts==null?null:HarmonyLib.Harmony.GetPatchInfo(updateParts);
        int supportAfterRebind=afterRebind==null?0:afterRebind.Postfixes.Count(p=>p.owner=="stellarfunctions.support.runtime");
        Check(supportAfterRebind==1,"re-binding after uninstall reinstalls exactly one dispatcher postfix");
        StellarFunctions.Support.BehaviourHost.Unbind(65000);
        var actionBroadcast=typeof(StellarFunctions.Content.ContentActions).GetNestedType("ActionBroadcast",BindingFlags.NonPublic);
        var directoryType=typeof(StellarFunctions.Plugin).Assembly.GetType("StellarFunctions.FunctionDirectory");
        var directoryReply=directoryType?.GetNestedType("Reply",BindingFlags.NonPublic);
        Check(actionBroadcast!=null&&actionBroadcast.FullName=="StellarFunctions.Content.ContentActions+ActionBroadcast"&&directoryReply!=null&&directoryReply.FullName=="StellarFunctions.FunctionDirectory+Reply","broadcast wire identities keep their original declaring types");
        object actionWriter=typeof(FishNet.Serializing.GenericWriter<>).MakeGenericType(actionBroadcast).GetProperty("Write")?.GetValue(null);
        object actionReader=typeof(FishNet.Serializing.GenericReader<>).MakeGenericType(actionBroadcast).GetProperty("Read")?.GetValue(null);
        object replyWriter=typeof(FishNet.Serializing.GenericWriter<>).MakeGenericType(directoryReply).GetProperty("Write")?.GetValue(null);
        object replyReader=typeof(FishNet.Serializing.GenericReader<>).MakeGenericType(directoryReply).GetProperty("Read")?.GetValue(null);
        Check(actionWriter!=null&&actionReader!=null&&replyWriter!=null&&replyReader!=null,"action and directory broadcast serializers remain registered");
        var actionsLink=typeof(StellarFunctions.Content.ContentActions).GetField("_link",BindingFlags.NonPublic|BindingFlags.Static)?.GetValue(null);
        var linkNetwork=actionsLink?.GetType().GetField("_network",BindingFlags.NonPublic|BindingFlags.Instance);
        StellarFunctions.Content.ContentActions.Install();
        StellarFunctions.Content.ContentActions.Uninstall();
        StellarFunctions.Content.ContentActions.Install();
        StellarFunctions.Content.ContentActions.EnsureNetworking();
        Check(actionsLink!=null&&linkNetwork!=null&&linkNetwork.GetValue(actionsLink)==null,"repeated action install/uninstall cycles leave no manager registration without a NetworkManager");
        // Stage 5A facade migration: the registration bootstrap delegate is
        // installed and the shared lookups preserve name/id resolution and
        // invalidation. The temporary screen-hook adapter is gone: part
        // visuals call Support.Screens directly and surfaces are the
        // Support.ScreenSurface type itself.
        var bootstrap=(Action)typeof(StellarFunctions.Support.Registration).GetField("Bootstrap",BindingFlags.Public|BindingFlags.Static)?.GetValue(null);
        Check(bootstrap!=null&&bootstrap.Method.DeclaringType!=null&&bootstrap.Method.DeclaringType.Name=="SupportLink"&&bootstrap.Method.Name=="EnsureInstalled","registration bootstrap delegate points at the Functions SupportLink");
        Check(typeof(StellarFunctions.Plugin).Assembly.GetType("StellarFunctions.Support.ScreenHooks")==null,"temporary ScreenHooks adapter is removed");
        Check(ReferenceEquals(StellarFunctions.Game.PartLookup.ByAssetName("StellarFunctionParam"),StellarFunctions.Support.NativeAssets.PartByName("StellarFunctionParam")),"asset-name lookup delegates to the shared Support cache");
        Check(StellarFunctions.Game.PartLookup.ByAssetName("StellarFunctionParam")!=null&&StellarFunctions.Game.PartLookup.ById(62005)?.id==62005,"name and numeric lookups resolve function parts");
        StellarFunctions.Game.PartLookup.Invalidate();
        Check(StellarFunctions.Game.PartLookup.ByAssetName("StellarFunctionParam")!=null,"name lookup repopulates after invalidation");
        Check(ContentRuntime.Of(62005) is PartBehaviour,"ContentRuntime.Of still returns the original PartBehaviour through the adapter");
        // Support-owned screen infrastructure: builders emit the SDScreen
        // name and the Support surface component directly.
        var probeParent=new GameObject("probe-screen-parent").transform;
        var screenMesh=new StellarFunctions.Support.MeshSpec{Screen="probe.channel",Columns=20,Speed=8f,Scale=Vector3.one};
        var supportScreen=StellarFunctions.Support.Screens.BuildScreen("probe",probeParent,screenMesh,null);
        Check(supportScreen.name=="SDScreen|probe.channel|20|8"&&supportScreen.GetComponent<StellarFunctions.Support.ScreenSurface>()!=null,"Support builder emits the SDScreen name and the Support surface component");
        StellarFunctions.Support.Screens.Provide("probe.channel",ProbeFrame);
        var provided=StellarFunctions.Support.Screens.BuildScreen("probe2",probeParent,screenMesh,null);
        var providedLabel=provided.GetComponentInChildren<TMPro.TextMeshPro>(true);
        Check(providedLabel!=null&&providedLabel.text=="PROBE OK","Support screen polls its registered provider");
        StellarFunctions.Support.Screens.Provide("probe.scroll",ScrollingProbeFrame);
        var scrolling=StellarFunctions.Support.Screens.BuildScreen("probe3",probeParent,new StellarFunctions.Support.MeshSpec{Screen="probe.scroll",Columns=16,Speed=6f,Scale=Vector3.one},null);
        var scrollingLabel=scrolling.GetComponentInChildren<TMPro.TextMeshPro>(true);
        Check(scrollingLabel!=null&&scrollingLabel.text.StartsWith("<mspace=0.62em>"),"scrolling provider renders monospace ticker markup");
        Check(StellarFunctions.Support.Screens.Unprovide("probe.channel",ProbeFrame),"Unprovide removes a provider by equivalent delegate");
        var unprovided=StellarFunctions.Support.Screens.BuildScreen("probe4",probeParent,screenMesh,null);
        Check(unprovided.GetComponentInChildren<TMPro.TextMeshPro>(true).text.Length==0,"unregistered channel leaves the screen blank");
        StellarFunctions.Support.Screens.Provide("probe.channel",ProbeFrame);
        StellarFunctions.Support.Screens.Provide("probe.channel",OtherProbeFrame);
        Check(!StellarFunctions.Support.Screens.Unprovide("probe.channel",(Func<GameObject,StellarFunctions.Support.ScreenFrame>)ProbeFrame),"stale provider cannot unregister its replacement");
        Check(StellarFunctions.Support.Screens.Unprovide("probe.channel",OtherProbeFrame),"current provider unregisters itself");
        StellarFunctions.Support.Screens.Provide("probe.bad",ThrowingProbeFrame);
        var bad=StellarFunctions.Support.Screens.BuildScreen("probe5",probeParent,new StellarFunctions.Support.MeshSpec{Screen="probe.bad",Columns=16,Speed=6f,Scale=Vector3.one},null);
        Check(bad!=null&&bad.GetComponentInChildren<TMPro.TextMeshPro>(true).text.Length==0,"throwing provider is isolated from screen construction");
        // Release logging floor: warnings/errors visible by default. The
        // diagnostics flag is deterministic — it must equal the documented
        // opt-in detection (env var or marker file beside the DLL) whether
        // or not this process deliberately enabled diagnostics.
        string diagEnv=Environment.GetEnvironmentVariable("STELLARFUNCTIONS_DIAGNOSTICS");
        bool expectDiag=!string.IsNullOrEmpty(diagEnv)&&(diagEnv=="1"||string.Equals(diagEnv,"true",StringComparison.OrdinalIgnoreCase)||string.Equals(diagEnv,"yes",StringComparison.OrdinalIgnoreCase));
        string dllDir=Path.GetDirectoryName(typeof(StellarFunctions.Plugin).Assembly.Location);
        expectDiag=expectDiag||(dllDir!=null&&File.Exists(Path.Combine(dllDir,"StellarFunctions.diagnostics")));
        Check(StellarFunctions.Support.NativeServices.Floor==StellarFunctions.Support.LogGate.Warn&&StellarFunctions.Support.NativeServices.DiagnosticsEnabled==expectDiag,"support logging keeps the warn floor and diagnostics matches its documented opt-in state");
        var configure=typeof(StellarFunctions.Plugin).Assembly.GetType("StellarFunctions.FunctionScreens")?.GetMethod("ConfigureCallFace",BindingFlags.NonPublic|BindingFlags.Static);
        Check(configure!=null,"FunctionScreens.ConfigureCallFace remains available for call-face metrics");
        if(configure!=null)
        {
            var faceGo=StellarFunctions.Support.Screens.BuildScreen("face",probeParent,new StellarFunctions.Support.MeshSpec{Screen="probe.face",Columns=16,Speed=6f,Scale=Vector3.one},null);
            var faceScreen=faceGo.GetComponent<StellarFunctions.Support.ScreenSurface>();
            var faceLabel=faceScreen.GetComponentInChildren<TMPro.TextMeshPro>(true);
            foreach(var pair in new[]{Tuple.Create(1,new Vector2(.118f,.1f)),Tuple.Create(2,new Vector2(.150f,.080f)),Tuple.Create(3,new Vector2(.240f,.080f))})
            {
                configure.Invoke(null,new object[]{faceScreen,pair.Item1});
                Check(!faceLabel.enableAutoSizing&&!faceLabel.richText&&faceLabel.textWrappingMode==TMPro.TextWrappingModes.NoWrap&&faceLabel.overflowMode==TMPro.TextOverflowModes.Truncate&&Mathf.Abs(faceLabel.rectTransform.sizeDelta.x-pair.Item2.x)<.0001f&&Mathf.Abs(faceLabel.rectTransform.sizeDelta.y-pair.Item2.y)<.0001f,"call face "+pair.Item1+"-input metrics applied through the Support surface");
            }
        }
        foreach(var id in Ids)
        {
            var settings=StellarFunctions.Support.Registration.Get(id);
            var surfaces=settings!=null&&settings.part!=null?settings.part.GetComponentsInChildren<StellarFunctions.Support.ScreenSurface>(true):new StellarFunctions.Support.ScreenSurface[0];
            Check(surfaces.Length>0&&surfaces.All(s=>StellarFunctions.Support.ScreenText.ParseName(s.name,out var ch,out _,out _)&&ch=="stellar-functions.status"),"part "+id+" prefab carries Support ScreenSurface displays on the status channel");
        }
        // Issue #1 diagnostic runs first: a later check failure must not cost the audit.
        StellarFunctions.CallSocketAuditChecks.Run(Check);
        StellarFunctions.CallCatalogueChecks.Run(Check);
        StellarFunctions.SourcePresentationChecks.Run(Check);
        StellarFunctions.DirectoryTransportChecks.Run(Check);
        DumpDonors();
        StellarFunctions.TransmitterCodecChecks.Run(Check);
        StellarFunctions.CallSizingCodecChecks.Run(Check);
        StellarFunctions.CallSizingNativeChecks.Run(Check);
        StellarFunctions.CallSizingAuthorityChecks.Run(Check);
        // Dedicated-context check last among sizing checks: it requires a
        // server flag a solo client cannot provide.
        StellarFunctions.DedicatedResearchChecks.Run(Check);
        StellarFunctions.CallSizingDefaultsChecks.Run(Check);
        StellarFunctions.NestedBroadcastLifecycleChecks.Run(Check);
        StellarFunctions.MoreMathLifecycleChecks.Run(Check);
        StellarFunctions.MoreMathAdapterChecks.Run(Check);
            StellarFunctions.BroadcastStateChecks.Run(Check);
            StellarFunctions.TransmitterRuntimeChecks.Run(Check);
            TransmitterAssetChecks.Run(_output);
        if(File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-redesign.txt")))StellarFunctions.UiRedesignChecks.Run(_output,Check);
        var parts=Ids.Select(id=>StellarFunctions.Support.Registration.Get(id)).ToArray();Check(parts.All(p=>p!=null),"all four real function settings exist");
        var list=Resources.FindObjectsOfTypeAll<ResearchSettingsList>().First();
        ushort prereqId=list.list.FirstOrDefault(r=>r!=null&&r.techName=="Advanced Signal Processing")?.id??(ushort)0;
        Check(prereqId!=0,"fixture resolves the Functions research prerequisite id");
        var parent=list.list.Single(r=>r.id==1);File.WriteAllText(Path.Combine(_output,"grant-parent.txt"),parent.id+" "+parent.techName);
        var owner=new GameObject("Private research seed");owner.SetActive(false);
        var state=owner.AddComponent<ResearchNetworkedState>();var proxy=owner.AddComponent<ResearchClientProxy>();
        AccessTools.Field(typeof(ResearchClientProxy),"researchSettingsList").SetValue(proxy,list);proxy.Setup(state);
        // Seed real native state, not an always-unlocked research provider.
        state.LoadState(new Seed{Researched=list.list.Where(r=>r.id!=62008&&r.id!=prereqId).Select(r=>r.id).ToArray()});
        Check(parts.All(p=>!proxy.IsPartUnlocked(p)),"native research provider initially hides ungranted function parts");
        bool fixedBuild=File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"expect-unlock.txt"));
        if(fixedBuild)
        {
            var research=list.list.SingleOrDefault(r=>r.id==62008);
            Check(research!=null&&research.hideFromTechTree&&research.unlockedParts.Select(p=>p.id).OrderBy(id=>id).SequenceEqual(Ids),"hidden function research maps exactly Param Return Call Transmitter");
            HarmonyInstance.Patch(AccessTools.PropertyGetter(typeof(StellarFunctions.Support.NativeServices),"IsServer"),prefix:new HarmonyMethod(typeof(MenuResearchProbe),nameof(ServerFlag)));
            AccessTools.Field(typeof(StellarFunctions.Support.ResearchService),"_state").SetValue(null,state);AccessTools.Field(typeof(StellarFunctions.Support.ResearchService),"_nextGrant").SetValue(null,0f);
            state.ServerMarkResearchAsDone(prereqId);StellarFunctions.Support.ResearchService.Tick();
            Check(state.IsResearched(62008),"actual SDK grant tick unlocks functions in an existing researched world");
            Check(parts.All(proxy.IsPartUnlocked),"actual native research provider unlocks every function part");
            state.LoadState(new Seed{Researched=Array.Empty<ushort>()});AccessTools.Field(typeof(StellarFunctions.Support.ResearchService),"_nextGrant").SetValue(null,0f);StellarFunctions.Support.ResearchService.Tick();
            Check(!state.IsResearched(62008)&&parts.All(p=>!proxy.IsPartUnlocked(p)),"unresearched prerequisite is respected");
            state.ServerMarkResearchAsDone(prereqId);AccessTools.Field(typeof(StellarFunctions.Support.ResearchService),"_nextGrant").SetValue(null,0f);StellarFunctions.Support.ResearchService.Tick();
            Check(parts.All(proxy.IsPartUnlocked),"research completion grants the parts without replacing the world");
            HarmonyInstance.Unpatch(AccessTools.PropertyGetter(typeof(StellarFunctions.Support.NativeServices),"IsServer"),typeof(MenuResearchProbe).GetMethod(nameof(ServerFlag),BindingFlags.Static|BindingFlags.NonPublic));
        }
        // Research-table unlock preview: the real native ResearchInfo.Show
        // populates TechUnlockItem rows straight from unlockedParts, and the
        // thumbnail pipeline resolves each part id through PartSettingsProvider.
        var asp=list.list.FirstOrDefault(r=>r!=null&&r.techName=="Advanced Signal Processing");
        Check(asp!=null,"native Advanced Signal Processing tech exists for preview checks");
        Check(asp.unlockedParts!=null&&Ids.All(id=>asp.unlockedParts.Count(p=>p!=null&&p.id==id)==1),"prerequisite tech previews each function part exactly once");
        UI.Research.ResearchInfo infoSource=Resources.FindObjectsOfTypeAll<UI.Research.ResearchInfo>().FirstOrDefault(i=>i!=null);
        if(infoSource==null)
        {
            // The info panel may only exist inside the research canvas prefab.
            var canvas=Resources.FindObjectsOfTypeAll<UI.Research.ResearchCanvas>().FirstOrDefault(c=>c!=null);
            if(canvas!=null)infoSource=(UI.Research.ResearchInfo)AccessTools.Field(typeof(UI.Research.ResearchCanvas),"info").GetValue(canvas);
        }
        if(infoSource==null)
        {
            var candidates=Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(m=>m!=null&&m.GetType().FullName!=null&&m.GetType().FullName.Contains("Research")).Select(m=>m.GetType().FullName+" "+m.name).Distinct().ToArray();
            File.WriteAllLines(Path.Combine(_output,"research-ui-candidates.txt"),candidates);
        }
        Check(infoSource!=null,"native ResearchInfo available for preview population");
        var thumbnailCalls=new List<ushort>();RecordTarget=thumbnailCalls;
        var svcOwner=new GameObject("Private research services");svcOwner.tag="ServiceObject";
        var svcThumbs=svcOwner.AddComponent<ShipPartsThumbnailRuntimeCache>();
        AccessTools.Field(typeof(ShipPartsThumbnailRuntimeCache),"_thumbnailsResProvider").SetValue(svcThumbs,new Resolution());
        var renderMethod=AccessTools.Method(typeof(ShipPartsThumbnailRuntimeCache),"RenderPartThumbnail");
        HarmonyInstance.Patch(renderMethod,prefix:new HarmonyMethod(typeof(MenuResearchProbe),nameof(RecordThumbnail)));
        var guideProvider=Resources.FindObjectsOfTypeAll<UI.Guide.GuideEntriesProvider>().FirstOrDefault();
        var infoOwner=new GameObject("Private research info");infoOwner.SetActive(false);
        var info=UnityEngine.Object.Instantiate(infoSource,infoOwner.transform);
        AccessTools.Field(typeof(UI.Research.ResearchInfo),"_researchProvider").SetValue(info,proxy);
        if(guideProvider!=null)AccessTools.Field(typeof(UI.Research.ResearchInfo),"_guideEntriesProvider").SetValue(info,guideProvider);
        HarmonyInstance.Patch(AccessTools.Method(typeof(ResearchClientProxy),"CanResearch"),prefix:new HarmonyMethod(typeof(MenuResearchProbe),nameof(AllowResearch)));
        string showError=null;
        try{AccessTools.Method(typeof(UI.Research.ResearchInfo),"Show").Invoke(info,new object[]{asp});}
        catch(Exception error){showError=error.ToString();File.AppendAllText(Path.Combine(_output,"assertions.txt"),"NOTE research preview Show threw: "+error.Message+Environment.NewLine);}
        HarmonyInstance.Unpatch(AccessTools.Method(typeof(ResearchClientProxy),"CanResearch"),typeof(MenuResearchProbe).GetMethod(nameof(AllowResearch),BindingFlags.Static|BindingFlags.NonPublic));
        Check(showError==null,"native research preview Show completed without errors");
        var previewIds=info.GetComponentsInChildren<UI.Research.TechUnlockItem>(true).Where(i=>i.PartSettings!=null).Select(i=>i.PartSettings.id).OrderBy(x=>x).ToArray();
        var expectedIds=asp.unlockedParts.Where(p=>p!=null).Select(p=>p.id).OrderBy(x=>x).ToArray();
        Check(previewIds.SequenceEqual(expectedIds),"native research preview lists exactly the tech's unlockedParts");
        Check(Ids.All(id=>previewIds.Count(x=>x==id)==1),"native research preview shows each function part exactly once");
        var tasks=(System.Collections.IEnumerable)AccessTools.Field(typeof(ShipPartsThumbnailRuntimeCache),"_tasks").GetValue(svcThumbs);
        foreach(var task in tasks){var pid=(ushort)task.GetType().GetField("PartId").GetValue(task);renderMethod.Invoke(svcThumbs,new object[]{pid,null});}
        Check(Ids.All(id=>thumbnailCalls.Contains(id)),"thumbnail pipeline requested every function part id");
        // Restore the real renderer before the optional real-render attempt.
        HarmonyInstance.Unpatch(renderMethod,typeof(MenuResearchProbe).GetMethod(nameof(RecordThumbnail),BindingFlags.Static|BindingFlags.NonPublic));
        File.WriteAllLines(Path.Combine(_output,"preview-parts.txt"),previewIds.Select(x=>x.ToString()));
        // Repeated Apply/Revert cycles keep exactly one entry; a foreign
        // unlock addition to the same native tech survives reapplication.
        Check(StellarFunctions.Support.ResearchService.All.Count==1&&ReferenceEquals(StellarFunctions.Support.ResearchService.AppliedList,list),"research service tracks exactly its own applied entry");
        StellarFunctions.Support.ResearchService.Apply(StellarFunctions.Content.ResearchSpecs.FromPacks(ContentRegistry.Packs));
        Check(list.list.Count(r=>r!=null&&r.id==62008)==1,"repeated research apply keeps exactly one hidden entry");
        // Foreign fixture: a genuinely absent PartSettings reference (not one
        // of ours, not already in the prerequisite's unlocks), verified absent
        // before insertion so restoration cannot drop a preexisting entry.
        var beforeForeign=asp.unlockedParts??Array.Empty<PartSettings>();
        var foreign=Resources.FindObjectsOfTypeAll<PartSettings>().FirstOrDefault(p=>p!=null&&p.id!=0&&!Ids.Contains(p.id)&&!beforeForeign.Contains(p));
        if(foreign!=null)
        {
            Check(!beforeForeign.Contains(foreign),"foreign fixture part verified absent from prerequisite unlocks before insertion");
            var prior=asp.unlockedParts.ToArray();
            asp.unlockedParts=asp.unlockedParts.Concat(new[]{foreign}).ToArray();
            StellarFunctions.Support.ResearchService.Apply(StellarFunctions.Content.ResearchSpecs.FromPacks(ContentRegistry.Packs));
            Check(asp.unlockedParts.Contains(foreign),"foreign unlock addition survives research reapplication");
            Check(Ids.All(id=>asp.unlockedParts.Count(p=>p!=null&&p.id==id)==1),"our parts still merged exactly once after foreign interleave");
            asp.unlockedParts=prior;
            StellarFunctions.Support.ResearchService.Apply(StellarFunctions.Content.ResearchSpecs.FromPacks(ContentRegistry.Packs));
            Check(asp.unlockedParts.SequenceEqual(prior),"reapplication restores the exact prior unlock array");
        }
        // Late-prerequisite regression: a grantedByName tech absent at apply
        // time must still produce both the grant mapping and the merged
        // unlock preview once the prerequisite appears later.
        var lateSpec=new StellarFunctions.Support.TechSpec{Id=65001,Name="Late Grant Probe",Hidden=true,GrantedByName="Late Grant Prereq",UnlockParts=new[]{"StellarFunctionParam","StellarFunctionReturn","StellarFunctionCall","StellarFunctionTransmitter"}};
        StellarFunctions.Support.ResearchService.Apply(new[]{lateSpec});
        Check(StellarFunctions.Support.ResearchService.All.Any(r=>r!=null&&r.id==65001&&r.hideFromTechTree),"late-prerequisite spec registers as a hidden entry while its prerequisite is absent");
        var lateTech=ScriptableObject.CreateInstance<ResearchSettings>();
        lateTech.name="LateGrantPrereq";lateTech.id=65002;lateTech.techName="Late Grant Prereq";
        lateTech.hideFlags=HideFlags.HideAndDontSave;lateTech.unlockedParts=new PartSettings[0];
        lateTech.unlockedFeatures=new ResearchUnlockableFeature[0];lateTech.requirements=new ResearchSettings[0];
        list.list.Add(lateTech);
        AccessTools.Field(typeof(StellarFunctions.Support.ResearchService),"_nextGrant").SetValue(null,0f);
        StellarFunctions.Support.ResearchService.Tick();
        var grants=(System.Collections.IDictionary)AccessTools.Field(typeof(StellarFunctions.Support.ResearchService),"Grants").GetValue(null);
        Check(grants!=null&&grants.Contains((ushort)65001),"late prerequisite resolves into the grant map on tick");
        Check(lateTech.unlockedParts!=null&&Ids.All(id=>lateTech.unlockedParts.Count(p=>p!=null&&p.id==id)==1),"late prerequisite merges every function part into its unlock preview");
        list.list.Remove(lateTech);
        UnityEngine.Object.DestroyImmediate(lateTech);
        StellarFunctions.Support.ResearchService.Apply(StellarFunctions.Content.ResearchSpecs.FromPacks(ContentRegistry.Packs));
        Check(!list.list.Any(r=>r!=null&&r.id==65001)&&StellarFunctions.Support.ResearchService.All.Count==1,"real research specs restored after the late-grant probe");
        // ApplyOne failure rollback: a spec whose UnlockFeatures enumerable
        // throws after the ResearchSettings instance is created must leave no
        // ScriptableObject, no list entry, no ownership record and no grant
        // mapping, and must not stop a later spec in the same Apply.
        int soBaseline=Resources.FindObjectsOfTypeAll<ResearchSettings>().Length;
        var failingSpec=new StellarFunctions.Support.TechSpec{Id=65003,Name="Failing Spec Probe",Hidden=true,UnlockFeatures=new ThrowingFeatures()};
        var survivorSpec=new StellarFunctions.Support.TechSpec{Id=65004,Name="Survivor Spec Probe",Hidden=true};
        StellarFunctions.Support.ResearchService.Apply(new[]{failingSpec,survivorSpec});
        Check(Resources.FindObjectsOfTypeAll<ResearchSettings>().Length==soBaseline+1,"failed research spec destroys its ScriptableObject instead of leaking it");
        Check(!list.list.Any(r=>r!=null&&r.id==65003)&&!StellarFunctions.Support.ResearchService.All.Any(r=>r!=null&&r.id==65003),"failed research spec leaves no list entry or ownership record");
        var grantsAfterFail=(System.Collections.IDictionary)AccessTools.Field(typeof(StellarFunctions.Support.ResearchService),"Grants").GetValue(null);
        Check(grantsAfterFail==null||!grantsAfterFail.Contains((ushort)65003),"failed research spec leaves no grant mapping");
        Check(list.list.Any(r=>r!=null&&r.id==65004)&&StellarFunctions.Support.ResearchService.All.Any(r=>r!=null&&r.id==65004),"spec after a failed spec still applies in the same pass");
        StellarFunctions.Support.ResearchService.Apply(StellarFunctions.Content.ResearchSpecs.FromPacks(ContentRegistry.Packs));
        Check(!list.list.Any(r=>r!=null&&(r.id==65003||r.id==65004))&&StellarFunctions.Support.ResearchService.All.Count==1,"probe failure specs removed by reapply");
        // PartSettingsProvider stale-snapshot regression: a provider whose
        // dictionary was built before our parts registered must still resolve
        // every function id when several are requested in the same burst,
        // while genuinely unknown ids keep throwing through the native path.
        var staleProvider=new PartSettingsProvider();
        var stockDict=Resources.FindObjectsOfTypeAll<PartSettings>().Where(p=>p!=null&&!Ids.Contains(p.id)).GroupBy(p=>p.id).ToDictionary(g=>g.Key,g=>g.First());
        AccessTools.Field(typeof(PartSettingsProvider),"_partsDict").SetValue(staleProvider,stockDict);
        foreach(ushort id in Ids)
        {
            var resolved=staleProvider.GetPartSettingById(id);
            Check(resolved!=null&&resolved.id==id,"stale provider resolves function part "+id+" in a same-frame burst");
        }
        bool unknownThrew=false;
        try{staleProvider.GetPartSettingById(65000);}catch(ArgumentOutOfRangeException){unknownThrew=true;}
        Check(unknownThrew,"unknown part id still throws through the native provider path");
        // Real thumbnail pixels need the native service objects (instancer,
        // renderer, scene lighting) which only exist in an activated world.
        // Attempt the real synchronous render only when they are present.
        var realInstancer=ServiceLocator.GetService<Ships.Interface.Services.IShipPartInstancer>();
        var realRenderer=ServiceLocator.GetService<Rendering.Services.ObjectThumbnailRenderer>();
        var realLighting=ServiceLocator.GetService<Rendering.Services.SceneLighting>();
        if(realInstancer==null||realRenderer==null||realLighting==null)
        {
            File.AppendAllText(Path.Combine(_output,"assertions.txt"),"SKIP real thumbnail render: native thumbnail services unavailable in the unactivated scene"+Environment.NewLine);
        }
        else
        {
            var realOwner=new GameObject("Private real thumbnails");realOwner.tag="ServiceObject";
            var realCache=realOwner.AddComponent<ShipPartsThumbnailRuntimeCache>();
            AccessTools.Field(typeof(ShipPartsThumbnailRuntimeCache),"_thumbnailsResProvider").SetValue(realCache,new Resolution());
            // Control first: a stock part through the identical render path
            // proves this fixture can rasterize before our parts are judged.
            ushort controlId=Resources.FindObjectsOfTypeAll<PartSettings>().Where(p=>p!=null&&p.id!=0&&!Ids.Contains(p.id)).Select(p=>p.id).FirstOrDefault();
            Texture2D control=null;string controlError=null;
            try{if(controlId!=0)control=realCache.GetLargeThumbnail(controlId);}catch(Exception error){controlError=error.Message;}
            if(controlError!=null||control==null||DistinctColors(control)<=8)
            {
                File.AppendAllText(Path.Combine(_output,"assertions.txt"),"SKIP real thumbnail render: unactivated scene cannot rasterize even the stock control part ("+(controlError??(control==null?"no texture":"blank image"))+")"+Environment.NewLine);
            }
            else
            {
                Check(true,"real thumbnail pipeline rendered the stock control part "+controlId);
                foreach(ushort id in Ids)
                {
                    Texture2D thumb=null;string renderError=null;
                    try{thumb=realCache.GetLargeThumbnail(id);}catch(Exception error){renderError=error.Message;}
                    if(renderError!=null||thumb==null)
                    {
                        File.AppendAllText(Path.Combine(_output,"assertions.txt"),"SKIP real thumbnail render for "+id+": "+(renderError??"no texture")+Environment.NewLine);
                        continue;
                    }
                    Check(DistinctColors(thumb)>8,"real thumbnail render produced a non-blank image for part "+id+" ("+DistinctColors(thumb)+" distinct colors)");
                }
            }
        }
        var nativeMenu=Resources.FindObjectsOfTypeAll<BuildMenu>().FirstOrDefault(m=>m.name!="Private menu");
        if(nativeMenu==null)
        {
            // The build menu only exists in an activated world; this probe
            // deliberately keeps MainGame unactivated. Menu placement is
            // covered by MenuPlan pure checks; native menu evidence needs the
            // authorized harness tier.
            File.AppendAllText(Path.Combine(_output,"assertions.txt"),"SKIP native menu section: no BuildMenu instance in the unactivated scene"+Environment.NewLine);
            return;
        }
        var menuOwner=new GameObject("Private menu");menuOwner.SetActive(false);
        var menu=UnityEngine.Object.Instantiate(nativeMenu,menuOwner.transform);
        for(int i=0;i<3;i++)StellarFunctions.Support.MenuCatalog.BeforeStart(menu);
        var tabs=(BuildMenuTabConfig[])AccessTools.Field(typeof(BuildMenu),"tabsConfig").GetValue(menu);
        int tab=Array.FindIndex(tabs,t=>t.rows.Any(r=>r.parts.Any(p=>p!=null&&Ids.Contains(p.id))));Check(tab>=0,"native menu config contains function parts");
        Check(Ids.All(id=>tabs.SelectMany(t=>t.rows).SelectMany(r=>r.parts).Count(p=>p!=null&&p.id==id)==1),"repeated menu registration creates no duplicates");
        var thumbs=menuOwner.AddComponent<ShipPartsThumbnailRuntimeCache>();AccessTools.Field(typeof(ShipPartsThumbnailRuntimeCache),"_thumbnailsResProvider").SetValue(thumbs,new Resolution());
        AccessTools.Field(typeof(BuildMenu),"researchProvider").SetValue(menu,proxy);AccessTools.Field(typeof(BuildMenu),"_selectedPartTracker").SetValue(menu,new Selection());
        AccessTools.Field(typeof(BuildMenu),"_thumbnailProvider").SetValue(menu,thumbs);
        AccessTools.Method(typeof(BuildMenu),"LoadTabPanel").Invoke(menu,new object[]{tab});
        var buttons=menu.GetComponentsInChildren<BuildPartButton>(true).Where(b=>b.partSettings!=null&&Ids.Contains(b.partSettings.id)).ToArray();
        Check(buttons.Length==(fixedBuild?Ids.Length:0),"actual native LoadTabPanel "+(fixedBuild?"shows all four unlocked function buttons":"reproduces 0.1.0 missing buttons"));
        File.WriteAllLines(Path.Combine(_output,"buttons.txt"),buttons.Select(b=>b.partSettings.id+" "+b.partSettings.fullLabel));
        int FunctionRows()=>tabs.SelectMany(t=>t.rows).SelectMany(r=>r.parts).Count(p=>p!=null&&Ids.Contains(p.id));
        Check(FunctionRows()==Ids.Length,"menu holds exactly one entry per function part before restore");
        StellarFunctions.Support.MenuCatalog.Restore();
        Check(FunctionRows()==0,"ownership-aware restore removes every function entry");
        for(int i=0;i<3;i++)StellarFunctions.Support.MenuCatalog.BeforeStart(menu);
        Check(FunctionRows()==Ids.Length,"menu re-registration after restore rebuilds all four entries once");
    }
    private sealed class ThrowingFeatures:IReadOnlyList<ResearchUnlockableFeature>
    {
        public ResearchUnlockableFeature this[int index]=>throw new InvalidOperationException("probe");
        public int Count=>throw new InvalidOperationException("probe");
        public IEnumerator<ResearchUnlockableFeature> GetEnumerator()=>throw new InvalidOperationException("probe");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private sealed class Seed:IResearchState{public ushort[] Researched{get;set;}}
    private static int DistinctColors(Texture2D texture)
    {
        if(texture==null)return 0;
        return new HashSet<uint>(texture.GetPixels32().Select(p=>(uint)(p.r|(p.g<<8)|(p.b<<16)))).Count;
    }
    private static List<ushort> RecordTarget;
    private static bool RecordThumbnail(ushort partId){if(RecordTarget!=null)RecordTarget.Add(partId);return false;}
    private static bool AllowResearch(ResearchClientProxy __instance,ResearchSettings research,ref bool __result){__result=true;return false;}
    private void DumpDonors()
    {
        var lines=new List<string>();
        foreach(var settings in Resources.FindObjectsOfTypeAll<PartSettings>().Where(p=>p.id==45||p.id==54||p.id==82))
        {
            lines.Add("PART "+settings.id+" "+settings.name+" snap="+settings.snappingStyle);
            foreach(var field in settings.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
                if(field.FieldType.IsPrimitive||field.FieldType.IsEnum||field.FieldType==typeof(Vector3))lines.Add("SETTING "+field.Name+" = "+field.GetValue(settings));
            var root=settings.part.transform;
            foreach(var t in root.GetComponentsInChildren<Transform>(true))
            {
                string path=t==root?".":t.name;for(var p=t.parent;p!=null&&p!=root;p=p.parent)path=p.name+"/"+path;
                lines.Add(path+" p="+t.localPosition.ToString("F5")+" r="+t.localEulerAngles.ToString("F3")+" s="+t.localScale.ToString("F4"));
                foreach(var component in t.GetComponents<Component>())
                {
                    if(component==null)continue;lines.Add("  "+component.GetType().FullName);
                    if(component is MeshFilter mesh&&mesh.sharedMesh!=null)lines.Add("  MESH "+mesh.sharedMesh.name+" "+mesh.sharedMesh.bounds.ToString("F5"));
                    if(component.GetType().Name.Contains("Socket")||component.GetType().Name.Contains("Display"))
                        foreach(var f in component.GetType().GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance))
                            if(f.FieldType.IsPrimitive||f.FieldType.IsEnum||f.FieldType==typeof(Vector3)||typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType))
                                lines.Add("    "+f.Name+"="+f.GetValue(component));
                }
            }
        }
        foreach(var activator in Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(c=>c!=null&&c.GetType().Name=="GenericPartOpenableMenuActivator"))
            foreach(var field in activator.GetType().GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance))
                if(field.GetValue(activator) is GameObject prefab)
                    foreach(var t in prefab.GetComponentsInChildren<Transform>(true))lines.Add("MENU "+field.Name+" "+t.name+" comps="+string.Join(",",t.GetComponents<Component>().Where(c=>c!=null).Select(c=>c.GetType().FullName)));
        File.WriteAllLines(Path.Combine(_output,"native-donor-layout.txt"),lines);
    }
    private sealed class Selection:IBuildToolPartPreviewTracker
    {public Action<PartSettings> OnPreviewShown{get;set;}public Action OnPreviewHidden{get;set;}public PartSettings SelectedPartSettings{get;set;}}
    private sealed class Resolution:IThumbnailsResolutionProvider
    {public int GetShipPartSmallThumbnailResolution()=>32;public int GetShipPartLargeThumbnailResolution()=>32;public int GetItemSmallThumbnailResolution()=>32;}
    private sealed class LifecycleProbeBehaviour:StellarFunctions.Support.IPartBehaviour
    {
        public string Scope=>"probe";
        public void OnRegistered(){}
        public void OnWorldEnter(){}
        public void OnWorldExit(){}
        public void OnPartAdded(StellarFunctions.Support.LivePart part){}
        public void OnPartRemoved(uint shipId,ushort partId){}
        public void ServerTick(StellarFunctions.Support.LivePart part,float deltaTime){}
    }
    private static StellarFunctions.Support.ScreenFrame ProbeFrame(GameObject screen)
        =>new StellarFunctions.Support.ScreenFrame{Text="PROBE OK",Scroll=false};
    private static StellarFunctions.Support.ScreenFrame OtherProbeFrame(GameObject screen)
        =>new StellarFunctions.Support.ScreenFrame{Text="OTHER",Scroll=false};
    private static StellarFunctions.Support.ScreenFrame ScrollingProbeFrame(GameObject screen)
        =>new StellarFunctions.Support.ScreenFrame{Text="SCROLLING PROBE TEXT THAT EXCEEDS THE COLUMN WIDTH",Scroll=true};
    private static StellarFunctions.Support.ScreenFrame ThrowingProbeFrame(GameObject screen)
        =>throw new InvalidOperationException("probe provider failure");
}
