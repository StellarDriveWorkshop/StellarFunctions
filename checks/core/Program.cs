using System.Globalization;
using StellarFunctions;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }

    checks++;
}
MoreMathCoreChecks.Run(Check);
CleanBehavioralChecks.Run(Check);
SupportCoreChecks.Run(Check);
CallFaceTextChecks.Run(Check);
ScreenProfileChecks.Run(Check);
void Near(double actual, double expected, string message) =>
    Check(Math.Abs(actual - expected) < 1e-10, message + ": " + actual + " != " + expected);
Guid Id(int i) => new Guid(i, 0, 0, new byte[8]);
FunctionNode N(
    int id,
    FunctionOp op,
    int[] input = null,
    string operation = null,
    double initial = 0,
    Guid reference = default,
    Guid[] bindings = null,
    byte wired = 0
) => new(Id(id), op, input, operation, initial, reference, bindings, wired);
FunctionEvaluation Eval(
    FunctionGraph graph,
    Dictionary<Guid, double> args = null,
    Dictionary<string, double> state = null,
    double dt = .02,
    Func<Guid, FunctionGraph> resolve = null,
    Func<Guid, double> external = null
) => FunctionEvaluator.Evaluate(graph, resolve, args, state, external, dt);

var param = new FunctionParameter(Id(1), "x", 1, 7);
var identity = new FunctionGraph(
    Id(100),
    "Identity",
    new[] { param },
    new[] { N(1, FunctionOp.Parameter, reference: Id(1)) },
    0,
    "identity"
);
Near(Eval(identity).Output, 7, "unwired uses default");
Near(Eval(identity, new() { { Id(1), 0 } }).Output, 0, "wired zero does not use default");
Near(Eval(identity, new() { { Id(1), -2.5 } }).Output, -2.5, "negative fractional argument");

var memory = new FunctionGraph(
    Id(101),
    "Accumulator",
    new[] { param },
    new[]
    {
        N(1, FunctionOp.Parameter, reference: Id(1)),
        N(2, FunctionOp.Constant, initial: 1),
        N(3, FunctionOp.Memory, new[] { 3, 1 }),
        N(4, FunctionOp.Binary, new[] { 2, 0 }, "Add"),
    },
    2,
    "feedback"
);
Dictionary<string, double> a = null,
    b = null;
for (int pass = 0; pass < 12; pass++)
{
    var ar = Eval(memory, new() { { Id(1), 1 } }, a);
    var br = Eval(memory, new() { { Id(1), 10 } }, b);
    Check(ar.Error == null && br.Error == null, "legal Memory feedback");
    Near(ar.Output, pass, "private Call A");
    Near(br.Output, pass * 10, "private Call B");
    a = ar.ProposedState;
    b = br.ProposedState;
}
var swap = new FunctionGraph(
    Id(102),
    "Swap",
    Array.Empty<FunctionParameter>(),
    new[]
    {
        N(1, FunctionOp.Constant, initial: 1),
        N(2, FunctionOp.Memory, new[] { 2, 0 }, initial: 3),
        N(3, FunctionOp.Memory, new[] { 1, 0 }, initial: 9),
        N(4, FunctionOp.Binary, new[] { 1, 2 }, "Subtract"),
    },
    3,
    "swap"
);
var swapped = Eval(swap);
Near(swapped.Output, -6, "reads prior Memory values");
Near(Eval(swap, state: swapped.ProposedState).Output, 6, "simultaneous Memory writes");
var held = new FunctionGraph(
    Id(103),
    "Hold",
    Array.Empty<FunctionParameter>(),
    new[]
    {
        N(1, FunctionOp.Constant, initial: 99),
        N(2, FunctionOp.Constant, initial: .999),
        N(3, FunctionOp.Memory, new[] { 0, 1 }, initial: 4),
    },
    2,
    "hold"
);
var hr = Eval(held);
Near(hr.Output, 4, "Memory initial value");
Check(hr.ProposedState.Count == 0, "enable below one does not write");

foreach (
    var pair in new[]
    {
        ("Subtract", -3d),
        ("Divide", .25),
        ("Atan2", Math.Atan2(1, 4)),
        ("LesserThan", 1d),
        ("GreaterThan", 0d),
        ("Equals", 0d),
    }
)
{
    Near(FunctionEvaluator.Binary(pair.Item1, 1, 4), pair.Item2, "asymmetric " + pair.Item1);
}

