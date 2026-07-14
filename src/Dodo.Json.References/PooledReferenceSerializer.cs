using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.ObjectPool;

namespace Dodo.Json.References;

/// <summary>Create one instance per (base options, type) pair and reuse it; each lease pins a warm type-info graph.</summary>
public sealed class PooledReferenceSerializer<T>
{
    private readonly ObjectPool<ReferenceLease<T>> _pool;

    /// <summary>The per-lease type info comes from the options' own resolver; use the factory overload to pin a context.</summary>
    public PooledReferenceSerializer(JsonSerializerOptions baseOptions, int? maxRetained = null)
        : this(baseOptions, static o => (JsonTypeInfo<T>)o.GetTypeInfo(typeof(T)), maxRetained)
    {
    }

    /// <remarks>Base options are snapshotted — later mutations are not observed; the first lease is built eagerly (fail-fast).</remarks>
    public PooledReferenceSerializer(
        JsonSerializerOptions baseOptions,
        Func<JsonSerializerOptions, JsonTypeInfo<T>> typeInfoFactory,
        int? maxRetained = null)
    {
        _pool = ObjectPoolFactory.Create(new ReferenceLeasePolicy<T>(baseOptions, typeInfoFactory), maxRetained);
        _pool.Return(_pool.Get());
    }

    public async Task SerializeWithPointers(T payload, Stream output, CancellationToken ct = default)
    {
        var lease = _pool.Get();
        try
        {
            await JsonReferenceTransformer.SerializeWithPointers(payload, output, lease.TypeInfo, ct);
        }
        finally
        {
            _pool.Return(lease);
        }
    }

    public async Task SerializeWithPointers(T payload, PipeWriter output, CancellationToken ct = default)
    {
        var lease = _pool.Get();
        try
        {
            await JsonReferenceTransformer.SerializeWithPointers(payload, output, lease.TypeInfo, ct);
        }
        finally
        {
            _pool.Return(lease);
        }
    }
}