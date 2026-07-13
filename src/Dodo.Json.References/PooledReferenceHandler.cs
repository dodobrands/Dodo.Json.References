using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dodo.Json.References;

/// <summary>
/// Hands the SAME mutable resolver to every operation — single-operation contract: Reset between
/// documents or a fresh instance per document, never concurrent use.
/// </summary>
public sealed class PooledReferenceHandler(PoolingReferenceResolver resolver) : ReferenceHandler
{
    public override ReferenceResolver CreateResolver()
        => resolver;

    /// <summary>Copies the options with the handler bound to <paramref name="resolver"/> (single-operation contract).</summary>
    public static JsonSerializerOptions CreateOptions(JsonSerializerOptions baseOptions,
        PoolingReferenceResolver resolver)
        => new(baseOptions) { ReferenceHandler = new PooledReferenceHandler(resolver) };
}