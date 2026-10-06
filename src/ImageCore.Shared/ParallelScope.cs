namespace ImageCoreService;

/// <summary>
/// How many threads the parallel loops of this library may use, for the current flow of work (async-local: it follows
/// awaits and Task.Run into the loops, and does not leak to other callers). Background work (saving full-size pages)
/// caps itself so the picture the user is editing keeps the other cores and stays smooth; interactive work is uncapped.
/// </summary>
public static class ParallelScope
{
    private static readonly AsyncLocal<int> Cap = new();
    private static readonly ParallelOptions Unbounded = new();

    /// <summary>Options for Parallel.For honoring the current cap.</summary>
    public static ParallelOptions Options
    {
        get
        {
            int cap = Cap.Value;
            return cap > 0 ? new ParallelOptions { MaxDegreeOfParallelism = cap } : Unbounded;
        }
    }

    /// <summary>Limits the loops started by this flow to <paramref name="threads"/> threads until disposed.</summary>
    public static Scope Limit(int threads)
    {
        int previous = Cap.Value;
        Cap.Value = Math.Max(1, threads);
        return new Scope(previous);
    }

    public readonly struct Scope(int previous) : IDisposable
    {
        public void Dispose() => Cap.Value = previous;
    }
}
