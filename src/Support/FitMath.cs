namespace StellarFunctions.Support;

// Pure scale/volume math shared by the interaction-fit and socket-seating
// stages. Float-only so checks/core exercises the same code the game runs;
// callers pass component values and rebuild Vector3 results themselves.
public static class FitMath
{
    public static float ScaleRatio(float world, float from, float to)
        => System.Math.Abs(to) < 0.0001f ? 1f : world * from / to;

    public static float Ratio(float from, float to)
        => System.Math.Abs(to) < 0.0001f ? 1f : from / to;

    public static float Volume(float x, float y, float z)
        => System.Math.Abs(x * y * z);

    // Dominant axis of a direction vector: returns the axis index (0=x,1=y,2=z)
    // and its sign, or axis -1 when the vector is effectively zero.
    public static int DominantAxis(float x, float y, float z, out float sign)
    {
        float ax = System.Math.Abs(x), ay = System.Math.Abs(y), az = System.Math.Abs(z);
        if (ax > ay && ax > az) { sign = System.Math.Sign(x); return 0; }
        if (ay > ax && ay > az) { sign = System.Math.Sign(y); return 1; }
        if (az > ax && az > ay) { sign = System.Math.Sign(z); return 2; }
        sign = 0f;
        return -1;
    }
}