Near(FunctionEvaluator.Binary("Divide", 12, 0), 0, "native division by zero");
Near(FunctionEvaluator.Unary("SquareRoot", -1), 0, "negative sqrt");
Near(FunctionEvaluator.Unary("Asin", 2), Math.PI / 2, "native asin clamp");
foreach (double dt in new[] { 0, .01, .1, .5 })
{
    var delta = new FunctionGraph(
        Id(104),
        "Derivative",
        new[] { param },
        new[] { N(1, FunctionOp.Parameter, reference: Id(1)), N(2, FunctionOp.Unary, new[] { 0 }, "Delta") },
        1,
        "delta"
    );
    var r = Eval(delta, new() { { Id(1), 3 } }, dt: dt);
    Near(r.Output, dt == 0 ? 0 : 3 / dt, "native Delta first input and dt");
    Near(Eval(delta, new() { { Id(1), 5 } }, r.ProposedState, dt).Output, dt == 0 ? 0 : 2 / dt, "native Delta history");
}
var nested = new FunctionGraph(
    Id(105),
    "Two accumulators",
    Array.Empty<FunctionParameter>(),
    new[]
    {
        N(1, FunctionOp.Constant, initial: 1),
        N(2, FunctionOp.Constant, initial: 10),
        N(3, FunctionOp.Nested, new[] { 0, -1, -1 }, reference: memory.Id, bindings: new[] { param.Id }, wired: 1),
        N(4, FunctionOp.Nested, new[] { 1, -1, -1 }, reference: memory.Id, bindings: new[] { param.Id }, wired: 1),
        N(5, FunctionOp.Binary, new[] { 2, 3 }, "Add"),
    },
    4,
    "nested"
);
var nr = Eval(nested, resolve: id => id == memory.Id ? memory : null);
Check(nr.Error == null, "nested calls supported");
Near(nr.Output, 0, "nested prior state");
Near(Eval(nested, state: nr.ProposedState, resolve: id => memory).Output, 11, "private nested histories");
Check(
    nr.ProposedState.Keys.Count(k => k.EndsWith(Id(3).ToString("N"))) == 2,
    "nested path keys distinguish identical Memory node IDs"
);
var callerGraphNested = FunctionEvaluator.Evaluate(
    nested,
    (caller, id) => caller != null && caller.Id == nested.Id && id == memory.Id ? memory : null,
    null,
    null,
    null,
    .02
);
Check(callerGraphNested.Error == null, "nested calls with caller graph resolver supported");
Near(callerGraphNested.Output, 0, "caller graph nested prior state");

