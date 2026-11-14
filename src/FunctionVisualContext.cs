using Ships.Interface.Model.Parts;
using Ships.Interface.Model.State;
using Ships.Parts.Common.Model;
using Ships.Parts.Common.Utils;
using UnityEngine;

namespace StellarFunctions;

// Attached to IShipPartVisual.GameObject so native visual-only cloning retains
// this component. ShipPartVisuals supplies the exact context for the placed part.
// Screens must use their own part context, never search neighboring interactors.
public sealed class FunctionVisualContext : MonoBehaviour, IContextAwarePart
{
    private PartContext _part;
    private IShipStateRead _ship;

    public void SetContext(ShipPartContext context)
    {
        _part = context.Part;
        _ship = context.ShipStateRead;
    }

    internal bool Read(out PartContext context, out StatefulPart part)
    {
        context = _part;
        part = null;
        return _ship != null && _ship.TryGetStatefulPart(_part.PartId, out part);
    }
}
