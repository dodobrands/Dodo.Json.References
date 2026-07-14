namespace Dodo.Json.References;

internal static class InterlockedMath
{
    // CAS loop: a check-then-write max lets a stale reader regress the high-water mark.
    public static void Max(ref int location, int value)
    {
        var observed = Volatile.Read(ref location);
        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref location, value, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }
}
