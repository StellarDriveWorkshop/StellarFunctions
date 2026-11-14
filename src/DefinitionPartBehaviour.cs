using Core.Services;
using MelonLoader;
using Players;
using Ships.Services;
using StellarFunctions.Content;

namespace StellarFunctions;

public sealed class DefinitionPartBehaviour : PartBehaviour
{
    public override void OnAction(ContentPart part, TrackedPlayerServer player, string action)
    {
        if (!FunctionEdits.Authorize(part, player, action) || part.Part?.State is not FunctionPartState original)
        {
            return;
        }

        try
        {
            if (FunctionDirectory.Handle(part, player, action) || FunctionBroadcast.Handle(part, action))
            {
                return;
            }

            FunctionPartState next = FunctionEdits.Apply(original, action);
            var ships = ServiceLocator.GetService<ShipsServerTracker>();
            if (ships != null && ships.TryGetTrackedShip(part.ShipId, out var ship))
            {
                ship.UpdatePartState(part.PartId, next);
            }
        }
        catch (System.Exception error)
            when (error is FunctionFault
                || error is System.IO.InvalidDataException
                || error is Newtonsoft.Json.JsonException
                || error is System.FormatException
                || error is System.OverflowException
            )
        {
            // No fields are applied until the entire bounded edit has validated.
            MelonLogger.Warning("[functions] Definition edit refused: " + error.Message);
        }
    }

    public override void OnWorldExit()
    {
        NativeFunctionRuntime.Reset();
        FunctionEdits.Reset();
        FunctionEditor.Reset();
    }
}
