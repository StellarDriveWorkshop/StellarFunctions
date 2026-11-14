using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace StellarFunctions;

internal static class MoreMathCoreChecks
{
    private static int _nextId = 700000;
    private static Guid Id() => new Guid(_nextId++, 0, 0, new byte[8]);
    private static FunctionNode Node(FunctionOp op, int[] inputs = null, string operation = null, double initial = 0,
        Guid reference = default, Guid[] bindings = null, byte wiredMask = 0) =>
        new FunctionNode(Id(), op, inputs, operation, initial, reference, bindings, wiredMask);
    private static FunctionGraph Graph(FunctionNode[] nodes, int result, string name = "MoreMath core fixture") =>
        new FunctionGraph(Id(), name, Array.Empty<FunctionParameter>(), nodes, result, "moremath-core-check");
    private static FunctionEvaluation Eval(FunctionGraph graph, Dictionary<string, double> state = null, double dt = .1,
        Dictionary<Guid, double> args = null,
        Func<Guid, double> external = null, Func<Guid, FunctionGraph> resolve = null) =>
        FunctionEvaluator.Evaluate(graph, resolve, args, state, external, dt);
    private static void Require(bool condition, string label, Action<bool, string> check) => check(condition, label);
    private static void Near(double actual, double expected, string label, Action<bool, string> check)
    {
        bool equal = Math.Abs(actual - expected) <= 1e-10 * Math.Max(1, Math.Abs(expected));
        check(equal, label + " (actual " + actual + ", expected " + expected + ")");
    }
    private static FunctionGraph Unary(int op, double input) => Graph(new[]
    {
        Node(FunctionOp.Constant, initial: input),
        Node(FunctionOp.MoreMathUnary, new[] { 0 }, MoreMathOperations.UnaryName(op), op)
    }, 1);
    private static FunctionGraph Binary(int op, double a, double b) => Graph(new[]
    {
        Node(FunctionOp.Constant, initial: a), Node(FunctionOp.Constant, initial: b),
        Node(FunctionOp.MoreMathBinary, new[] { 0, 1 }, MoreMathOperations.BinaryName(op), op)
    }, 2);
    private static FunctionGraph BinaryParameters(int op, out Guid left, out Guid right, out int operationNode)
    {
        left = Id(); right = Id();
        var parameters = new[] { new FunctionParameter(left, "a", 1, 0), new FunctionParameter(right, "b", 2, 0) };
        var nodes = new[] { Node(FunctionOp.Parameter, reference: left), Node(FunctionOp.Parameter, reference: right),
            Node(FunctionOp.MoreMathBinary, new[] { 0, 1 }, MoreMathOperations.BinaryName(op), op) };
        operationNode = 2;
        return new FunctionGraph(Id(), "Stateful binary", parameters, nodes, 2, "stateful-binary");
    }
    private static FunctionGraph UnaryParameter(int op, out Guid input, out int operationNode)
    {
        input = Id();
        var parameters = new[] { new FunctionParameter(input, "input", 1, 0) };
        var nodes = new[] { Node(FunctionOp.Parameter, reference: input),
            Node(FunctionOp.MoreMathUnary, new[] { 0 }, MoreMathOperations.UnaryName(op), op) };
        operationNode = 1;
        return new FunctionGraph(Id(), "Stateful unary", parameters, nodes, 1, "stateful-unary");
    }
    private static string StateKey(FunctionGraph graph, int nodeIndex) => graph.Node(nodeIndex).Id.ToString("N");

