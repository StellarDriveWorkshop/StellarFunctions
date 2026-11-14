namespace StellarFunctions.Support;

// Support-owned per-part behaviour contract for the lifecycle host.
// Action handling, configuration UI and panel metrics stay with the
// caller's own behaviour type; the host only drives registration,
// world transitions, placement detection, server ticks and removal.
public interface IPartBehaviour
{
    string Scope { get; }
    void OnRegistered();
    void OnWorldEnter();
    void OnWorldExit();
    void OnPartAdded(LivePart part);
    void OnPartRemoved(uint shipId, ushort partId);
    void ServerTick(LivePart part, float deltaTime);
}
