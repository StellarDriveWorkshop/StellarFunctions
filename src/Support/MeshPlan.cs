namespace StellarFunctions.Support;

// Pure mesh routing decision: which construction path a MeshSpec takes.
// String-only inputs so checks/core exercises the same code the game runs.
public enum MeshRoute { Text, Screen, Geometry }

public static class MeshPlan
{
    // Mirrors the legacy priority: explicit "text" shape, then "screen" shape
    // or any screen channel, then donor/primitive geometry.
    public static MeshRoute Route(string shape, string screen)
    {
        if (string.Equals(shape, "text", System.StringComparison.OrdinalIgnoreCase)) return MeshRoute.Text;
        if (string.Equals(shape, "screen", System.StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(screen))
            return MeshRoute.Screen;
        return MeshRoute.Geometry;
    }
}
