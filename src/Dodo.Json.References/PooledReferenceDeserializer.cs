using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.ObjectPool;

namespace Dodo.Json.References;

/// <summary>Each retained lease pins a warm type-info graph and a payload-sized resolver map; keep maxRetained low when creating many instances.</summary>
public sealed class PooledReferenceDeserializer<T>
{
    private readonly ObjectPool<ReferenceLease<T>> _pool;

    /// <summary>The per-lease type info comes from the options' own resolver; use the factory overload to pin a context.</summary>
    public PooledReferenceDeserializer(JsonSerializerOptions baseOptions, int? maxRetained = null)
        : this(baseOptions, static o => (JsonTypeInfo<T>)o.GetTypeInfo(typeof(T)), maxRetained)
    {
    }

    /// <remarks>Base options are snapshotted — later mutations are not observed; the first lease is built eagerly (fail-fast).</remarks>
    public PooledReferenceDeserializer(
        JsonSerializerOptions baseOptions,
        Func<JsonSerializerOptions, JsonTypeInfo<T>> typeInfoFactory,
        int? maxRetained = null)
    {
        _pool = ObjectPoolFactory.Create(new ReferenceLeasePolicy<T>(baseOptions, typeInfoFactory), maxRetained);
        _pool.Return(_pool.Get());
    }

    public async ValueTask<T?> Deserialize(Stream input, CancellationToken ct = default)
    {
        var lease = _pool.Get();
        try
        {
            return await JsonSerializer.DeserializeAsync(input, lease.TypeInfo, ct);
        }
        finally
        {
            _pool.Return(lease);
        }
    }
}
