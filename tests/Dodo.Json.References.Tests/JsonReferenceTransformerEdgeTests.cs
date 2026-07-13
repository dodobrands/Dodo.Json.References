using System.IO.Pipelines;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dodo.Json.References;
using FluentAssertions;
using NUnit.Framework;

namespace Dodo.Json.References.Tests;

[TestFixture]
internal class JsonReferenceTransformerEdgeTests
{
    private static readonly JsonSerializerOptions PreserveOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.Preserve
    };

    internal sealed class Node
    {
        public string Name { get; set; } = string.Empty;
    }

    internal sealed class Pair
    {
        public Node? Left { get; set; }
        public Node? Right { get; set; }
    }

    internal sealed class BoolBag
    {
        public bool Yes { get; set; }
        public bool No { get; set; }
        public object[] Mixed { get; set; } = [];
        public object? Echo { get; set; }
    }

    internal sealed class Cyclic
    {
        public string Name { get; set; } = string.Empty;
        public Cyclic? Self { get; set; }
    }

    internal sealed class EmptyNameGraph
    {
        [JsonPropertyName("")]
        public Node[] Unnamed { get; set; } = [];

        public Node? Echo { get; set; }
    }

    internal sealed class DollarNameGraph
    {
        // STJ itself rejects [JsonPropertyName("$id"/"$ref"/"$values")] under a ReferenceHandler, so metadata detection cannot be spoofed.
        [JsonPropertyName("$custom")]
        public Node[] Custom { get; set; } = [];

        public Node? Echo { get; set; }
    }

    internal sealed class ManyRefsGraph
    {
        public Node[] First { get; set; } = [];
        public Node[] Second { get; set; } = [];
    }

    internal sealed class ListGraph
    {
        public List<Node> Solo { get; set; } = [];
    }

    private static async Task<string> Serialize<T>(T payload, JsonSerializerOptions options)
    {
        await using var buffer = new MemoryStream();
        await JsonReferenceTransformer.SerializeWithPointers(payload, buffer, options);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static JsonSerializerOptions CustomIdOptions(Func<int, string> idFactory, bool alwaysExists = false)
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = new CustomIdReferenceHandler(idFactory, alwaysExists)
        };

    private static Pair SharedPair(out Node shared)
    {
        shared = new Node { Name = "shared" };
        return new Pair { Left = shared, Right = shared };
    }

    // Every shape TryParseNumericId rejects: non-digits, leading zero, past the dense bound (2^21), eight digits.
    private static readonly Dictionary<string, Func<int, string>> NonCanonicalIdFactories = new()
    {
        ["short-alpha"] = n => $"id-{n}",
        ["leading-zero"] = n => $"00{n}",
        ["above-dense-bound"] = n => (2_097_152 + n).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["eight-digit"] = n => $"9000000{n}"
    };

    [TestCase("short-alpha")]
    [TestCase("leading-zero")]
    [TestCase("above-dense-bound")]
    [TestCase("eight-digit")]
    public async Task NonCanonicalIds_AreRewrittenToPointers(string idShape)
    {
        var options = CustomIdOptions(NonCanonicalIdFactories[idShape]);
        var json = await Serialize(SharedPair(out _), options);

        json.Should().Contain("\"$id\":\"#/left\"");
        json.Should().Contain("\"$ref\":\"#/left\"");

        var restored = JsonSerializer.Deserialize<Pair>(json, options)!;
        restored.Left.Should().BeSameAs(restored.Right);
    }

    [Test]
    public async Task DanglingRef_KeepsOriginalIdValue()
    {
        // $ref without any matching $id must pass the opaque id through untouched.
        var options = CustomIdOptions(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture), alwaysExists: true);
        var json = await Serialize(new Pair { Left = new Node { Name = "a" } }, options);

        json.Should().Contain("\"$ref\":\"1\"", "an unresolvable $ref keeps its original id");
        json.Should().NotContain("\"$id\"");
    }

    [Test]
    public async Task EscapedIds_DefaultEncoder_PassThroughUntransformed()
    {
        // Documented limitation: the default encoder escapes non-ASCII ids to \uXXXX, the two passes disagree, and the pair stays untransformed.
        var options = CustomIdOptions(n => $"тест-{n}");
        var json = await Serialize(SharedPair(out _), options);

        // The resolver hands out ids root-first: the shared node is the second registration.
        json.Should().NotContain("\"$id\"");
        json.Should().Contain("\"$ref\":\"\\u0442\\u0435\\u0441\\u0442-2\"");
    }

    [Test]
    public async Task NonAsciiIds_RelaxedEncoder_AreRewrittenToPointers()
    {
        // UnsafeRelaxedJsonEscaping keeps the same ids raw UTF-8, so both passes see identical bytes.
        var options = CustomIdOptions(n => $"тест-{n}");
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        var json = await Serialize(SharedPair(out _), options);

        json.Should().Contain("\"$id\":\"#/left\"");
        json.Should().Contain("\"$ref\":\"#/left\"");

        var restored = JsonSerializer.Deserialize<Pair>(json, options)!;
        restored.Left.Should().BeSameAs(restored.Right);
    }

    [Test]
    public async Task BooleanTokens_ArePreservedAndCountTowardArrayIndexes()
    {
        var shared = new Node { Name = "shared" };
        var bag = new BoolBag { Yes = true, No = false, Mixed = [true, false, shared], Echo = shared };

        var json = await Serialize(bag, PreserveOptions);

        json.Should().Contain("\"yes\":true");
        json.Should().Contain("\"no\":false");
        json.Should().Contain("\"$ref\":\"#/mixed/2\"", "booleans occupy array positions 0 and 1");
    }

    [Test]
    public async Task RootSelfReference_UsesRootPointer()
    {
        var root = new Cyclic { Name = "root" };
        root.Self = root;

        var json = await Serialize(root, PreserveOptions);

        json.Should().Contain("\"$id\":\"#\"");
        json.Should().Contain("\"$ref\":\"#\"");

        var restored = JsonSerializer.Deserialize<Cyclic>(json, PreserveOptions)!;
        restored.Self.Should().BeSameAs(restored);
    }

    [Test]
    public async Task EmptyPropertyName_ProducesEmptyPointerToken()
    {
        var shared = new Node { Name = "shared" };
        var graph = new EmptyNameGraph { Unnamed = [shared], Echo = shared };

        var json = await Serialize(graph, PreserveOptions);

        // RFC 6901: an empty reference token is legal — "#//0" addresses element 0 of the ""-named property.
        json.Should().Contain("\"$ref\":\"#//0\"");

        var restored = JsonSerializer.Deserialize<EmptyNameGraph>(json, PreserveOptions)!;
        restored.Unnamed[0].Should().BeSameAs(restored.Echo);
    }

    [Test]
    public async Task DollarPrefixedPayloadProperty_BehavesAsRegularProperty()
    {
        // "$custom" is not metadata; '$' needs no RFC 6901 escaping in pointer paths.
        var shared = new Node { Name = "shared" };
        var graph = new DollarNameGraph { Custom = [shared], Echo = shared };

        var json = await Serialize(graph, PreserveOptions);

        json.Should().Contain("\"$id\":\"#/$custom/0\"");
        json.Should().Contain("\"$ref\":\"#/$custom/0\"");
    }

    [Test]
    public void PreCanceledToken_ThrowsOperationCanceled()
    {
        var ct = new CancellationToken(canceled: true);

        Assert.CatchAsync<OperationCanceledException>(async () =>
        {
            await using var buffer = new MemoryStream();
            await JsonReferenceTransformer.SerializeWithPointers(new Node(), buffer, PreserveOptions, ct);
        });
    }

    [Test]
    public async Task PipeWriterOutput_MatchesStreamOutput()
    {
        var graph = SharedPair(out _);

        await using var streamBuffer = new MemoryStream();
        await JsonReferenceTransformer.SerializeWithPointers(graph, streamBuffer, PreserveOptions);

        var pipe = new Pipe();
        await JsonReferenceTransformer.SerializeWithPointers(graph, pipe.Writer, PreserveOptions);
        await pipe.Writer.CompleteAsync();

        await using var pipeBuffer = new MemoryStream();
        await pipe.Reader.CopyToAsync(pipeBuffer);

        pipeBuffer.ToArray().Should().Equal(streamBuffer.ToArray());
    }

    [Test]
    public async Task ManyReferencedIds_PastRatchetThreshold_AllRewritten()
    {
        // 4200 ids: crosses the 4096 ratchet, spills the stack-first builder into the pool, spans multiple bitmap words.
        var nodes = Enumerable.Range(0, 4_200).Select(i => new Node { Name = $"n{i}" }).ToArray();
        // Distinct array instances — one shared array would collapse into a single array-level $ref.
        var graph = new ManyRefsGraph { First = nodes, Second = [.. nodes] };

        var json = await Serialize(graph, PreserveOptions);

        var restored = JsonSerializer.Deserialize<ManyRefsGraph>(json, PreserveOptions)!;
        restored.Second.Should().HaveCount(4_200);
        restored.First[0].Should().BeSameAs(restored.Second[0]);
        restored.First[4_199].Should().BeSameAs(restored.Second[4_199]);
    }

    [Test]
    public async Task LongUnreferencedWrapperId_IsReemittedBeforeValues()
    {
        // Wrapper $id must survive the unreferenced-id drop (STJ needs it before $values), even ids past the stack scratch.
        var options = CustomIdOptions(n => new string('a', 80) + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var graph = new ListGraph { Solo = [new Node { Name = "x" }] };

        var json = await Serialize(graph, options);

        json.Should().Contain($"\"$id\":\"{new string('a', 80)}2\",\"$values\"", "the wrapper is the second registered reference");

        var restored = JsonSerializer.Deserialize<ListGraph>(json, options)!;
        restored.Solo.Should().ContainSingle().Which.Name.Should().Be("x");
    }

    private sealed class CustomIdReferenceHandler(Func<int, string> idFactory, bool alwaysExists): ReferenceHandler
    {
        public override ReferenceResolver CreateResolver() => new CustomIdResolver(idFactory, alwaysExists);
    }

    private sealed class CustomIdResolver(Func<int, string> idFactory, bool alwaysExists): ReferenceResolver
    {
        private readonly Dictionary<object, string> _ids = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, object> _objects = [];
        private int _count;

        public override void AddReference(string referenceId, object value) => _objects[referenceId] = value;

        public override string GetReference(object value, out bool alreadyExists)
        {
            if (_ids.TryGetValue(value, out var id))
            {
                alreadyExists = true;
                return id;
            }

            id = idFactory(++_count);
            _ids[value] = id;
            alreadyExists = alwaysExists;
            return id;
        }

        public override object ResolveReference(string referenceId) => _objects[referenceId];
    }
}
