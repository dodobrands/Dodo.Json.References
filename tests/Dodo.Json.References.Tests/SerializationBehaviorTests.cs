using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using NUnit.Framework;

namespace Dodo.Json.References.Tests;

/// <summary>
/// End-to-end behaviors of the public surface: output shaping (indentation, wrapper re-emit,
/// element indexes, escaped names), buffer growth, incremental writing, pooled reuse under
/// concurrency and the source-generated type-info path.
/// </summary>
[TestFixture]
internal class SerializationBehaviorTests
{
    private static readonly JsonSerializerOptions PreserveOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.Preserve
    };

    private static JsonSerializerOptions PooledOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = ReferenceHandler.Preserve,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static async Task<string> Serialize<T>(T payload, JsonSerializerOptions? options = null)
    {
        await using var buffer = new MemoryStream();
        await JsonReferenceTransformer.SerializeWithPointers(payload, buffer, options ?? PreserveOptions);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    [Test]
    public async Task WriteIndented_ProducesIndentedOutputWithPointerRefs()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = ReferenceHandler.Preserve,
            WriteIndented = true
        };
        var shared = new Node { Name = "shared" };
        var json = await Serialize(new Pair { Left = shared, Right = shared }, options);

        json.Should().Contain("\n", "output honors WriteIndented");
        json.Should().Contain("#/", "references are rewritten to pointers");

        var restored = JsonSerializer.Deserialize<Pair>(json, options)!;
        restored.Left.Should().BeSameAs(restored.Right);
    }

    [Test]
    public async Task UnreferencedCollectionWrappers_RoundTripWithSharedIdentity()
    {
        var shared = new Node { Name = "shared" };
        var graph = new RefGraph
        {
            ListShared = [shared, new() { Name = "a" }],
            ArrayShared = [shared, new Node { Name = "b" }],
            ListSolo = [new() { Name = "c" }, new() { Name = "d" }],
            ArraySolo = [new Node { Name = "e" }]
        };

        var json = await Serialize(graph);

        json.Should().Contain("$values", "Preserve wraps List<T> collections");
        json.Should().Contain("#/", "the shared node is reference-rewritten");

        var restored = JsonSerializer.Deserialize<RefGraph>(json, PreserveOptions)!;
        restored.ListShared.Should().HaveCount(2);
        restored.ArrayShared.Should().HaveCount(2);
        restored.ListSolo.Should().HaveCount(2);
        restored.ArraySolo.Should().HaveCount(1);
        restored.ListShared[0].Should().BeSameAs(restored.ArrayShared[0]);
    }

    [Test]
    public async Task NullArrayElements_CountTowardPointerIndexes()
    {
        var shared = new Node { Name = "shared" };
        var json = await Serialize(new MixedElements { Items = [null, shared], Echo = shared });

        json.Should().Contain("\"$ref\":\"#/items/1\"", "the null at index 0 occupies its position");
    }

    [Test]
    public async Task PrimitiveArrayElements_CountTowardPointerIndexes()
    {
        var shared = new Node { Name = "shared" };
        var json = await Serialize(new MixedElements { Mixed = [1, "x", shared], MixedEcho = shared });

        json.Should().Contain("\"$ref\":\"#/mixed/2\"", "the number and string occupy positions 0 and 1");
    }

    [Test]
    public async Task EscapedPropertyNames_RoundTripWithoutDoubleEscaping()
    {
        var shared = new Node { Name = "shared" };
        var json = await Serialize(new EscapedNameGraph { Quoted = [shared], Echo = shared });

        var restored = JsonSerializer.Deserialize<EscapedNameGraph>(json, PreserveOptions)!;
        restored.Quoted.Should().HaveCount(1);
        restored.Quoted[0].Name.Should().Be("shared");
        restored.Quoted[0].Should().BeSameAs(restored.Echo);
    }

    [Test]
    public async Task DocumentLargerThanInitialBuffer_RoundTrips()
    {
        // ~9MB of JSON: outgrows the pooled document buffer mid-serialize.
        var graph = new BigGraph
        {
            Items = Enumerable.Range(0, 9_000)
                .Select(_ => new Node { Name = new string('x', 1_000) })
                .ToArray()
        };

        await using var buffer = new MemoryStream();
        await JsonReferenceTransformer.SerializeWithPointers(graph, buffer, PreserveOptions);
        buffer.Seek(0, SeekOrigin.Begin);

        var restored = await JsonSerializer.DeserializeAsync<BigGraph>(buffer, PreserveOptions);
        restored!.Items.Should().HaveCount(9_000);
        restored.Items[8_999].Name.Should().HaveLength(1_000);
    }

    [Test]
    public async Task LargeDocumentToStream_WritesIncrementally()
    {
        var graph = new BigGraph
        {
            Items = Enumerable.Range(0, 1_500)
                .Select(_ => new Node { Name = new string('x', 600) })
                .ToArray()
        };

        await using var counting = new WriteCountingStream();
        await JsonReferenceTransformer.SerializeWithPointers(graph, counting, PreserveOptions);

        counting.WriteCalls.Should().BeGreaterThan(1, "large documents must reach the stream in segments, not one blob");
    }

    [Test]
    public async Task PooledSerializer_ConcurrentUse_ProducesIdenticalRoundTrippableOutput()
    {
        var serializer = new PooledReferenceSerializer<Pair>(PooledOptions());
        var shared = new Node { Name = "shared" };
        var graph = new Pair { Left = shared, Right = shared };

        var reference = await SerializePooled(serializer, graph);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => SerializePooled(serializer, graph)));

        foreach (var result in results)
        {
            result.Should().Be(reference);
        }

        var restored = JsonSerializer.Deserialize<Pair>(reference, PreserveOptions)!;
        restored.Left.Should().BeSameAs(restored.Right);
    }

    [Test]
    public async Task PooledSerializer_SourceGenContext_RoundTripsWithWarmTypeInfo()
    {
#pragma warning disable CA1869 // one-shot options exercising the source-gen resolver path
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = ReferenceHandler.Preserve,
            TypeInfoResolver = JsonTypeInfoResolver.Combine(ProbeJsonContext.Default, new DefaultJsonTypeInfoResolver())
        };
        options.MakeReadOnly(populateMissingResolver: true);
