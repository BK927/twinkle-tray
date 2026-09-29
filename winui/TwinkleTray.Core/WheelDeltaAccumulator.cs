namespace TwinkleTray.Core;

/// <summary>Converts Windows wheel deltas into complete 120-unit detents without losing partial input.</summary>
/// <remarks>Keep one instance per input target. Callers serialize access on their input thread.</remarks>
public sealed class WheelDeltaAccumulator
{
    private int _remainder;

    public int Add(int delta)
    {
        // The retained remainder is in [-119, 119]; promote before addition so even
        // an Int32 boundary input cannot overflow or reverse its direction.
        long total = (long)_remainder + delta;
        int detents = (int)(total / 120);
        _remainder = (int)(total % 120);
        return detents;
    }

    public void Reset() => _remainder = 0;
}
