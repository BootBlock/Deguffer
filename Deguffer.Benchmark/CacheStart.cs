namespace Deguffer.Benchmark;

/// <summary>What the duplicates route's checksum cache holds as each run starts.</summary>
internal enum CacheStart
{
    /// <summary>Nothing, as a first search finds it.</summary>
    Cold,

    /// <summary>
    /// What an untimed search over the same folders kept, loaded from its store, as the first search
    /// after the app starts again finds it.
    /// </summary>
    Warm,
}
