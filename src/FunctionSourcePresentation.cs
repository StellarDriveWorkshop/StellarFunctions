using System;
using System.Linq;
using Core.Services;
using StellarFunctions.Content;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Services;
using Ships.Visuals;
using UnityEngine;
using WorldTracking.Interface.Services;

namespace StellarFunctions;

// Client-local presentation for the Return source picker. Native state remains
// authoritative; this helper only describes and briefly highlights a source.
internal static class FunctionSourcePresentation
{
    private const float HighlightSeconds = 3f;
    private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
    private static ShipPartVisuals _highlightOwner;
    private static ushort _highlightedPart;
    private static float _removeHighlightAt;

    internal static string Label(ContentPart context, EntityPartSocket socket)
    {
        if (!TryResolvePart(socket, out StatefulPart part))
            return "Unavailable source · section " + socket.PartContext.ShipId + " · part " + socket.PartContext.PartId +
                " · output " + socket.Socket.SocketId;

        string type = FriendlyType(part.State, part.Settings?.name);
        string meaning = OutputMeaning(part.State, socket.Socket.SocketId);
        string distance = Distance(context, socket);
        return type + " — " + meaning + (distance.Length == 0 ? "" : " · " + distance) +
            "  [part " + part.Id + " · section " + socket.PartContext.ShipId + " · output " + socket.Socket.SocketId + "]";
    }

    internal static bool Identify(ContentPart context, EntityPartSocket socket, out string message)
    {
        RemoveHighlight();
        if (!TryResolvePart(socket, out StatefulPart part))
        {
            message = "Source is no longer available in the loaded native world.";
            return false;
        }
        if (!HasOutput(part.State, socket.Socket.SocketId))
        {
            message = "That output socket is no longer available.";
            return false;
        }
        try
        {
            ShipPartVisuals visuals = FindVisuals(socket.PartContext.ShipId);
            if (visuals == null)
            {
                message = "Source visual is not loaded, so it cannot be highlighted.";
                return false;
            }
            if (!visuals.TryGetFloatingPartVisual(part.Id, out var visual))
            {
                message = "Source visual is not loaded, so it cannot be highlighted.";
                return false;
            }
            // The native API has no highlight ownership/reference count:
            // removal resets red emission to black. Preserve an existing
            // native/user red highlight rather than taking ownership of it.
            if (HasRedEmission(visual.GameObject))
            {
                message = "This part is already highlighted; its current highlight was left unchanged.";
                return true;
            }

            visuals.HighlightFloatingPart(part.Id);
            _highlightOwner = visuals;
            _highlightedPart = part.Id;
            _removeHighlightAt = Time.unscaledTime + HighlightSeconds;
            message = "Highlighted " + FriendlyType(part.State, part.Settings?.name) + " for 3 seconds.";
            return true;
        }
        catch (Exception)
        {
            RemoveHighlight();
            message = "The native source highlight could not be applied.";
            return false;
        }
    }

    internal static void Tick()
    {
        if (_highlightOwner != null && Time.unscaledTime >= _removeHighlightAt) RemoveHighlight();
    }

    internal static void Reset() => RemoveHighlight();

    internal static bool HasOutput(IStatefulPartState state, byte socket)
    {
        byte[] emittedSockets = state is ISignalEmitterState emitter
            ? emitter.EmissionSockets
            : state is ISignalProcessorState processor ? processor.EmissionSockets : Array.Empty<byte>();
        return emittedSockets.Contains(socket);
    }

    internal static string FriendlyType(IStatefulPartState state, string nativeName = null)
    {
        if (state is FluidTankState) return "Fluid Tank";
        if (state is GasSensorState) return "Gas Sensor";
        if (state is VelocitySensorState) return "Velocity Sensor";
        if (state is AngularVelSensorState) return "Angular Velocity Sensor";
        if (state is ConstantState) return "Constant";
        if (state is LeverState) return "Lever";
        if (state is MathGateState) return "Math Gate";
        if (state is SignalProcessorState) return "Signal Processor";
        if (state is MemoryRegisterState) return "Memory Register";
        return string.IsNullOrWhiteSpace(nativeName) ? "Signal source" : nativeName;
    }

