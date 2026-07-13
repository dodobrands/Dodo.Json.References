using Microsoft.Extensions.ObjectPool;

namespace Dodo.Json.References;

internal static class ObjectPoolFactory
{
    // Floor keeps small pods useful under concurrency; ceiling caps pinned memory on big hosts.
    private const int RetentionFloor = 16;
    private const int RetentionCeiling = 64;

    public static int DefaultMaxRetained { get; } =
        Math.Clamp(Environment.ProcessorCount * 2, RetentionFloor, RetentionCeiling);

    public static ObjectPool<T> Create<T>(IPooledObjectPolicy<T> policy, int? maxRetained = null) where T : class
        => new DefaultObjectPool<T>(policy, maxRetained ?? DefaultMaxRetained);

    // Retained uncapped: assumes trusted server-built documents — DefaultObjectPool never trims,
    // so a capacity valve must come back if untrusted input ever feeds these pools.
    public static ObjectPool<Dictionary<TKey, TValue>> CreateDictionaryPool<TKey, TValue>(
        int? maxRetained = null,
        IEqualityComparer<TKey>? comparer = null) where TKey : notnull
        => Create(new AlwaysReturnPooledDictionaryPolicy<TKey, TValue>(comparer: comparer), maxRetained);
}
