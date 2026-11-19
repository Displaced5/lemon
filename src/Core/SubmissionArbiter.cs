namespace ClothingPairing.Core;

// Serialize independent requests and fairly rotate categories and facewear slots.
public sealed class SubmissionArbiter
{
    public const double MinimumInterval = 0.6;
    private int nextIndex;
    private double nextAllowed;

    public int? Select(IReadOnlyList<bool> ready, double now)
    {
        if (now < nextAllowed || ready.Count == 0) return null;
        for (var offset = 0; offset < ready.Count; offset++)
        {
            var index = (nextIndex + offset) % ready.Count;
            if (!ready[index]) continue;
            nextIndex = (index + 1) % ready.Count;
            return index;
        }
        return null;
    }

    public void Submitted(double now) => nextAllowed = now + MinimumInterval;
}
