using System.IO.Pipelines;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using NUnit.Framework;

namespace Dodo.Json.References.Tests;

[TestFixture]
internal sealed class JsonReferenceTransformerEdgeTests
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

    internal sealed class PointerSpecialNameGraph
    {
        [JsonPropertyName("a/b")]
        public Node? Slash { get; set; }

        [JsonPropertyName("c~d")]
        public Node? Tilde { get; set; }
    }

    internal sealed class TildeNameGraph
    {
        [JsonPropertyName("c~d")]
        public Node? Tilde { get; set; }

        public Node? Plain { get; set; }
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

    // Every shape NumericId.TryParse rejects: non-digits, leading zero, past the dense bound (2^21), eight digits.
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
        var options = CustomIdOptions(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture), alwaysExists: true);
        var json = await Serialize(new Pair { Left = new Node { Name = "a" } }, options);

        json.Should().Contain("\"$ref\":\"1\"", "an unresolvable $ref keeps its original id");
        json.Should().NotContain("\"$id\"");
    }

    [Test]
    public async Task PooledArrayReuse_DanglingRefDoesNotSeePreviousDocumentPointer()
    {
        // Doc 1: the self-referencing root keeps $id 1, writing the root pointer into idPaths slot 1.
        var cycle = new Cyclic { Name = "c" };
        cycle.Self = cycle;
        (await Serialize(cycle, PreserveOptions)).Should().Contain("\"$id\":\"#\"");

        // Doc 2 reads id 1 dangling; return-side and rent-side cleanup each null the slot, so this trips only if both regress.
        var dangling = CustomIdOptions(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture), alwaysExists: true);
        var json = await Serialize(new Pair { Left = new Node { Name = "a" } }, dangling);

        json.Should().Contain("\"$ref\":\"1\"");
        json.Should().NotContain("\"$ref\":\"#\"");
    }

    internal sealed class RawRefHolder
    {
        public bool Unused { get; set; }
    }

    private sealed class RawRefConverter : System.Text.Json.Serialization.JsonConverter<RawRefHolder>
    {
        public override RawRefHolder Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, RawRefHolder value, JsonSerializerOptions options)
            => writer.WriteRawValue("{\"$ref\": \"1\"}");
    }

    internal sealed class RawRefGraph
    {
        public RawRefHolder Raw { get; set; } = new();
        public Node? Left { get; set; }
        public Node? Right { get; set; }
    }

    [Test]
    public async Task RawConverterRef_InvisibleToScan_PassesThroughVerbatim()
    {
        // A foreign renter returns the bucket dirty at slot 1; the transformer must never surface it.
        var dirty = System.Buffers.ArrayPool<long>.Shared.Rent(301);
        dirty[1] = 3L;
        System.Buffers.ArrayPool<long>.Shared.Return(dirty);

        // "$ref": "1" (with a space) is invisible to the pass-1 byte scan, so slot 1 is untracked; ids are
        // spread 100 apart to keep density under 1/8 so the rent-clear takes the sparse branch and skips it.
        var options = CustomIdOptions(n => (n * 100).ToString(System.Globalization.CultureInfo.InvariantCulture));
        options.Converters.Add(new RawRefConverter());
        var shared = new Node { Name = "s" };
        var json = await Serialize(new RawRefGraph { Raw = new RawRefHolder(), Left = shared, Right = shared }, options);

        json.Should().Contain("\"$ref\":\"1\"", "a scan-invisible ref must pass through verbatim");
        json.Should().Contain("\"$ref\":\"#/left\"", "tracked refs still transform");
    }

    [Test]
    public async Task EscapedIds_DefaultEncoder_AreRewrittenToPointers()
    {
        var options = CustomIdOptions(n => $"тест-{n}");
        var json = await Serialize(SharedPair(out _), options);

        json.Should().Contain("\"$id\":\"#/left\"");
        json.Should().Contain("\"$ref\":\"#/left\"");

        var restored = JsonSerializer.Deserialize<Pair>(json, options)!;
        restored.Left.Should().BeSameAs(restored.Right);
    }

    [Test]
    public async Task PointerSpecialsInPropertyNames_AreEscapedPerRfc6901()
    {
        var settings = new TextEncoderSettings(UnicodeRanges.BasicLatin);
        settings.ForbidCharacter('/');
        settings.ForbidCharacter('~');
        var options = new JsonSerializerOptions(PreserveOptions) { Encoder = JavaScriptEncoder.Create(settings) };
        var shared = new Node { Name = "s" };
        var json = await Serialize(new PointerSpecialNameGraph { Slash = shared, Tilde = shared }, options);

        json.Should().Contain("\"$id\":\"#/a~1b\"");
        json.Should().Contain("\"$ref\":\"#/a~1b\"");

        var restored = JsonSerializer.Deserialize<PointerSpecialNameGraph>(json, options)!;
        restored.Slash.Should().BeSameAs(restored.Tilde);
    }

    [Test]
    public async Task TildeInPropertyName_EscapesAsTildeZero()
    {
        var settings = new TextEncoderSettings(UnicodeRanges.BasicLatin);
        settings.ForbidCharacter('/');
        settings.ForbidCharacter('~');
        var options = new JsonSerializerOptions(PreserveOptions) { Encoder = JavaScriptEncoder.Create(settings) };
        var shared = new Node { Name = "s" };
        var json = await Serialize(new TildeNameGraph { Tilde = shared, Plain = shared }, options);

        json.Should().Contain("\"$id\":\"#/c~0d\"");
        json.Should().Contain("\"$ref\":\"#/c~0d\"");

        var restored = JsonSerializer.Deserialize<TildeNameGraph>(json, options)!;
        restored.Tilde.Should().BeSameAs(restored.Plain);
    }

    [Test]
    public async Task NonAsciiIds_RelaxedEncoder_AreRewrittenToPointers()
    {
        // Relaxed escaping keeps the ids raw UTF-8, so both passes see identical bytes.
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

    [Test]
    public async Task PercentInPropertyName_IsPercentEncodedPerSection6()
    {
        var shared = new Node { Name = "s" };
        var json = await Serialize(new PercentNameGraph { Percent = shared, Plain = shared }, PreserveOptions);

        json.Should().Contain("\"$id\":\"#/c%25d\"");
        json.Should().Contain("\"$ref\":\"#/c%25d\"");

        var restored = JsonSerializer.Deserialize<PercentNameGraph>(json, PreserveOptions)!;
        restored.Percent.Should().BeSameAs(restored.Plain);
    }

    [Test]
    public async Task SpaceInPropertyName_IsPercentEncoded()
    {
        var shared = new Node { Name = "s" };
        var json = await Serialize(new SpaceNameGraph { Spaced = shared, Plain = shared }, PreserveOptions);

        json.Should().Contain("\"$id\":\"#/a%20b\"");
        json.Should().Contain("\"$ref\":\"#/a%20b\"");
    }

    [Test]
    public async Task NonAsciiPropertyName_IsPercentEncodedAsUtf8Octets()
    {
        var options = new JsonSerializerOptions(PreserveOptions) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var shared = new Node { Name = "s" };
        var json = await Serialize(new NonAsciiNameGraph { Cyrillic = shared, Plain = shared }, options);

        json.Should().Contain("\"$id\":\"#/%D1%82%D0%B5%D1%81%D1%82\"");
        json.Should().Contain("\"$ref\":\"#/%D1%82%D0%B5%D1%81%D1%82\"");
    }

    [Test]
    public async Task SurrogatePairPropertyName_DefaultEncoder_IsPercentEncodedAsUtf8Octets()
    {
        var shared = new Node { Name = "s" };
        var json = await Serialize(new SurrogateNameGraph { Emoji = shared, Plain = shared }, PreserveOptions);

        json.Should().Contain("\"$id\":\"#/a%F0%9F%98%80b\"");
        json.Should().Contain("\"$ref\":\"#/a%F0%9F%98%80b\"");
    }

    [Test]
    public async Task EscapedRefToUnescapedNumericId_KeepsItsTarget()
    {
        var options = CustomIdOptions(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        options.Converters.Add(new EscapedRefConverter());
        var json = await Serialize(new EscapedRefGraph { Left = new Node { Name = "s" }, Raw = new RawRefHolder() }, options);

        json.Should().Contain("\"$id\":\"#\"", "the root owns the numeric id the escaped $ref names");
        json.Should().Contain("\"raw\":{\"$ref\":\"#\"}", "an escaped $ref still resolves to its target");
    }

    [Test]
    public async Task UnescapedRefToEscapedNumericId_KeepsItsTarget()
    {
        var json = await Serialize(new EscapedIdPairHolder(), PreserveOptions);

        json.Should().Contain("\"$id\":\"#/pair/a\"", "an unescaped $ref still references this escaped id");
        json.Should().Contain("\"$ref\":\"#/pair/a\"");
    }

    [Test]
    public async Task NonStringRefValue_DoesNotShiftArrayIndexes()
    {
        var json = await Serialize(new NonStringRefHolder(), PreserveOptions);

        json.Should().Contain("\"$id\":\"#/items/1\"", "a non-string $ref value is an object member, not an array element");
        json.Should().Contain("\"$ref\":\"#/items/1\"");
    }

    [Test]
    public async Task IdOnlyArrayElement_DoesNotNameTheNextElement()
    {
        var shared = new Node { Name = "shared" };
        var json = await Serialize(new List<object> { new Dictionary<string, int>(), shared, shared }, PreserveOptions);

        json.Should().Contain("\"$id\":\"#/1\"", "an $id-only element occupies an array index, it never names one");
        json.Should().Contain("\"$ref\":\"#/1\"");
    }

    [Test]
    public async Task ScalarAfterIdOnlyArrayElement_StillCountsTowardArrayIndexes()
    {
        var json = await Serialize(new IdOnlyThenScalarHolder(), PreserveOptions);

        json.Should().Contain("\"$id\":\"#/2\"", "the string element between the two $id elements occupies an index");
        json.Should().Contain("\"$ref\":\"#/2\"");
    }

    [Test]
    public async Task IdOnlyElementInsideNestedArray_DoesNotEscapeIntoTheOuterArray()
    {
        var json = await Serialize(new IdOnlyInNestedArrayHolder(), PreserveOptions);

        json.Should().Contain("\"$id\":\"#/1\"", "a closing array must not carry a pending property name outwards");
        json.Should().Contain("\"$ref\":\"#/1\"");
    }

    [Test]
    public async Task ValuesDictionaryKeyWithScalarValue_DoesNotSwallowTheNextPropertyName()
    {
        var shared = new Node { Name = "shared" };
        var json = await Serialize(new { d = new Dictionary<string, int> { ["$values"] = 1 }, x = new[] { new[] { shared } }, y = shared }, PreserveOptions);

        json.Should().Contain("\"$id\":\"#/x/0/0\"");
        json.Should().Contain("\"$ref\":\"#/x/0/0\"");
    }

    [Test]
    public async Task IdDictionaryKeyWithNumberValue_IsKeptAsData()
    {
        var json = await Serialize(new Dictionary<string, int> { ["$id"] = 5, ["b"] = 1 }, PreserveOptions);

        json.Should().Contain("\"$id\":5");
    }

    [Test]
    public async Task IdDictionaryKeyWithObjectValue_IsKeptAsData()
    {
        var json = await Serialize(new Dictionary<string, Node> { ["$id"] = new() { Name = "t" } }, PreserveOptions);

        json.Should().Contain("\"$id\":{\"name\":\"t\"}");
    }

    [TestCase(' ', 2, "\n")]
    [TestCase('\t', 1, "\r\n")]
    public async Task WriteIndented_PutsEveryArrayElementOnItsOwnLine(char indentCharacter, int indentSize, string newLine)
    {
        var shared = new Node { Name = "shared" };
        var payload = new { items = new object?[] { shared, 1, "s", new[] { 2, 3 }, true, null, 1.5 }, empty = Array.Empty<int>(), back = shared };
        var indented = new JsonSerializerOptions(PreserveOptions) { WriteIndented = true, IndentCharacter = indentCharacter, IndentSize = indentSize, NewLine = newLine };

        var json = await Serialize(payload, indented);

        json.Should().Be(Reindent(await Serialize(payload, PreserveOptions), indented));
    }

    private static string Reindent(string compact, JsonSerializerOptions options)
    {
        using var document = JsonDocument.Parse(compact);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, IndentCharacter = options.IndentCharacter, IndentSize = options.IndentSize, NewLine = options.NewLine }))
        {
            document.WriteTo(writer);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal sealed class PercentNameGraph
    {
        [JsonPropertyName("c%d")]
        public Node? Percent { get; set; }

        public Node? Plain { get; set; }
    }

    internal sealed class SpaceNameGraph
    {
        [JsonPropertyName("a b")]
        public Node? Spaced { get; set; }

        public Node? Plain { get; set; }
    }

    internal sealed class NonAsciiNameGraph
    {
        [JsonPropertyName("тест")]
        public Node? Cyrillic { get; set; }

        public Node? Plain { get; set; }
    }

    internal sealed class SurrogateNameGraph
    {
        [JsonPropertyName("a😀b")]
        public Node? Emoji { get; set; }

        public Node? Plain { get; set; }
    }

    private sealed class EscapedRefConverter: System.Text.Json.Serialization.JsonConverter<RawRefHolder>
    {
        public override RawRefHolder Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, RawRefHolder value, JsonSerializerOptions options)
            => writer.WriteRawValue("{\"$ref\":\"\\u0031\"}");
    }

    internal sealed class EscapedRefGraph
    {
        public Node? Left { get; set; }
        public RawRefHolder Raw { get; set; } = new();
    }

    [JsonConverter(typeof(NonStringRefConverter))]
    internal sealed class NonStringRefHolder;

    [JsonConverter(typeof(IdOnlyThenScalarConverter))]
    internal sealed class IdOnlyThenScalarHolder;

    private sealed class IdOnlyThenScalarConverter: System.Text.Json.Serialization.JsonConverter<IdOnlyThenScalarHolder>
    {
        public override IdOnlyThenScalarHolder Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, IdOnlyThenScalarHolder value, JsonSerializerOptions options)
            => writer.WriteRawValue("""[{"$id":"1"},"x",{"$id":"2","name":"t"},{"$ref":"2"}]""");
    }

    [JsonConverter(typeof(IdOnlyInNestedArrayConverter))]
    internal sealed class IdOnlyInNestedArrayHolder;

    private sealed class IdOnlyInNestedArrayConverter: System.Text.Json.Serialization.JsonConverter<IdOnlyInNestedArrayHolder>
    {
        public override IdOnlyInNestedArrayHolder Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, IdOnlyInNestedArrayHolder value, JsonSerializerOptions options)
            => writer.WriteRawValue("""[[{"$id":"1"}],{"$id":"2","name":"t"},{"$ref":"2"}]""");
    }

    private sealed class NonStringRefConverter: System.Text.Json.Serialization.JsonConverter<NonStringRefHolder>
    {
        public override NonStringRefHolder Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, NonStringRefHolder value, JsonSerializerOptions options)
            => writer.WriteRawValue("""{"items":[{"$ref":true},{"$id":"1","name":"t"}],"back":{"$ref":"1"}}""");
    }

    internal sealed class EscapedIdPairHolder
    {
        public EscapedIdPair Pair { get; set; } = new();
    }

    [JsonConverter(typeof(EscapedIdPairConverter))]
    internal sealed class EscapedIdPair;

    private sealed class EscapedIdPairConverter: System.Text.Json.Serialization.JsonConverter<EscapedIdPair>
    {
        public override EscapedIdPair Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, EscapedIdPair value, JsonSerializerOptions options)
            => writer.WriteRawValue("{\"a\":{\"$id\":\"\\u0032\"},\"b\":{\"$ref\":\"2\"}}");
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