#pragma warning restore CA1869

        var shared = new ProbeNode { Name = "shared" };
        var probe = new Probe { First = [shared], Second = [shared] };
        var serializer = new PooledReferenceSerializer<Probe>(options);

        var first = await SerializePooled(serializer, probe);
        var second = await SerializePooled(serializer, probe);

        first.Should().Contain("#/", "the source-gen path emits pointer refs");
        second.Should().Be(first, "pooled reuse is deterministic");

        var restored = JsonSerializer.Deserialize<Probe>(second, options)!;
        restored.First[0].Should().BeSameAs(restored.Second[0]);
    }

    [Test]
    public async Task PropertyNamesWithPointerSpecials_AreRfc6901Escaped()
    {
        var shared = new Node { Name = "shared" };
        var json = await Serialize(new PointerSpecialsGraph { Special = [shared], Echo = shared });

        json.Should().Contain("\"$ref\":\"#/a~1b~0c/0\"", "'/' escapes to ~1 and '~' to ~0 in pointer tokens");
    }

    [Test]
    public async Task ExplicitTypeInfoFactory_PinsContextForSerializeAndDeserialize()
    {
        var options = PooledOptions();
        var serializer = new PooledReferenceSerializer<Pair>(options, o => (JsonTypeInfo<Pair>)o.GetTypeInfo(typeof(Pair)));
        var deserializer = new PooledReferenceDeserializer<Pair>(options, o => (JsonTypeInfo<Pair>)o.GetTypeInfo(typeof(Pair)));

        var shared = new Node { Name = "shared" };
        await using var buffer = new MemoryStream();
        await serializer.SerializeWithPointers(new Pair { Left = shared, Right = shared }, buffer);
        buffer.Seek(0, SeekOrigin.Begin);

        var restored = await deserializer.Deserialize(buffer);
        restored!.Left.Should().BeSameAs(restored.Right);
    }

    [Test]
    public async Task PooledDeserializer_ConcurrentUse_RelinksEveryDocument()
    {
        var options = PooledOptions();
        var serializer = new PooledReferenceSerializer<Pair>(options);
        var deserializer = new PooledReferenceDeserializer<Pair>(options, maxRetained: 2);

        var shared = new Node { Name = "shared" };
        await using var buffer = new MemoryStream();
        await serializer.SerializeWithPointers(new Pair { Left = shared, Right = shared }, buffer);
        var bytes = buffer.ToArray();

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ =>
        {
            using var input = new MemoryStream(bytes, writable: false);
            return await deserializer.Deserialize(input);
        }));

        foreach (var restored in results)
        {
            restored!.Left.Should().BeSameAs(restored.Right);
        }
    }

    private static async Task<string> SerializePooled<T>(PooledReferenceSerializer<T> serializer, T payload)
    {
        await using var buffer = new MemoryStream();
        await serializer.SerializeWithPointers(payload, buffer);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal sealed class Node
    {
        public string Name { get; set; } = string.Empty;
    }

    internal sealed class Pair
    {
        public Node? Left { get; set; }
        public Node? Right { get; set; }
    }

    internal sealed class RefGraph
    {
        public List<Node> ListShared { get; set; } = [];
        public Node[] ArrayShared { get; set; } = [];
        public List<Node> ListSolo { get; set; } = [];
        public Node[] ArraySolo { get; set; } = [];
    }

    internal sealed class MixedElements
    {
        public Node?[] Items { get; set; } = [];
        public object[] Mixed { get; set; } = [];
        public Node? Echo { get; set; }
        public object? MixedEcho { get; set; }
    }

    internal sealed class EscapedNameGraph
    {
        [JsonPropertyName("a\"b")]
        public Node[] Quoted { get; set; } = [];

        public Node? Echo { get; set; }
    }

    internal sealed class PointerSpecialsGraph
    {
        [JsonPropertyName("a/b~c")]
        public Node[] Special { get; set; } = [];

        public Node? Echo { get; set; }
    }

    internal sealed class BigGraph
    {
        public Node[] Items { get; set; } = [];
    }

    internal sealed class Probe
    {
        public ProbeNode[] First { get; set; } = [];
        public ProbeNode[] Second { get; set; } = [];
    }

    internal sealed class ProbeNode
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class WriteCountingStream : Stream
    {
        public int WriteCalls { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => WriteCalls++;

        public override void Write(ReadOnlySpan<byte> buffer) => WriteCalls++;

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteCalls++;
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            return ValueTask.CompletedTask;
        }
    }
}

[JsonSerializable(typeof(SerializationBehaviorTests.Probe), GenerationMode = JsonSourceGenerationMode.Metadata)]
internal sealed partial class ProbeJsonContext : JsonSerializerContext;
