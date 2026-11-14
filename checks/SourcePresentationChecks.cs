using System;
using System.Reflection;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;

namespace StellarFunctions;

// Focused assertions for the native state-to-picker vocabulary. These checks
// do not create a ship, alter world state, or depend on player/UI services.
internal static class SourcePresentationChecks
{
    private const BindingFlags StaticNonPublic = BindingFlags.Static | BindingFlags.NonPublic;

    internal static void Run(Action<bool, string> check)
    {
        Type presentation = typeof(FunctionReturnState).Assembly.GetType("StellarFunctions.FunctionSourcePresentation", true);
        MethodInfo friendlyType = presentation.GetMethod("FriendlyType", StaticNonPublic);
        MethodInfo outputMeaning = presentation.GetMethod("OutputMeaning", StaticNonPublic);
        MethodInfo hasOutput = presentation.GetMethod("HasOutput", StaticNonPublic);

        Check(check, new FluidTankState(), "Fluid Tank", "tank fluid quantity");
        Check(check, new GasSensorState(), "Gas Sensor", "gas reading");
        Check(check, new VelocitySensorState(), "Velocity Sensor", "linear velocity");
        Check(check, new AngularVelSensorState(), "Angular Velocity Sensor", "angular velocity");
        Check(check, new ConstantState { ConstantValue = 2.5 }, "Constant", "fixed value 2.5");
        Check(check, new LeverState(), "Lever", "manual control value");
        Check(check, new MathGateState { Operation = MathGateOperation.Add }, "Math Gate", "input 1 + input 2");
        Check(check, new SignalProcessorState { Operation = SignalProcessorOperation.Delta }, "Signal Processor", "rate of change per second");
        Check(check, new MemoryRegisterState { Value = 7 }, "Memory Register", "stored value 7");

        check(Meaning(outputMeaning, new MathGateState { Operation = MathGateOperation.Divide }) ==
            "input 1 ÷ input 2 (zero divisor outputs 0)", "Math Gate divider describes native zero-divisor behavior");
        check(Meaning(outputMeaning, new SignalProcessorState { Operation = SignalProcessorOperation.SquareRoot }) ==
            "square root (negative input outputs 0)", "Signal Processor square root describes native negative-input behavior");
        check(Meaning(outputMeaning, new FluidTankState(), (byte)2) == "tank fluid quantity",
            "Fluid Tank socket 2 is identified as native fluid quantity");
        check(Meaning(outputMeaning, new MathGateState { Operation = MathGateOperation.Add }, (byte)1) == "math gate signal",
            "non-output Math Gate sockets are not described as its computed output");
        var math = new MathGateState { Operation = MathGateOperation.Add };
        var processor = new SignalProcessorState { Operation = SignalProcessorOperation.Abs };
        var memory = new MemoryRegisterState();
        check(HasOutput(hasOutput, math, 2) && !HasOutput(hasOutput, math, 0),
            "Identify accepts Math Gate output 2 and rejects input 0");
        check(HasOutput(hasOutput, processor, 1) && !HasOutput(hasOutput, processor, 0),
            "Identify accepts Signal Processor output 1 and rejects input 0");
        check(HasOutput(hasOutput, memory, 2) && !HasOutput(hasOutput, memory, 0),
            "Identify accepts Memory output 2 and rejects input 0");

        void Check(Action<bool, string> assertion, IStatefulPartState state, string expectedType, string expectedMeaning)
        {
            assertion(Friendly(friendlyType, state) == expectedType, expectedType + " gets a friendly part name");
            assertion(Meaning(outputMeaning, state) == expectedMeaning, expectedType + " explains its native output");
        }
    }

    private static string Friendly(MethodInfo method, IStatefulPartState state) =>
        (string)method.Invoke(null, new object[] { state, null });

    private static string Meaning(MethodInfo method, IStatefulPartState state, byte? socket = null) =>
        (string)method.Invoke(null, new object[] { state, socket });

    private static bool HasOutput(MethodInfo method, IStatefulPartState state, byte socket) =>
        (bool)method.Invoke(null, new object[] { state, socket });
}
