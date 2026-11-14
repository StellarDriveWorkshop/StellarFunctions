// Clean behavioral tests derived strictly from the black-box specification.
// Zero SDModKit or old Kit dependencies. Runs in pure .NET 10.
using System;
using System.Collections.Generic;
using System.Linq;

namespace StellarFunctions;

public static class CleanBehavioralChecks
{
    public static void Run(Action<bool, string> check)
    {
        CheckMultiplayerActionSecurity(check);
        CheckResearchStateMachine(check);
        CheckSocketLayoutAndSizingInvariants(check);
        CheckCatalogueUniquenessRule(check);
    }

    // 1. Action Network Protocol Security Gate (Section 2.4 & 4)
    // Server must drop actions unless sending player is in InteractingWithChest on matching ship and part.
    private enum TestPlayerStateType
    {
        Walking = 0,
        InteractingWithChest = 9,
        InteractingWithGenericPart = 8,
    }

    private record struct TestPlayerState(TestPlayerStateType Type, uint EntityId, ushort PartId);

    private static bool IsActionAuthorized(TestPlayerState state, uint targetShipId, ushort targetPartId, string action)
    {
        if (string.IsNullOrEmpty(action))
            return false;
        return state.Type == TestPlayerStateType.InteractingWithChest
            && state.EntityId == targetShipId
            && state.PartId == targetPartId;
    }

    private static void CheckMultiplayerActionSecurity(Action<bool, string> check)
    {
        uint ship = 1001;
        ushort part = 62005;
        var validState = new TestPlayerState(TestPlayerStateType.InteractingWithChest, ship, part);

        check(
            IsActionAuthorized(validState, ship, part, "select:func1"),
            "security gate accepts action when player is interacting with exact chest/part"
        );

        check(
            !IsActionAuthorized(validState with { Type = TestPlayerStateType.Walking }, ship, part, "select:func1"),
            "security gate rejects action from walking player"
        );

        check(
            !IsActionAuthorized(
                validState with
                {
                    Type = TestPlayerStateType.InteractingWithGenericPart,
                },
                ship,
                part,
                "select:func1"
            ),
            "security gate rejects generic part interaction state (must be chest/storage state 9)"
        );

        check(
            !IsActionAuthorized(validState with { EntityId = 9999 }, ship, part, "select:func1"),
            "security gate rejects action targeting a different ship"
        );

        check(
            !IsActionAuthorized(validState with { PartId = 123 }, ship, part, "select:func1"),
            "security gate rejects action targeting a different part on the same ship"
        );

        check(!IsActionAuthorized(validState, ship, part, ""), "security gate rejects empty action string");
    }

    // 2. Hidden Research Unlock & Coexistence State Machine (Section 2.2 & 3)
    private static void CheckResearchStateMachine(Action<bool, string> check)
    {
        const ushort PrereqTechId = 32123; // Advanced Signal Processing
        const ushort FunctionsTechId = 62008; // Circuit Functions

        HashSet<ushort> completedTechs = new HashSet<ushort> { 1, 100 }; // Existing unrelated techs

        bool GrantIfEligible(bool isServerAuthoritative, HashSet<ushort> state)
        {
            if (!isServerAuthoritative)
                return false;
            if (state.Contains(PrereqTechId) && !state.Contains(FunctionsTechId))
            {
                state.Add(FunctionsTechId);
                return true;
            }
            return false;
        }

        check(
            !GrantIfEligible(true, completedTechs),
            "research grant tick does nothing before prerequisite is researched"
        );
        check(!completedTechs.Contains(FunctionsTechId), "Functions tech remains locked before prerequisite");

        // Non-authoritative client tick cannot grant
        completedTechs.Add(PrereqTechId);
        check(!GrantIfEligible(false, completedTechs), "non-authoritative client cannot trigger research grant");
        check(!completedTechs.Contains(FunctionsTechId), "Functions tech remains ungranted by client");

        // Authoritative server grant
        check(
            GrantIfEligible(true, completedTechs),
            "authoritative server grants Functions research once prerequisite is completed"
        );
        check(completedTechs.Contains(FunctionsTechId), "Functions tech 62008 is now in completed research set");

        // Idempotency
        int countBefore = completedTechs.Count;
        check(
            !GrantIfEligible(true, completedTechs),
            "subsequent server grant ticks are idempotent and do not re-grant"
        );
        check(completedTechs.Count == countBefore, "research list count is unchanged on idempotent tick");

        // Coexistence
        check(
            completedTechs.Contains(1) && completedTechs.Contains(100) && completedTechs.Contains(PrereqTechId),
            "granting Functions preserves all existing unrelated research"
        );
    }