var recursive = new FunctionGraph(
    Id(106),
    "Recursive",
    Array.Empty<FunctionParameter>(),
    new[] { N(1, FunctionOp.Nested, reference: Id(106)) },
    0,
    "recursive"
);
var rr = Eval(recursive, resolve: id => recursive);
Check(
    rr.Error == "RECURSION" && rr.Output == 0 && rr.ProposedState == null,
    "refuse recursion without committing state"
);
var broken = new FunctionGraph(
    Id(107),
    "Broken subtree",
    Array.Empty<FunctionParameter>(),
    new[]
    {
        N(1, FunctionOp.Nested, new[] { -1, -1, -1 }, reference: memory.Id),
        N(2, FunctionOp.External, reference: Id(55)),
        N(3, FunctionOp.Binary, new[] { 0, 1 }, "Add"),
    },
    2,
    "broken"
);
var original = new Dictionary<string, double> { { "preserved", 12 } };
var failure = Eval(
    broken,
    state: original,
    resolve: id => memory,
    external: id => throw new InvalidOperationException("failed source")
);
Check(
    failure.Error != null && failure.ProposedState == null && original.Count == 1 && original["preserved"] == 12,
    "failed nested tree leaves original snapshot untouched"
);
var invalidState = Eval(identity, state: new() { { "bad", double.NaN } });
Check(invalidState.Error == "BAD INSTANCE STATE", "reject invalid loaded state without escaping boundary");
var infinity = Eval(identity, new() { { Id(1), double.PositiveInfinity } });
Check(infinity.Error != null && infinity.Output == 0, "contain nonfinite input");
try
{
    new FunctionGraph(
        Id(108),
        "Loop",
        Array.Empty<FunctionParameter>(),
        new[] { N(1, FunctionOp.Unary, new[] { 0 }, "Abs") },
        0,
        "loop"
    );
    Check(false, "loop must throw");
}
catch (FunctionFault e)
{
    Check(e.Message == "WIRE LOOP", "combinational cycle diagnosed");
}
try
{
    new FunctionGraph(
        Id(109),
        "Skipped",
        new[] { new FunctionParameter(Id(1), "a", 2, null) },
        new[] { N(1, FunctionOp.Constant) },
        0,
        "bad"
    );
    Check(false, "skipped index must throw");
}
catch (FunctionFault e)
{
    Check(e.Message == "BAD PARAM INDEX", "contiguous indexes");
}
var clampParams = new[]
{
    new FunctionParameter(Id(11), "x", 1, null),
    new FunctionParameter(Id(12), "hi", 2, 1),
    new FunctionParameter(Id(13), "lo", 3, -1),
};
var clamp = new FunctionGraph(
    Id(110),
    "Clamp",
    clampParams,
    new[]
    {
        N(11, FunctionOp.Parameter, reference: Id(11)),
        N(12, FunctionOp.Parameter, reference: Id(12)),
        N(13, FunctionOp.Parameter, reference: Id(13)),
        N(14, FunctionOp.Binary, new[] { 0, 1 }, "Min"),
        N(15, FunctionOp.Binary, new[] { 3, 2 }, "Max"),
    },
    4,
    "clamp"
);
Near(Eval(clamp, new() { { Id(11), 8 } }).Output, 1, "Clamp default high");
Near(Eval(clamp, new() { { Id(11), -8 } }).Output, -1, "Clamp default low");
Near(Eval(clamp, new() { { Id(11), .25 } }).Output, .25, "Clamp preserves interior value");
Near(Eval(clamp, new() { { Id(11), 8 }, { Id(12), 0 } }).Output, 0, "wired zero replaces Clamp default high");
var slewParams = new[]
{
    new FunctionParameter(Id(21), "Target", 1, null),
    new FunctionParameter(Id(22), "RateUp", 2, null),
    new FunctionParameter(Id(23), "RateDown", 3, null),
};
var slew = new FunctionGraph(
    Id(111),
    "Slew",
    slewParams,
    new[]
    {
        N(21, FunctionOp.Parameter, reference: Id(21)),
        N(22, FunctionOp.Parameter, reference: Id(22)),
        N(23, FunctionOp.Parameter, reference: Id(23)),
        N(24, FunctionOp.Constant, initial: 1),
        N(25, FunctionOp.Memory, new[] { 9, 3 }),
        N(26, FunctionOp.Binary, new[] { 0, 4 }, "Subtract"),
        N(27, FunctionOp.Unary, new[] { 2 }, "Negation"),
        N(28, FunctionOp.Binary, new[] { 5, 1 }, "Min"),
        N(29, FunctionOp.Binary, new[] { 7, 6 }, "Max"),
        N(30, FunctionOp.Binary, new[] { 4, 8 }, "Add"),
    },
    9,
    "slew"
);
Dictionary<string, double> slewA = null,
    slewB = null;
for (int pass = 1; pass <= 5; pass++)
{
    var ar = Eval(
        slew,
        new()
        {
            { Id(21), 10 },
            { Id(22), 2 },
            { Id(23), 3 },
        },
        slewA
    );
    var br = Eval(
        slew,
        new()
        {
            { Id(21), -10 },
            { Id(22), 1 },
            { Id(23), 4 },
        },
        slewB
    );
    Near(ar.Output, Math.Min(10, pass * 2), "Slew A isolated rising history");
    Near(br.Output, Math.Max(-10, pass * -4), "Slew B isolated falling history");
    slewA = ar.ProposedState;
    slewB = br.ProposedState;
}
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
Near(Eval(identity, new() { { Id(1), 1.25 } }).Output, 1.25, "numeric execution independent of UI culture");
Console.WriteLine(
    $"PASS {checks} pure compiler/executor checks; native template ownership, wiring, persistence and multiplayer are separate gates."
);
