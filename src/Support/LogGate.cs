namespace StellarFunctions.Support;

// Pure severity-floor decision shared by the Support and Functions log
// facades. String-free so checks/core exercises the same policy the game
// runs: Trace=0, Info=1, Warn=2, Error=3; a message is visible when
// diagnostics are enabled or its level meets the floor.
public static class LogGate
{
    public const int Trace = 0;
    public const int Info = 1;
    public const int Warn = 2;
    public const int Error = 3;

    public static int Level(string name)
    {
        switch (name)
        {
            case "ERROR": return Error;
            case "WARN": return Warn;
            case "INFO": return Info;
            default: return Trace;
        }
    }

    public static bool Visible(int level, int floor, bool diagnostics)
        => diagnostics || level >= floor;
}
