using System.Reflection;
using MelonLoader;
using StellarFunctions.Content;

[assembly: MelonInfo(typeof(StellarFunctions.Plugin), "StellarDrive Functions", "1.0.0", "Killabears", null)]
[assembly: MelonGame("CuriousOwlGames", "StellarDrive")]

namespace StellarFunctions;

public sealed class Plugin : MelonMod
{
    public override void OnInitializeMelon()
    {
        FunctionSocketPositionFix.Install("stellar.functions.sockets");
        PartSettingsLookupFix.Install("stellar.functions.partlookup");
        ContentRegistry.Behaviour("stellar-functions.call", () => new CallPartBehaviour());
        ContentRegistry.Behaviour("stellar-functions.param", () => new DefinitionPartBehaviour());
        ContentRegistry.Behaviour("stellar-functions.return", () => new DefinitionPartBehaviour());
        ContentRegistry.Behaviour("stellar-functions.transmitter", () => new TransmitterPartBehaviour());
        ContentRegistry.LoadEmbedded(Assembly.GetExecutingAssembly(), "content.functions.json");
        foreach(ushort id in new ushort[]{62005,62006,62007,62009})
            if(ContentRegistry.PartDef(id)==null)throw new System.InvalidOperationException("Function content registration failed for part "+id);
        FunctionScreens.Install();
        NativeSignalBridge.Install("stellar.functions.native");
        NativeTemplateHooks.Install();
        MoreMathCompatibility.Install();
        NativeStateSerialization.Install();
        NativeFunctionPersistence.Install();
        SchematicFunctionIntegration.InstallIfPresent();
        ClientCallInteraction.Install("stellar.functions.client");
        CallSizing.Install();
    }

    public override void OnDeinitializeMelon()
    {
        FunctionSocketPositionFix.Uninstall();
        PartSettingsLookupFix.Uninstall();
        MoreMathCompatibility.Uninstall();
        NativeSignalBridge.Uninstall();
        NativeStateSerialization.Uninstall();
        FunctionDirectory.Uninstall();
        ContentActions.Uninstall();
        NativeFunctionPersistence.Uninstall();
        SchematicFunctionIntegration.Uninstall();
        NativeTemplateHooks.Uninstall();
        Support.BehaviourHost.Uninstall();
        ClientCallInteraction.Uninstall();
        CallSizing.Uninstall();
    }

    public override void OnUpdate()
    {
        // Research ticking is independent of networking: it must run before
        // any NetworkManager exists and exactly once per frame.
        Support.ResearchService.Tick();
        // Registration can precede the dedicated scene's NetworkManager. Reconcile
        // the action/directory networking handlers with the current manager.
        ContentActions.EnsureNetworking();
        FunctionDirectory.EnsureNetworking();
        SchematicFunctionIntegration.InstallIfPresent();
        ClientCallInteraction.Tick();
        FunctionEditor.Tick();
        Support.ScreenProfiler.Tick();
    }
}
