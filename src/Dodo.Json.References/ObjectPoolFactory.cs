using Microsoft.Extensions.ObjectPool;

namespace Dodo.Json.References;

/// <summary>Object pools whose retained count is derived from the host's processor count.</summary>
public static class ObjectPoolFactory
{
    // Floor keeps small pods useful under concurrency; ceiling caps pinned memory on big hosts.
    private const int RetentionFloor = 16;
    private const int RetentionCeiling = 64;

    /// <summary>Two instances per processor, clamped to [16, 64].</summary>
    public static int DefaultMaxRetained { get; } =
        Math.Clamp(Environment.ProcessorCount * 2, RetentionFloor, RetentionCeiling);

    /// <summary>Creates a pool retaining <see cref="DefaultMaxRetained"/> instances unless <paramref name="maxRetained"/> overrides it.</summary>
    public static ObjectPool<T> Create<T>(IPooledObjectPolicy<T> policy, int? maxRetained = null) where T : class
        => new DefaultObjectPool<T>(policy, maxRetained ?? DefaultMaxRetained);

    /// <summary>Creates a dictionary pool that takes every instance back, however large it grew.</summary>
    /// <remarks>Uncapped retained capacity assumes trusted server-built documents; DefaultObjectPool never trims.</remarks>
    public static ObjectPool<Dictionary<TKey, TValue>> CreateDictionaryPool<TKey, TValue>(
        int? maxRetained = null,
        IEqualityComparer<TKey>? comparer = null) where TKey : notnull
        => Create(new AlwaysReturnPooledDictionaryPolicy<TKey, TValue>(comparer: comparer), maxRetained);
}
