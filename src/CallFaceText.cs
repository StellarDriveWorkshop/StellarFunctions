using System;

namespace StellarFunctions;

internal static class CallFaceText
{
    // The native cardinal rotation puts this screen plane upside down.
    internal const float FaceOrientationRoll = 180f;

    // Mirror Players.Utils.LookAtPlayerFlat4Sides in the surface's local
    // frame. Indices: 0=+Z, 1=+X, 2=-Z, 3=-X.
    internal static int Cardinal(float z, float x) => Math.Abs(z) > Math.Abs(x) ? (z > 0f ? 0 : 2) : (x > 0f ? 1 : 3);
}