    internal static void Run(Action<bool, string> check)
    {
        // Every supported binary and unary opcode gets an explicit finite reference value.
        var binary = new (int op, double a, double b, double expected)[]
        {
            (10, 2, 3, 8), (11, 1.234, 2, 1.23), (12, 2, 3, 1), (13, 3, 2, 1),
            (14, 7, 4, 3), (15, 5, 3, 3), (16, 1, 0, 1), (17, 0, 1, 0),
            (18, 1, 0, 1), (19, 1, 2, 1), (20, 8, 2, 3), (21, 0, 2, 1),
            (22, 2, 0, 1), (23, 2, 3, 0), (24, 0, -1, 1), (25, 1, 2, 1),
            (26, 3, 4, 5), (27, 1, 2, 0), (28, 3, 2, 1), (29, 1, 0, .1), (30, 1, 0, 1)
        };
        foreach (var item in binary)
        {
            FunctionGraph graph = Binary(item.op, item.a, item.b);
            FunctionEvaluation result = Eval(graph);
            Require(result.Error == null, "binary opcode " + item.op + " evaluates without an error", check);
            Near(result.Output, item.expected, "binary opcode " + item.op + " output", check);
            Require(MoreMathOperations.BinaryFields(item.op) >= 0 && MoreMathOperations.BinaryName(item.op).StartsWith("MoreMath.", StringComparison.Ordinal),
                "binary opcode " + item.op + " has state metadata and a stable name", check);
        }

        var unary = new (int op, double input, double expected)[]
        {
            (10, 2.7, 2), (11, 1, 1), (12, -2.5, -2.5), (13, 2.2, 3),
            (14, 0, 1), (15, -2.5, -1), (16, 2, .2), (17, 0, 1)
        };
        foreach (var item in unary)
        {
            FunctionGraph graph = Unary(item.op, item.input);
            FunctionEvaluation result = Eval(graph);
            Require(result.Error == null, "unary opcode " + item.op + " evaluates without an error", check);
            Near(result.Output, item.expected, "unary opcode " + item.op + " output", check);
            Require(MoreMathOperations.UnaryFields(item.op) >= 0 && MoreMathOperations.UnaryName(item.op).StartsWith("MoreMath.", StringComparison.Ordinal),
                "unary opcode " + item.op + " has state metadata and a stable name", check);
        }

        // Delays and edge/count state are stepped through separate evaluator snapshots.
        FunctionGraph onDelay = BinaryParameters(18, out Guid onActive, out Guid onDuration, out int onNode);
        string onKey = StateKey(onDelay, onNode);
        Dictionary<string, double> onState = null;
        foreach (double expected in new[] { 0d, 0d, 1d })
        {
            FunctionEvaluation step = Eval(onDelay, onState, .1, new Dictionary<Guid, double> { [onActive] = 1, [onDuration] = .2 });
            Require(step.Error == null, "OnDelay sequence remains valid", check);
            Near(step.Output, expected, "OnDelay elapsed threshold", check);
            onState = step.ProposedState;
        }
        Near(onState[onKey + "/t"], .2, "OnDelay retains elapsed time", check);

        FunctionGraph offDelay = BinaryParameters(19, out Guid offActive, out Guid offDuration, out int offNode);
        string offKey = StateKey(offDelay, offNode);
        Dictionary<string, double> offState = Eval(offDelay, args: new Dictionary<Guid, double> { [offActive] = 1, [offDuration] = .2 }).ProposedState;
        Near(Eval(offDelay, offState, args: new Dictionary<Guid, double> { [offActive] = 0, [offDuration] = .2 }).Output, 1, "OffDelay begins held", check);
        offState = Eval(offDelay, offState, args: new Dictionary<Guid, double> { [offActive] = 0, [offDuration] = .2 }).ProposedState;
        Near(Eval(offDelay, offState, args: new Dictionary<Guid, double> { [offActive] = 0, [offDuration] = .2 }).Output, 1, "OffDelay continues through grace period", check);
        offState = Eval(offDelay, offState, args: new Dictionary<Guid, double> { [offActive] = 0, [offDuration] = .2 }).ProposedState;
        Near(Eval(offDelay, offState, args: new Dictionary<Guid, double> { [offActive] = 0, [offDuration] = .2 }).Output, 0, "OffDelay expires at threshold", check);
        Require(offState.ContainsKey(offKey + "/t"), "OffDelay elapsed state is keyed per node", check);

        FunctionGraph counter = BinaryParameters(30, out Guid countInput, out Guid countReset, out int countNode);
        string counterKey = StateKey(counter, countNode);
        Dictionary<string, double> countState = null;
        foreach (var sample in new[] { (input: 1d, expected: 1d), (input: 1d, expected: 1d), (input: -1d, expected: 0d) })
        {
            FunctionEvaluation step = Eval(counter, countState, args: new Dictionary<Guid, double> { [countInput] = sample.input, [countReset] = 0 });
            Near(step.Output, sample.expected, "Counter edge sequence", check);
            countState = step.ProposedState;
        }
        Require(countState.ContainsKey(counterKey + "/p"), "Counter stores previous direction", check);
        FunctionEvaluation reset = Eval(counter, countState, args: new Dictionary<Guid, double> { [countInput] = 1, [countReset] = 1 });
        Near(reset.Output, 0, "Counter reset input", check);

        FunctionGraph toggle = UnaryParameter(11, out Guid toggleInput, out int toggleNode);
        string toggleKey = StateKey(toggle, toggleNode);
        FunctionEvaluation toggleOn = Eval(toggle, args: new Dictionary<Guid, double> { [toggleInput] = 1 });
        Near(toggleOn.Output, 1, "Toggle rising edge", check);
        FunctionEvaluation held = Eval(toggle, toggleOn.ProposedState, args: new Dictionary<Guid, double> { [toggleInput] = 1 });
        Near(held.Output, 1, "Toggle held high does not retrigger", check);
        FunctionEvaluation toggleOff = Eval(toggle, held.ProposedState, args: new Dictionary<Guid, double> { [toggleInput] = 0 });
        FunctionEvaluation toggleAgain = Eval(toggle, toggleOff.ProposedState, args: new Dictionary<Guid, double> { [toggleInput] = 1 });
        Near(toggleAgain.Output, 0, "Toggle second rising edge", check);
        Require(toggleAgain.ProposedState.ContainsKey(toggleKey + "/p"), "Toggle stores its previous input", check);

        FunctionGraph integrator = UnaryParameter(16, out Guid integratorInput, out int integratorNode);
        string integratorKey = StateKey(integrator, integratorNode);
        Dictionary<string, double> integrateState = null;
        foreach (double expected in new[] { .2, .4, .6 })
        {
            FunctionEvaluation step = Eval(integrator, integrateState, .1, new Dictionary<Guid, double> { [integratorInput] = 2 });
            Near(step.Output, expected, "Integrator accumulates by supplied dt", check);
            integrateState = step.ProposedState;
        }
        Require(integrateState.ContainsKey(integratorKey), "Integrator persists output by node key", check);
        var roundTrip = JsonSerializer.Deserialize<Dictionary<string, double>>(JsonSerializer.Serialize(integrateState));
        Require(roundTrip != null && roundTrip.Count == integrateState.Count && roundTrip.All(p => integrateState[p.Key] == p.Value),
            "private state dictionary survives System.Text.Json round trip", check);

        // The same stateful target is called twice in one graph. Paths keep each virtual
        // instance independent, and advancing one history does not mutate the other.
        var parameter = new FunctionParameter(Id(), "input", 1, 0);
        FunctionGraph target = new FunctionGraph(Id(), "Integrator target", new[] { parameter }, new[]
        {
            Node(FunctionOp.Parameter, reference: parameter.Id),
            Node(FunctionOp.MoreMathUnary, new[] { 0 }, MoreMathOperations.UnaryName(16), 16)
        }, 1, "integrator-target");
        Guid targetId = target.Id;
        FunctionGraph nested = Graph(new[]
        {
            Node(FunctionOp.Constant, initial: 1), Node(FunctionOp.Constant, initial: 3),
            Node(FunctionOp.Nested, new[] { 0, -1, -1 }, reference: targetId, bindings: new[] { parameter.Id }, wiredMask: 1),
            Node(FunctionOp.Nested, new[] { 1, -1, -1 }, reference: targetId, bindings: new[] { parameter.Id }, wiredMask: 1),
            Node(FunctionOp.Binary, new[] { 2, 3 }, "Add")
        }, 4, "Nested integrators");
        FunctionEvaluation nested1 = Eval(nested, resolve: id => id == targetId ? target : null);
        Near(nested1.Output, .4, "nested stateful calls read isolated initial histories", check);
        Require(nested1.ProposedState.Count == 2, "each nested call writes a distinct private history", check);
        FunctionEvaluation nested2 = Eval(nested, nested1.ProposedState, resolve: id => id == targetId ? target : null);
        Near(nested2.Output, .8, "nested histories advance independently on next evaluation", check);
        Require(nested2.ProposedState.Keys.Distinct().Count() == 2, "nested state keys remain distinct", check);

        // A late failure discards all proposed updates and never mutates the caller's snapshot.
        var prior = new Dictionary<string, double> { ["owned"] = 12 };
        FunctionGraph failing = Graph(new[]
        {
            Node(FunctionOp.Constant, initial: 1), Node(FunctionOp.MoreMathUnary, new[] { 0 }, MoreMathOperations.UnaryName(16), 16),
            Node(FunctionOp.External, reference: Id()), Node(FunctionOp.Binary, new[] { 1, 2 }, "Add")
        }, 3, "Atomic MoreMath state failure");
        FunctionEvaluation failed = Eval(failing, prior, external: _ => double.PositiveInfinity);
        Require(failed.Error == "NONFINITE OUTPUT" && failed.ProposedState == null && prior.Count == 1 && prior["owned"] == 12,
            "nonfinite downstream result discards proposed MoreMath state atomically", check);
    }
}
