using StellarFunctions.Support;

namespace StellarFunctions;

// Exercises the real StellarFunctions.Support decision modules compiled from
// src/Support: MenuPlan placement, CatalogueMerge conflict handling and
// SpecRules validation/readiness. No Unity or game objects are involved.
internal static class SupportCoreChecks
{
    internal static void Run(Action<bool, string> check)
    {
        // ---- SpecRules ----
        check(SpecRules.ValidSpec(62005, "StellarFunctionCall"), "valid spec accepted");
        check(!SpecRules.ValidSpec(0, "X"), "zero id rejected");
        check(!SpecRules.ValidSpec(62005, null) && !SpecRules.ValidSpec(62005, ""), "missing asset rejected");
        check(SpecRules.Evaluate(0, 0, 0) == SpecRules.Readiness.Awaiting, "empty registration awaits");
        check(SpecRules.Evaluate(2, 0, 4) == SpecRules.Readiness.Awaiting, "partial build awaits");
        check(SpecRules.Evaluate(4, 0, 4) == SpecRules.Readiness.Ready, "all built is ready");
        check(SpecRules.Evaluate(3, 1, 4) == SpecRules.Readiness.Degraded, "partial failure is degraded not ready");
        check(SpecRules.Evaluate(0, 4, 4) == SpecRules.Readiness.Degraded, "total failure is degraded");

        // ---- CatalogueMerge ----
        var oursA = new object();
        var oursB = new object();
        var foreign = new object();
        var stock = new object();
        var registered = new Dictionary<ushort, object> { [62005] = oursA, [62006] = oursB };

        // Missing ids are inserted; unrelated stock entries preserved.
        var existing = new Dictionary<ushort, object> { [45] = stock };
        var plan = CatalogueMerge.Plan(existing, registered);
        check(plan.Merged != null && plan.Conflicts == null, "merge inserts missing ids without conflicts");
        check(plan.Merged.Count == 3 && ReferenceEquals(plan.Merged[45], stock), "merge preserves unrelated entries");
        check(
            ReferenceEquals(plan.Merged[62005], oursA) && ReferenceEquals(plan.Merged[62006], oursB),
            "merge inserts owned objects"
        );
        check(!existing.ContainsKey(62005), "plan does not mutate the source dictionary");

        // Already-present owned references: no rewrite, no conflict.
        var warm = new Dictionary<ushort, object>
        {
            [45] = stock,
            [62005] = oursA,
            [62006] = oursB,
        };
        plan = CatalogueMerge.Plan(warm, registered);
        check(plan.Merged == null && plan.Conflicts == null, "warm provider needs no rewrite");

        // Foreign same-id entries are reported and never overwritten.
        var clash = new Dictionary<ushort, object> { [62005] = foreign, [62006] = oursB };
        plan = CatalogueMerge.Plan(clash, registered);
        check(
            plan.Conflicts != null && plan.Conflicts.Count == 1 && plan.Conflicts[0] == 62005,
            "foreign id reported once"
        );
        check(plan.Merged == null || ReferenceEquals(plan.Merged[62005], foreign), "foreign entry never overwritten");

        // Mixed: one missing insert plus one foreign conflict in a single plan.
        var mixed = new Dictionary<ushort, object> { [62005] = foreign };
        var registered3 = new Dictionary<ushort, object>
        {
            [62005] = oursA,
            [62006] = oursB,
            [62007] = new object(),
        };
        plan = CatalogueMerge.Plan(mixed, registered3);
        check(
            plan.Merged != null && plan.Merged.ContainsKey(62006) && plan.Merged.ContainsKey(62007),
            "missing ids still inserted beside a conflict"
        );
        check(
            ReferenceEquals(plan.Merged[62005], foreign) && plan.Conflicts.Count == 1,
            "conflict preserved while others merge"
        );

        // ---- MenuPlan ----
        // tabs -> rows -> parts, using strings as stand-ins for PartSettings.
        string[][][] Build(params string[][][] tabs) => tabs;

        // Anchor row has room: insert directly after the anchor.
        var tabs = Build(
            new[] { new[] { "Engine", "Sensor" }, new[] { "Hull" } },
            new[] { new[] { "Radio", "Armor" } }
        );
        bool placed = MenuPlan.TryPlace(
            tabs,
            r => (IReadOnlyList<string>)r,
            p => p == "Sensor",
            8,
            out int ti,
            out int ri,
            out int ii
        );
        check(placed && ti == 0 && ri == 0 && ii == 2, "anchor placement inserts after anchor in its row");

        // Anchor row full: roomiest row in the anchor's tab wins.
        tabs = Build(
            new[] { new[] { "a", "b", "c", "d", "e", "f", "g", "h" }, new[] { "x" } },
            new[] { new[] { "y", "z" } }
        );
        placed = MenuPlan.TryPlace(tabs, r => (IReadOnlyList<string>)r, p => p == "a", 8, out ti, out ri, out ii);
        check(placed && ti == 0 && ri == 1 && ii == 1, "full anchor row falls back to roomiest row in same tab");

        // Anchor tab full: global fallback picks first row with space.
        tabs = Build(new[] { new[] { "a", "b", "c", "d", "e", "f", "g", "h" } }, new[] { new[] { "y" } });
        placed = MenuPlan.TryPlace(tabs, r => (IReadOnlyList<string>)r, p => p == "a", 8, out ti, out ri, out ii);
        check(placed && ti == 1 && ri == 0 && ii == 1, "full menu falls back to any row with space");

        // No anchor at all: last tab preferred, roomiest row inside it.
        tabs = Build(new[] { new[] { "a" } }, new[] { new[] { "b", "c" }, new string[0] });
        placed = MenuPlan.TryPlace(tabs, r => (IReadOnlyList<string>)r, p => p == "missing", 8, out ti, out ri, out ii);
        check(placed && ti == 1 && ri == 0 && ii == 2, "missing anchor prefers last tab's roomiest row");

        // Completely full menu: placement refused.
        tabs = Build(new[] { new[] { "a", "b", "c", "d", "e", "f", "g", "h" } });
        placed = MenuPlan.TryPlace(tabs, r => (IReadOnlyList<string>)r, p => p == "a", 8, out ti, out ri, out ii);
        check(!placed, "full menu refuses placement");

        // Null/empty rows are skipped, not crashed on.
        tabs = Build(new string[][] { null, new string[0] }, new[] { new[] { "solo" } });
        placed = MenuPlan.TryPlace(
            tabs,
            r => r == null ? null : (IReadOnlyList<string>)r,
            p => p == "solo",
            8,
            out ti,
            out ri,
            out ii
        );
        check(placed && ti == 1 && ri == 0 && ii == 1, "null rows skipped during placement");

        // Insert index lands after the FIRST anchor match, matching legacy behavior.
        tabs = Build(new[] { new[] { "pre", "Sensor", "mid", "Sensor", "post" } });
        placed = MenuPlan.TryPlace(tabs, r => (IReadOnlyList<string>)r, p => p == "Sensor", 8, out ti, out ri, out ii);
        check(placed && ii == 2, "insert index follows first anchor match");

        // ---- MeshPlan ----
        check(MeshPlan.Route("text", null) == MeshRoute.Text, "text shape routes to text builder");
        check(MeshPlan.Route("TEXT", null) == MeshRoute.Text, "text shape match is case-insensitive");
        check(MeshPlan.Route("screen", null) == MeshRoute.Screen, "screen shape routes to screen builder");
        check(MeshPlan.Route(null, "channel") == MeshRoute.Screen, "screen channel routes to screen builder");
        check(MeshPlan.Route("cube", "channel") == MeshRoute.Screen, "channel beats geometry shape");
        check(MeshPlan.Route("cube", null) == MeshRoute.Geometry, "plain shape routes to geometry");
        check(MeshPlan.Route(null, null) == MeshRoute.Geometry, "empty mesh routes to geometry");

        // ---- FitMath ----
        check(FitMath.ScaleRatio(1f, 2f, 4f) == 0.5f, "scale ratio multiplies world by from/to");
        check(FitMath.ScaleRatio(1f, 2f, 0f) == 1f, "scale ratio guards zero divisor");
        check(FitMath.Ratio(3f, 0f) == 1f, "ratio guards zero divisor");
        check(FitMath.Volume(2f, 3f, 4f) == 24f, "volume is absolute product");
        check(FitMath.Volume(-1f, 2f, -3f) == 6f, "volume is absolute");
        check(FitMath.DominantAxis(0f, -5f, 2f, out float s) == 1 && s == -1f, "dominant axis picks y with sign");
        check(FitMath.DominantAxis(3f, 0f, 0f, out s) == 0 && s == 1f, "dominant axis picks x with sign");
        check(FitMath.DominantAxis(1f, 1f, 0f, out s) == -1, "tied axes resolve to zero vector");
        check(FitMath.DominantAxis(0f, 0f, 0f, out s) == -1, "zero vector has no dominant axis");

        // ---- SeatMath ----
        // Probe direction u = dominant axis of dir; clearance is the socket's
        // distance beyond the bounds surface along u. Bounds centre 0, size 1
        // -> half extent 0.5; standoff 0.06; seated threshold 0.01; pad sink 0.04.

        // Flush on the +x face, probing +x: shifted out by the standoff.
        var seat = SeatMath.Evaluate(0.5f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f, 0f, 0f);
        check(seat.HasAxis && seat.Axis == 0 && seat.AxisShift > 0f, "flush +x socket shifts outward along +x");
        check(seat.PadDistance > 0f, "seated socket still requests a mounting pad");

        // Flush on the -x face, probing -x: symmetric result. The inherited
        // signed-dot formula produced a full-width phantom clearance here.
        seat = SeatMath.Evaluate(-0.5f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, -1f, 0f, 0f);
        check(seat.HasAxis && seat.Axis == 0 && seat.AxisShift < 0f, "flush -x socket shifts outward along -x");
        check(seat.PadDistance > 0f && seat.PadDistance < 0.2f, "negative-axis clearance is not inflated");

        // Deep inside the part probing -x: pushed out along -x, not +x.
        seat = SeatMath.Evaluate(-0.1f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, -1f, 0f, 0f);
        check(seat.AxisShift < 0f, "interior -x socket shifts toward -x");

        // Well outside the bounds: no standoff shift, pad covers the gap.
        seat = SeatMath.Evaluate(0.9f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f, 0f, 0f);
        check(seat.AxisShift == 0f && seat.PadDistance > 0.3f, "floating socket keeps its position and pads the gap");

        // Degenerate probe direction: no seating at all.
        seat = SeatMath.Evaluate(0.5f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f, 1f, 0f);
        check(!seat.HasAxis, "tied probe direction disables seating");

        // ---- TechRules ----
        check(TechRules.ValidSpec(62008, "Circuit Functions"), "valid tech spec accepted");
        check(
            !TechRules.ValidSpec(0, "X") && !TechRules.ValidSpec(62008, null) && !TechRules.ValidSpec(62008, ""),
            "invalid tech specs rejected"
        );

        // Application plan (the real decision ResearchService.Apply executes):
        // first-wins per id — the first occurrence of an intra-set duplicate
        // applies, later ones do not, and ids already owned by the existing
        // native/foreign list never apply.
        var applyPlan = TechRules.PlanApply(new ushort[] { 62008, 62008, 7 }, new ushort[] { 7, 9 });
        check(
            applyPlan.SequenceEqual(new[] { true, false, false }),
            "plan apply keeps first duplicate and rejects native-owned ids"
        );
        check(
            TechRules.PlanApply(new ushort[] { 62008, 62009 }, new ushort[] { 7 }).All(x => x),
            "clean spec set applies fully"
        );
        check(
            TechRules.PlanApply(new ushort[] { 5, 5, 5 }, new ushort[0]).SequenceEqual(new[] { true, false, false }),
            "triple duplicate applies exactly once"
        );
        check(
            TechRules.PlanApply(new ushort[] { 7, 62008 }, new ushort[] { 7 }).SequenceEqual(new[] { false, true }),
            "native-owned id rejected while free id applies"
        );

        // Unlock merge: appends only missing references, deduplicates wanted.
        var a = new object();
        var b = new object();
        var c = new object();
        var merged = TechRules.MergeAdditions(new[] { a }, new[] { a, b, b, c });
        check(
            merged.Count == 2 && ReferenceEquals(merged[0], b) && ReferenceEquals(merged[1], c),
            "merge additions appends only missing unique parts"
        );
        check(
            TechRules.MergeAdditions(new[] { a, b }, new[] { a, b }).Count == 0,
            "merge additions is a no-op when all parts present"
        );

        // Ownership-aware restore: only tracked references are removed;
        // foreign additions and ordering of the rest are preserved.
        var foreignPart = new object();
        var current = new[] { a, foreignPart, b, c };
        var restored = TechRules.RemoveOwned(current, new[] { b, c });
        check(
            restored.Length == 2 && ReferenceEquals(restored[0], a) && ReferenceEquals(restored[1], foreignPart),
            "remove owned drops only our parts and keeps foreign additions"
        );
        check(
            TechRules.RemoveOwned(current, new object[0]).SequenceEqual(current),
            "remove owned with empty set preserves array"
        );
        check(TechRules.RemoveOwned(null, new[] { a }) == null, "remove owned tolerates null array");

        // Grant decision.
        check(TechRules.GrantDue(true, false), "grant due when prerequisite researched and target not");
        check(
            !TechRules.GrantDue(false, false) && !TechRules.GrantDue(true, true) && !TechRules.GrantDue(false, true),
            "grant suppressed without prerequisite or when already researched"
        );

        // ---- PartTracking ----
        var tracking = new PartTracking();
        check(tracking.Mark(7, 62005), "first observation reports added edge");
        check(!tracking.Mark(7, 62005), "repeated observation suppresses duplicate add");
        check(tracking.Mark(7, 62006), "second distinct part reports added edge");
        check(tracking.HasPending(7), "ship with tracked ids reports pending");
        var removed = tracking.CollectRemoved(7, new HashSet<ushort> { 62006 });
        check(removed.SequenceEqual(new ushort[] { 62005 }), "removal returns only absent ids");
        check(tracking.HasPending(7), "ship keeps remaining tracked ids pending");
        check(
            tracking.CollectRemoved(7, new ushort[0]).SequenceEqual(new ushort[] { 62006 }),
            "second pass removes the rest"
        );
        check(!tracking.HasPending(7) && tracking.ShipCount == 0, "emptied ship entry is dropped, not retained");
        check(tracking.CollectRemoved(7, new ushort[0]).Count == 0, "repeated removal pass is quiet");
        tracking.Mark(7, 62005);
        check(tracking.Mark(9, 62005), "different ship tracks the same part id independently");
        check(tracking.Mark(9, 62007), "second ship first observation reports added");
        check(tracking.ShipCount == 2, "both ships tracked");
        tracking.Clear();
        check(
            tracking.ShipCount == 0 && !tracking.HasPending(7) && !tracking.HasPending(9),
            "world reset clears all ship tracking"
        );
        tracking.Mark(3, 1);
        check(tracking.HasPending(3), "fresh ship after reset tracks again");
        tracking.CollectRemoved(3, new ushort[0]);
        check(tracking.ShipCount == 0 && tracking.Mark(3, 1), "re-added part on emptied ship fires a fresh added edge");

        // ---- FailureBudget ----
        var budget = new FailureBudget(5);
        check(!budget.Muted(62005), "part starts unmuted");
        check(budget.Failed(62005) == FailureBudget.Outcome.First, "first failure reported once");
        check(budget.Failed(62005) == FailureBudget.Outcome.None, "second failure is silent");
        check(budget.Failed(62005) == FailureBudget.Outcome.None, "third failure is silent");
        check(budget.Failed(62005) == FailureBudget.Outcome.None, "fourth failure still silent");
        check(
            budget.Failed(62005) == FailureBudget.Outcome.Muted && budget.Muted(62005),
            "fifth failure mutes the part"
        );
        check(budget.Failed(62005) == FailureBudget.Outcome.None, "post-mute failures stay silent");
        check(budget.Failed(62006) == FailureBudget.Outcome.First, "mute budget is per part id");
        budget.Reset();
        check(!budget.Muted(62005) && budget.Failed(62005) == FailureBudget.Outcome.First, "reset restores the budget");

        // ---- ScreenText name codec ----
        check(
            ScreenText.FormatName("stellar-functions.status", 16, 6f) == "SDScreen|stellar-functions.status|16|6",
            "format name matches the legacy SDScreen layout"
        );
        check(
            ScreenText.ParseName("SDScreen|ch|20|8.5", out string pc, out int pcol, out float psp)
                && pc == "ch"
                && pcol == 20
                && System.Math.Abs(psp - 8.5f) < 0.0001f,
            "parse name round-trips a formatted name"
        );
        check(ScreenText.FormatName("ch", 20, 8.5f) == "SDScreen|ch|20|8.5", "format/parse are byte-compatible");
        check(!ScreenText.ParseName("sdscreen|ch|20|8", out _, out _, out _), "prefix match is ordinal case-sensitive");
        check(
            !ScreenText.ParseName(null, out _, out _, out _) && !ScreenText.ParseName("Panel", out _, out _, out _),
            "null and non-screen names do not parse"
        );
        check(
            ScreenText.ParseName("SDScreen|", out string ec, out int ecol, out float esp)
                && ec == ""
                && ecol == 16
                && esp == 6f,
            "bare prefix keeps legacy defaults"
        );
        check(
            ScreenText.ParseName("SDScreen|ch|bad|6", out _, out int badCol, out _) && badCol == 0,
            "malformed columns reproduces the legacy TryParse zero quirk"
        );
        check(
            ScreenText.ParseName("SDScreen|ch|16|bad", out _, out _, out float badSpd) && badSpd == 0f,
            "malformed speed reproduces the legacy TryParse zero quirk"
        );
        check(
            ScreenText.ParseName("SDScreen|ch", out string only, out int dcol, out float dspd)
                && only == "ch"
                && dcol == 16
                && dspd == 6f,
            "missing trailing fields keep defaults"
        );

        // ---- ScreenText ticker/mono ----
        check(
            ScreenText.Scrolls("abc", true)
                && !ScreenText.Scrolls("", true)
                && !ScreenText.Scrolls(null, true)
                && !ScreenText.Scrolls("abc", false),
            "scroll decision requires scroll flag and content"
        );
        const string ticker = "abcdef     ";
        check(ScreenText.Window(ticker, 4, 0f) == "abcd", "window starts at offset zero");
        check(ScreenText.Window(ticker, 4, 2f) == "cdef", "window honours a character offset");
        check(ScreenText.Window(ticker, 4, 10f) == " abc", "window wraps inside the ticker");
        check(ScreenText.Window(ticker, 12, 9f) == "  abcdef    ", "window wraps past ticker end");
        check(
            System.Math.Abs(ScreenText.Advance(10.9f, 0.1f, 6f, 11) - (10.9f + 0.6f) % 11) < 0.0001f,
            "advance accumulates delta*speed modulo ticker length"
        );
        check(
            ScreenText.Mono("a<b c") == "<mspace=0.62em>a&lt;b\u00A0c</mspace>",
            "mono markup escapes '<' and converts spaces to non-breaking"
        );
        check(ScreenText.Mono("") == "<mspace=0.62em></mspace>", "mono markup on empty text");

        // ---- ChannelProviders registry ----
        var channels = new ChannelProviders<string, string>();
        Func<string, string> alpha = s => "alpha";
        Func<string, string> beta = s => "beta";
        channels.Provide("chan", alpha);
        check(
            channels.Count == 1 && channels.TryGetProvider("CHAN", out var got) && got.Equals(alpha),
            "provider lookup is case-insensitive like the legacy registry"
        );
        check(
            channels.Unprovide("chan", beta) == false && channels.Count == 1,
            "unprovide with a different delegate keeps the provider"
        );
        check(
            channels.Unprovide("chan", alpha) && channels.Count == 0,
            "unprovide with the registered delegate removes it"
        );
        // Equivalent instance-method delegates: same target and method but
        // distinct delegate instances — delegate equality must still match.
        var bound = new BoundProvider("x");
        Func<string, string> first = bound.Produce;
        Func<string, string> second = bound.Produce;
        check(
            first.Equals(second) && !ReferenceEquals(first, second),
            "equivalent instance-method delegates compare equal without being identical"
        );
        channels.Provide("chan", first);
        check(
            channels.Unprovide("chan", second) && channels.Count == 0,
            "unprovide matches an equivalent delegate instance"
        );
        channels.Provide("chan", first);
        channels.Provide("chan", beta);
        check(
            channels.Unprovide("chan", first) == false
                && channels.Count == 1
                && channels.TryGetProvider("chan", out var kept)
                && kept.Equals(beta),
            "an old provider cannot unregister its replacement"
        );
        int before = channels.Count;
        channels.Provide("", s => s);
        channels.Provide(null, s => s);
        channels.Provide("chan", null);
        check(channels.Count == before, "empty channel and null provider are rejected");
        check(channels.Unprovide("missing", s => s) == false, "unprovide of an absent channel is a no-op");
        check(
            channels.MarkProviderError("bad")
                && !channels.MarkProviderError("bad")
                && channels.MarkProviderError("bad2"),
            "provider error marking is once per channel"
        );

        // ---- LogGate release severity floor ----
        check(
            LogGate.Level("TRACE") == LogGate.Trace
                && LogGate.Level("INFO") == LogGate.Info
                && LogGate.Level("WARN") == LogGate.Warn
                && LogGate.Level("ERROR") == LogGate.Error
                && LogGate.Level("anything-else") == LogGate.Trace,
            "level names map to ordered severities"
        );
        // Default release floor (Warn): Trace/Info hidden, Warn/Error shown.
        check(
            !LogGate.Visible(LogGate.Trace, LogGate.Warn, false)
                && !LogGate.Visible(LogGate.Info, LogGate.Warn, false)
                && LogGate.Visible(LogGate.Warn, LogGate.Warn, false)
                && LogGate.Visible(LogGate.Error, LogGate.Warn, false),
            "default floor shows warnings and errors only"
        );
        // Diagnostics enabled: every level visible regardless of floor.
        check(
            LogGate.Visible(LogGate.Trace, LogGate.Warn, true)
                && LogGate.Visible(LogGate.Info, LogGate.Warn, true)
                && LogGate.Visible(LogGate.Error, LogGate.Warn, true),
            "diagnostics enable shows all levels"
        );
        // Custom floors still gate when diagnostics are off.
        check(
            !LogGate.Visible(LogGate.Warn, LogGate.Error, false)
                && LogGate.Visible(LogGate.Error, LogGate.Error, false),
            "error-only floor suppresses warnings"
        );
        check(LogGate.Visible(LogGate.Trace, LogGate.Trace, false), "trace floor shows everything");
    }

    private sealed class BoundProvider
    {
        private readonly string _tag;

        public BoundProvider(string tag)
        {
            _tag = tag;
        }

        public string Produce(string screen) => _tag;
    }
}
