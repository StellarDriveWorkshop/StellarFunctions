namespace StellarFunctions.Support;

// Pure registration rules: spec validation and readiness evaluation. No Unity
// or game types so checks/core exercises the same code the game runs.
public static class SpecRules
{
    public static bool ValidSpec(ushort id, string asset) => id != 0 && !string.IsNullOrEmpty(asset);

    public enum Readiness
    {
        Awaiting,
        Ready,
        Degraded,
    }

    // Readiness means every required spec genuinely built. Any permanent
    // failure is degraded, never ready.
    public static Readiness Evaluate(int built, int failed, int total)
    {
        if (total <= 0 || built + failed < total)
        {
            return Readiness.Awaiting;
        }

        return failed > 0 ? Readiness.Degraded : Readiness.Ready;
    }
}
