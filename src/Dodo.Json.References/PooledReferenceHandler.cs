using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dodo.Json.References;

/// <summary>Hands the SAME mutable resolver to every operation: Reset between documents, never use concurrently.</summary>
public sealed class PooledReferenceHandler(PoolingReferenceResolver resolver) : ReferenceHandler
{
    /// <inheritdoc />
    public override ReferenceResolver CreateResolver()
        => resolver;

    /// <summary>Copies <paramref name="baseOptions"/> and swaps in a handler backed by <paramref name="resolver"/>.</summary>
    public static JsonSerializerOptions CreateOptions(JsonSerializerOptions baseOptions,
        PoolingReferenceResolver resolver)
        => new(baseOptions) { ReferenceHandler = new PooledReferenceHandler(resolver) };
}