    internal static string OutputMeaning(IStatefulPartState state, byte? socketId = null)
    {
        if (state is FluidTankState tank)
            return socketId == null || socketId == 2 ? "tank fluid quantity" : "fluid tank signal";
        if (state is GasSensorState gas)
            return "gas reading";
        if (state is VelocitySensorState velocity)
            return "linear velocity";
        if (state is AngularVelSensorState angular)
            return "angular velocity";
        if (state is ConstantState constant)
            return "fixed value " + constant.ConstantValue.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
        if (state is LeverState)
            return "manual control value";
        if (state is MathGateState math)
            return socketId == null || socketId == 2 ? BinaryMeaning(math.Operation) : "math gate signal";
        if (state is SignalProcessorState processor)
            return socketId == null || socketId == 1 ? UnaryMeaning(processor.Operation) : "signal processor output";
        if (state is MemoryRegisterState memory)
            return socketId == null || socketId == 2
                ? "stored value " + memory.Value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)
                : "memory register signal";
        return "signal output";
    }

    private static string BinaryMeaning(MathGateOperation operation)
    {
        switch (operation)
        {
            case MathGateOperation.Multiply: return "input 1 × input 2";
            case MathGateOperation.Add: return "input 1 + input 2";
            case MathGateOperation.Subtract: return "input 1 − input 2";
            case MathGateOperation.Divide: return "input 1 ÷ input 2 (zero divisor outputs 0)";
            case MathGateOperation.Min: return "lower of inputs 1 and 2";
            case MathGateOperation.Max: return "higher of inputs 1 and 2";
            case MathGateOperation.Atan2: return "atan2(input 1, input 2)";
            case MathGateOperation.LesserThan: return "1 when input 1 < input 2; otherwise 0";
            case MathGateOperation.GreaterThan: return "1 when input 1 > input 2; otherwise 0";
            case MathGateOperation.Equals: return "1 when inputs match; otherwise 0";
            default: return "native math gate output";
        }
    }

    private static string UnaryMeaning(SignalProcessorOperation operation)
    {
        switch (operation)
        {
            case SignalProcessorOperation.Negation: return "−input";
            case SignalProcessorOperation.SquareRoot: return "square root (negative input outputs 0)";
            case SignalProcessorOperation.Abs: return "absolute value";
            case SignalProcessorOperation.Delta: return "rate of change per second";
            case SignalProcessorOperation.Sin: return "sine of input";
            case SignalProcessorOperation.Cos: return "cosine of input";
            case SignalProcessorOperation.Tan: return "tangent of input";
            case SignalProcessorOperation.Asin: return "arcsine of input clamped to −1…1";
            case SignalProcessorOperation.Acos: return "arccosine of input clamped to −1…1";
            case SignalProcessorOperation.Atan: return "arctangent of input";
            default: return "native signal processor output";
        }
    }

    private static bool TryResolvePart(EntityPartSocket socket, out StatefulPart part)
    {
        part = null;
        var ships = ServiceLocator.GetService<IShipsClientProvider>();
        if (ships == null || !ships.TryGetShip(socket.PartContext.ShipId, out var ship)) return false;
        part = ship.StatefulParts?.FirstOrDefault(p => p.Id == socket.PartContext.PartId);
        return part != null && part.State != null;
    }

    private static ShipPartVisuals FindVisuals(uint shipId)
    {
        return Resources.FindObjectsOfTypeAll<ShipPartVisuals>()
            .FirstOrDefault(v => v != null && v.ShipId == shipId);
    }

    private static string Distance(ContentPart context, EntityPartSocket socket)
    {
        try
        {
            var ships = ServiceLocator.GetService<IShipsClientProvider>();
            var transpose = ServiceLocator.GetService<IWorldTransposeClient>();
            if (context.Part == null || ships == null || transpose == null ||
                !ships.TryGetShip(context.ShipId, out var contextShip) ||
                !ships.TryGetShip(socket.PartContext.ShipId, out var sourceShip) ||
                !ships.TryGetPartPosition(new PartContext(context.ShipId, context.PartId), out Vector3 contextPosition) ||
                !ships.TryGetPartPosition(socket.PartContext, out Vector3 sourcePosition)) return "";

            Vector3 sourceInContextSpace = sourceShip.RelativeSpaceId == contextShip.RelativeSpaceId
                ? sourcePosition
                : transpose.Position(sourceShip.RelativeSpaceId, contextShip.RelativeSpaceId, sourcePosition);
            if (!Finite(contextPosition) || !Finite(sourceInContextSpace)) return "";
            float distance = Vector3.Distance(contextPosition, sourceInContextSpace);
            return float.IsFinite(distance) ? distance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m" : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    private static bool HasRedEmission(GameObject root)
    {
        if (root == null) return false;
        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>())
            foreach (Material material in renderer.materials)
                if (material != null && material.HasColor(EmissionColor))
                {
                    Color color = material.GetColor(EmissionColor);
                    if (color.r > 0.99f && color.g < 0.01f && color.b < 0.01f) return true;
                }
        return false;
    }

    private static void RemoveHighlight()
    {
        if (_highlightOwner != null)
        {
            try { _highlightOwner.RemoveFloatingPartHighlight(_highlightedPart); }
            catch (Exception) { }
        }
        _highlightOwner = null;
        _highlightedPart = 0;
        _removeHighlightAt = 0;
    }
}
