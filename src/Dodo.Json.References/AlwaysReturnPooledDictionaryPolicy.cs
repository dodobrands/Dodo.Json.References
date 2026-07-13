using Microsoft.Extensions.ObjectPool;

namespace Dodo.Json.References;

internal sealed class AlwaysReturnPooledDictionaryPolicy<TKey, TValue>(
    int? capacity = null,
    IEqualityComparer<TKey>? comparer = null) : IPooledObjectPolicy<Dictionary<TKey, TValue>> where TKey : notnull
{
    public Dictionary<TKey, TValue> Create()
        => capacity.HasValue ? new(capacity.Value, comparer) : new(comparer);

    public bool Return(Dictionary<TKey, TValue> obj)
    {
        obj.Clear();
        return true;
    }
}
