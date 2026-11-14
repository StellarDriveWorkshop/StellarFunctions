using Ships.Interface.Model.Parts;
using Ships.Interface.Model.State;

namespace StellarFunctions.Support;

// Minimal live view of one placed ship part handed to behaviour hooks.
// Support owns the shape; callers map it into their own richer part
// representation (the Functions ContentPart adds the pack definition).
public readonly struct LivePart
{
    public readonly IShipStateWrite Ship;
    public readonly uint ShipId;
    public readonly ushort PartId;
    public readonly StatefulPart Part;

    public LivePart(IShipStateWrite ship, uint shipId, ushort partId, StatefulPart part)
    {
        Ship = ship;
        ShipId = shipId;
        PartId = partId;
        Part = part;
    }
}