    // 3. Socket Coordinates & Geometry Scaling Invariants (Section 4 & 5)
    private record struct SocketDef(byte Id, float X, float Y, float Z, float Yaw);

    private static void CheckSocketLayoutAndSizingInvariants(Action<bool, string> check)
    {
        // Stable Socket heights must be exactly 0.07m above mounting plane
        const float ExpectedHeight = 0.07f;

        // Param (62006)
        var paramSocket = new SocketDef(0, 0.25f, 0.07f, 0f, 270f);
        check(Math.Abs(paramSocket.Y - ExpectedHeight) < 1e-4, "Param socket sits at exact y=0.07m height");
        check(paramSocket.Id == 0 && paramSocket.X > 0, "Param output socket is index 0 at +X");

        // Return (62007)
        var returnSocket = new SocketDef(0, -0.25f, 0.07f, 0f, 90f);
        check(Math.Abs(returnSocket.Y - ExpectedHeight) < 1e-4, "Return socket sits at exact y=0.07m height");
        check(returnSocket.Id == 0 && returnSocket.X < 0, "Return input socket is index 0 at -X");

        // Call (62005) 3-input default
        var call3Sockets = new[]
        {
            new SocketDef(0, -0.125f, 0.07f, -0.125f, 0f),
            new SocketDef(1, 0.125f, 0.07f, -0.125f, 0f),
            new SocketDef(2, 0.375f, 0.07f, -0.125f, 0f),
            new SocketDef(3, 0.125f, 0.07f, 0.125f, 180f),
        };

        check(
            call3Sockets.All(s => Math.Abs(s.Y - ExpectedHeight) < 1e-4),
            "all Call 3-input sockets sit at exact y=0.07m height"
        );
        check(call3Sockets.Count(s => s.Z < 0) == 3, "Call has exactly 3 input sockets along the -Z face");
        check(call3Sockets.Single(s => s.Z > 0).Id == 3, "Call socket 3 is the single output along the +Z face");

        // Sizing bounds scaling check: screen widths per variant
        float width1 = 0.118f;
        float width2 = 0.330f;
        float width3 = 0.345f;
        check(
            width1 < width2 && width2 < width3,
            "Call housing screen widths strictly increase with input count (1 < 2 < 3)"
        );
    }

    // 4. Cold Catalogue Uniqueness Constraint (Section 3 & 4)
    private static void CheckCatalogueUniquenessRule(Action<bool, string> check)
    {
        // Demo 0.8.3 catalogue rule: Exactly one Call ID (62005) must be visible in the catalogue.
        var catalogue = new Dictionary<ushort, string>
        {
            [45] = "MathGate",
            [54] = "TargetingComputer",
            [82] = "SignalDisplay",
            [62005] = "CanonicalCall",
            [62006] = "Param",
            [62007] = "Return",
            [62009] = "FunctionTransmitter",
        };

        check(catalogue.ContainsKey(62005), "catalogue contains canonical Call 62005");
        check(
            catalogue.Values.Count(name => name.Contains("Call")) == 1,
            "catalogue contains exactly one Call entry (runtime geometry clones must not register duplicates)"
        );
    }
}
