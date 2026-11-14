using System;
using Newtonsoft.Json;
using Ships.Interface.Model.Parts.StateTypes;

namespace StellarFunctions;

public sealed record FunctionCallState : FunctionPartState, ISignalProcessorState
{
    private static readonly byte[] OutputSocketIds = { 3 };
    private static readonly byte[] InputSocketIds = { 0, 1, 2 };

    public Guid InstanceId { get; init; } = Guid.NewGuid();

    [JsonIgnore]
    public override Guid StableId => InstanceId;
    public string SelectedFunctionId { get; init; } = string.Empty;
    public string SelectedName { get; init; } = string.Empty;
    public string PendingFunctionId { get; init; } = string.Empty;
    public string PendingFunctionName { get; init; } = string.Empty;
    public string Status { get; init; } = "NO FUNCTION";
    public string Slot0 { get; init; } = string.Empty;
    public string Slot1 { get; init; } = string.Empty;
    public string Slot2 { get; init; } = string.Empty;
    public string Input0Label { get; init; } = "Unused";
    public string Input1Label { get; init; } = "Unused";
    public string Input2Label { get; init; } = "Unused";
    public int InputCount { get; init; } = 3;
    public string ResizeFeedback { get; init; } = string.Empty;
    public byte ListeningMask { get; init; } = 7;
    public string RuntimeJson { get; init; } = "{}";
    public string StructureKey { get; init; } = string.Empty;
    public double Output { get; init; }
    public double LastInput0 { get; init; }
    public double LastInput1 { get; init; }
    public double LastInput2 { get; init; }
    public long LastSignalPass { get; init; }

    [JsonIgnore]
    public byte[] EmissionSockets => OutputSocketIds;
    private static readonly byte[][] ListeningSocketIds = BuildListeningSocketIds();

    private static byte[][] BuildListeningSocketIds()
    {
        var result = new byte[8][];
        for (int mask = 0; mask < result.Length; mask++)
        {
            int count = 0;
            for (byte id = 0; id < InputSocketIds.Length; id++)
            {
                if ((mask & (1 << id)) != 0)
                {
                    count++;
                }
            }

            var sockets = new byte[count];
            int index = 0;
            for (byte id = 0; id < InputSocketIds.Length; id++)
            {
                if ((mask & (1 << id)) != 0)
                {
                    sockets[index++] = InputSocketIds[id];
                }
            }

            result[mask] = sockets;
        }
        return result;
    }

    [JsonIgnore]
    public byte[] ListeningSockets
    {
        get
        {
            int inputMask =
                InputCount == 1 ? 1
                : InputCount == 2 ? 3
                : InputCount == 3 ? 7
                : 0;
            return ListeningSocketIds[ListeningMask & inputMask & 7];
        }
    }
}
