namespace StellarFunctions.Support;

// Pure socket-seating decision math. Given a socket's prefab-space position,
// the part bounds and the socket's plug direction, decides the standoff
// shift and whether a mounting pad is needed. Float-only so checks/core
// exercises the same code the game runs; SocketSeating applies the result
// to Unity transforms.
public static class SeatMath
{
    public const float SocketStandoff = 0.06f;
    private const float PadSink = 0.04f;
    private const float Seated = 0.01f;

    public readonly struct Plan
    {
        public readonly bool HasAxis;
        public readonly int Axis;

        // Signed prefab-space delta to add to the socket position on Axis.
        public readonly float AxisShift;

        // >0 requests a mounting pad bridging this distance.
        public readonly float PadDistance;

        public Plan(bool hasAxis, int axis, float axisShift, float padDistance)
        {
            HasAxis = hasAxis;
            Axis = axis;
            AxisShift = axisShift;
            PadDistance = padDistance;
        }
    }

    // local*  = socket position in prefab space
    // center* = bounds.center in prefab space
    // size*   = bounds.bounds full extents in prefab space
    // dir*    = negated socket forward in prefab space (the seating probe
    //           direction; dominant axis wins, ties mean no seating)
    public static Plan Evaluate(
        float localX,
        float localY,
        float localZ,
        float centerX,
        float centerY,
        float centerZ,
        float sizeX,
        float sizeY,
        float sizeZ,
        float dirX,
        float dirY,
        float dirZ
    )
    {
        int axis = FitMath.DominantAxis(dirX, dirY, dirZ, out float sign);
        if (axis < 0)
        {
            return default;
        }

        float local =
            axis == 0 ? localX
            : axis == 1 ? localY
            : localZ;
        float center =
            axis == 0 ? centerX
            : axis == 1 ? centerY
            : centerZ;
        float size =
            axis == 0 ? sizeX
            : axis == 1 ? sizeY
            : sizeZ;

        // Half extent is always positive: the legacy signed dot product gave
        // a negative half extent for sockets facing a negative axis, which
        // overstated clearance by a full width and requested huge pads.
        float clearance = sign * (local - center) - size * 0.5f;
        float shift = 0f;
        if (clearance < SocketStandoff)
        {
            shift = sign * (SocketStandoff - clearance);
            clearance = SocketStandoff;
        }
        return new Plan(true, axis, shift, clearance > Seated ? clearance + PadSink : 0f);
    }
}
