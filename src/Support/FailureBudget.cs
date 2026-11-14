using System.Collections.Generic;

namespace StellarFunctions.Support;

// Pure per-part failure budget for repeated server-tick exceptions.
// First failure and the mute threshold are reported once each; muted parts
// stop ticking until Reset. No Unity or game types.
public sealed class FailureBudget
{
    public enum Outcome { None, First, Muted }

    private readonly Dictionary<ushort, int> _errors = new Dictionary<ushort, int>();
    private readonly int _muteAfter;

    public FailureBudget(int muteAfter) => _muteAfter = muteAfter;

    public bool Muted(ushort partId)
        => _errors.TryGetValue(partId, out int errors) && errors >= _muteAfter;

    // Records one failure; First fires on the first error, Muted exactly when
    // the count reaches the threshold, None otherwise.
    public Outcome Failed(ushort partId)
    {
        _errors.TryGetValue(partId, out int errors);
        errors++;
        _errors[partId] = errors;
        if (errors == 1) return Outcome.First;
        if (errors == _muteAfter) return Outcome.Muted;
        return Outcome.None;
    }

    public void Reset() => _errors.Clear();
}
