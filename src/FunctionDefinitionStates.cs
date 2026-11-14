using System;
using Newtonsoft.Json;
using Ships.Interface.Model.Parts.StateTypes;

namespace StellarFunctions;

public abstract record FunctionPartState : IPickablePartState, IDirectlyInteractivePart
{
    [JsonIgnore]
    public abstract Guid StableId { get; }
    public bool IsHeldByPlayer { get; init; }
    public uint HoldingPlayerId { get; init; }
    public bool IsInUse { get; init; }

    public IPickablePartState WithPlayerHolding(uint playerId) =>
        this with
        {
            IsHeldByPlayer = true,
            HoldingPlayerId = playerId,
        };

    public IPickablePartState WithoutPlayerHolding() => this with { IsHeldByPlayer = false, HoldingPlayerId = 0 };

    public IInteractivePart WithUseState(bool isInUse) => this with { IsInUse = isInUse };
}

public sealed record FunctionParamState : FunctionPartState, ISignalEmitterState
{
    public Guid ParamId { get; init; } = Guid.NewGuid();

    [JsonIgnore]
    public override Guid StableId => ParamId;
    public string Name { get; init; } = "Param";
    public int Index { get; init; } = 1;
    public double? DefaultValue { get; init; }

    [JsonIgnore]
    public byte[] EmissionSockets => new byte[] { 0 };
}

public sealed record FunctionReturnState : FunctionPartState, ISignalProcessorState
{
    public Guid DefinitionId { get; init; } = Guid.NewGuid();

    [JsonIgnore]
    public override Guid StableId => DefinitionId;
    public string Name { get; init; } = "Function";
    public string NodeIdentities { get; init; } = "{}";
    public string Sources { get; init; } = "{}";
    public string Status { get; init; } = "UNWIRED RETURN";
    public bool Broadcast { get; init; }

    [JsonIgnore]
    public byte[] ListeningSockets => new byte[] { 0 };

    [JsonIgnore]
    public byte[] EmissionSockets => Array.Empty<byte>();
}

public sealed record FunctionTransmitterState : FunctionPartState
{
    public Guid TransmitterId { get; init; } = Guid.NewGuid();

    [JsonIgnore]
    public override Guid StableId => TransmitterId;
    public string Summary { get; init; } = "No functions";
}
