using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.ObjectPool;

namespace Dodo.Json.References;

internal sealed class ReferenceLease<T>
{
    public PoolingReferenceResolver Resolver { get; } = new();
    public JsonTypeInfo<T> TypeInfo { get; }

    public ReferenceLease(JsonSerializerOptions baseOptions, Func<JsonSerializerOptions, JsonTypeInfo<T>> typeInfoFactory)
        => TypeInfo = typeInfoFactory(PooledReferenceHandler.CreateOptions(baseOptions, Resolver));
}

internal sealed class ReferenceLeasePolicy<T>(
    JsonSerializerOptions baseOptions,
    Func<JsonSerializerOptions, JsonTypeInfo<T>> typeInfoFactory): IPooledObjectPolicy<ReferenceLease<T>>
{
    // Snapshot: lazily created leases must not observe later mutations of the caller's options.
    private readonly JsonSerializerOptions _baseOptions = new(baseOptions);

    public ReferenceLease<T> Create()
        => new(_baseOptions, typeInfoFactory);

    public bool Return(ReferenceLease<T> lease)
    {
        lease.Resolver.Reset();
        return true;
    }
}
