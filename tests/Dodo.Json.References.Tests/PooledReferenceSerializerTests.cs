using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using NUnit.Framework;

namespace Dodo.Json.References.Tests;

[TestFixture]
internal sealed class PooledReferenceSerializerTests
{
    // Left and Right share one Node instance, so a correct deserialize must re-link them to a single object.
    internal sealed class Node
    {
        public string Name { get; set; } = string.Empty;
    }

    internal sealed class Graph
    {
        public Node Left { get; set; } = new();
        public Node Right { get; set; } = new();
    }

    private static JsonSerializerOptions BaseOptions()
        => new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private static Graph SharedRefGraph()
    {
        var shared = new Node { Name = "shared" };
        return new Graph { Left = shared, Right = shared };
    }

    [Test]
    public async Task MutatingBaseOptionsAfterConstruction_DoesNotAffectOutput()
    {
        // Leases are built lazily, so a snapshot at construction is what keeps later mutations out of them.
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        var serializer = new PooledReferenceSerializer<Graph>(options);

        options.PropertyNamingPolicy = null;

        var json = System.Text.Encoding.UTF8.GetString(await Serialize(serializer, SharedRefGraph()));
        json.Should().Contain("\"left\"", "the options snapshot taken at construction governs every lease");
        json.Should().NotContain("\"Left\"");
    }

    [Test]
    public void ThrowingTypeInfoFactory_FailsAtConstruction()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _ = new PooledReferenceSerializer<Graph>(
                BaseOptions(),
                _ => throw new InvalidOperationException("boom")
            )
        );

        Assert.Throws<InvalidOperationException>(() =>
            _ = new PooledReferenceDeserializer<Graph>(
                BaseOptions(),
                _ => throw new InvalidOperationException("boom")
            )
        );
    }

    [Test]
    public async Task PointerRoundTripPreservesSharedReference()
    {
        var serializer = new PooledReferenceSerializer<Graph>(BaseOptions());
        var deserializer = new PooledReferenceDeserializer<Graph>(BaseOptions());

        var bytes = await Serialize(serializer, SharedRefGraph());
        var roundTripped = await Deserialize(deserializer, bytes);

        roundTripped.Should().NotBeNull();
        roundTripped!.Left.Should().BeSameAs(roundTripped.Right, "the shared $ref must resolve to one instance");
        roundTripped.Left.Name.Should().Be("shared");
    }

    [Test]
    public async Task DeserializerReuseDoesNotLeakState()
    {
        var serializer = new PooledReferenceSerializer<Graph>(BaseOptions());
        // maxRetained 1 forces resolver reuse: a missed reset would leave stale ids and fail the next read.
        var deserializer = new PooledReferenceDeserializer<Graph>(BaseOptions(), maxRetained: 1);

        var bytes = await Serialize(serializer, SharedRefGraph());

        for (var iteration = 0; iteration < 3; iteration++)
        {
            var roundTripped = await Deserialize(deserializer, bytes);
            roundTripped.Should().NotBeNull($"reuse iteration {iteration} must deserialize");
            roundTripped!.Left.Should().BeSameAs(roundTripped.Right, $"reuse iteration {iteration} must re-link the shared ref");
            roundTripped.Left.Name.Should().Be("shared");
        }
    }

    [Test]
    public async Task PipeWriterOverload_MatchesStreamOutput()
    {
        var serializer = new PooledReferenceSerializer<Graph>(BaseOptions());
        var graph = SharedRefGraph();

        var streamed = await Serialize(serializer, graph);

        var pipe = new System.IO.Pipelines.Pipe();
        await serializer.SerializeWithPointers(graph, pipe.Writer);
        await pipe.Writer.CompleteAsync();
        using var piped = new MemoryStream();
        await pipe.Reader.CopyToAsync(piped);

        piped.ToArray().Should().Equal(streamed);
    }

    private static async Task<byte[]> Serialize(PooledReferenceSerializer<Graph> serializer, Graph graph)
    {
        using var output = new MemoryStream();
        await serializer.SerializeWithPointers(graph, output);
        return output.ToArray();
    }

    private static async Task<Graph?> Deserialize(PooledReferenceDeserializer<Graph> deserializer, byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        return await deserializer.Deserialize(input);
    }
}
