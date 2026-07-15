using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using NUnit.Framework;

namespace Dodo.Json.References.Tests;

[TestFixture]
internal sealed class PointerDeserializationTests
{
    private static readonly JsonSerializerOptions PreserveOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.Preserve
    };

    private static readonly JsonSerializerOptions SharedPooledOptions = PooledOptions();

    private static readonly PooledReferenceDeserializer<Pair> PairDeserializer = new(SharedPooledOptions);

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

    private static async Task<string> Serialize<T>(T payload)
    {
        await using var buffer = new MemoryStream();
        await JsonReferenceTransformer.SerializeWithPointers(payload, buffer, PreserveOptions);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<Pair?> DeserializePair(string json)
    {
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json), writable: false);
        return await PairDeserializer.Deserialize(input);
    }

    // Round-trips read through the package's own resolver, not STJ's default one.
    private static async Task<T?> DeserializePooled<T>(string json)
    {
        var deserializer = new PooledReferenceDeserializer<T>(SharedPooledOptions);
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json), writable: false);
        return await deserializer.Deserialize(input);
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

    internal sealed class TwoLists
    {
        public List<Node> First { get; set; } = [];
        public List<Node> Second { get; set; } = [];
        public Node? Echo { get; set; }
    }

    [Test]
    public async Task SharedListInstance_RefToWrapper_RoundTripsAsOneList()
    {
        var node = new Node { Name = "n" };
        var list = new List<Node> { node };
        var graph = new TwoLists { First = list, Second = list, Echo = node };

        var json = await Serialize(graph);

        json.Should().Contain("\"$ref\":\"#/first\"", "the shared list is addressed by its wrapper position");
        json.Should().Contain("\"$ref\":\"#/first/0\"", "$values stays transparent in element paths");

        var restored = (await DeserializePooled<TwoLists>(json))!;
        restored.Second.Should().BeSameAs(restored.First);
        restored.Echo.Should().BeSameAs(restored.First[0]);
    }

    internal sealed class NestedShared
    {
        public List<List<Node>> Outer { get; set; } = [];
    }

    [Test]
    public async Task SharedInnerList_RefToArrayElementWrapper_RoundTripsAsOneList()
    {
        var inner = new List<Node> { new() { Name = "i" } };
        var graph = new NestedShared { Outer = [inner, inner] };

        var json = await Serialize(graph);

        json.Should().Contain("\"$id\":\"#/outer/0\"", "the inner wrapper is addressed as an element of the outer list");
        json.Should().Contain("\"$ref\":\"#/outer/0\"");

        var restored = (await DeserializePooled<NestedShared>(json))!;
        restored.Outer[1].Should().BeSameAs(restored.Outer[0]);
    }

    internal sealed class Team
    {
        public List<Member> Members { get; set; } = [];
    }

    internal sealed class Member
    {
        public string Name { get; set; } = string.Empty;
        public List<Member>? Roster { get; set; }
    }

    [Test]
    public async Task ElementReferencingItsOwnCollection_RoundTripsCycle()
    {
        var team = new Team();
        var member = new Member { Name = "m" };
        team.Members.Add(member);
        member.Roster = team.Members;

        var json = await Serialize(team);

        json.Should().Contain("\"$ref\":\"#/members\"", "the cycle target is the collection wrapper itself");

        var restored = (await DeserializePooled<Team>(json))!;
        restored.Members[0].Roster.Should().BeSameAs(restored.Members);
    }

    [Test]
    public async Task ElementReferencingRootList_UsesRootPointer()
    {
        var roster = new List<Member>();
        roster.Add(new Member { Name = "m", Roster = roster });

        var json = await Serialize(roster);

        json.Should().Contain("\"$id\":\"#\"", "the referenced root wrapper keeps its id as the whole-document pointer");
        json.Should().Contain("\"$ref\":\"#\"");

        var restored = (await DeserializePooled<List<Member>>(json))!;
        restored[0].Roster.Should().BeSameAs(restored);
    }

    internal sealed class Catalog
    {
        public Dictionary<string, Node> ByCode { get; set; } = [];
        public Node? EchoA { get; set; }
        public Node? EchoB { get; set; }
    }

    [Test]
    public async Task DigitDictionaryKeys_ResolveAsMemberNames_NotArrayIndexes()
    {
        var zero = new Node { Name = "zero" };
        var padded = new Node { Name = "padded" };
        var graph = new Catalog
        {
            ByCode = new Dictionary<string, Node> { ["0"] = zero, ["01"] = padded },
            EchoA = zero,
            EchoB = padded
        };

        var json = await Serialize(graph);

        // RFC 6901 forbids leading zeros for array indexes; as member names both tokens are legal and stay opaque on read.
        json.Should().Contain("\"$ref\":\"#/byCode/0\"");
        json.Should().Contain("\"$ref\":\"#/byCode/01\"");

        var restored = (await DeserializePooled<Catalog>(json))!;
        restored.EchoA.Should().BeSameAs(restored.ByCode["0"]);
        restored.EchoB.Should().BeSameAs(restored.ByCode["01"]);
    }

    [Test]
    public async Task DictionaryKeysWithPointerSpecials_EscapeAndRoundTrip()
    {
        var slash = new Node { Name = "slash" };
        var tilde = new Node { Name = "tilde" };
        var graph = new Catalog
        {
            ByCode = new Dictionary<string, Node> { ["a/b"] = slash, ["x~1y"] = tilde },
            EchoA = slash,
            EchoB = tilde
        };

        var json = await Serialize(graph);

        json.Should().Contain("\"$ref\":\"#/byCode/a~1b\"", "'/' escapes to ~1");
        // A literal "~1" in the key must escape its '~' first, or unescaping would turn it into '/'.
        json.Should().Contain("\"$ref\":\"#/byCode/x~01y\"");

        var restored = (await DeserializePooled<Catalog>(json))!;
        restored.EchoA.Should().BeSameAs(restored.ByCode["a/b"]);
        restored.EchoB.Should().BeSameAs(restored.ByCode["x~1y"]);
    }

    [JsonDerivedType(typeof(Circle), "circle")]
    internal abstract class Shape
    {
        public string Label { get; set; } = string.Empty;
    }

    internal sealed class Circle : Shape
    {
        public double Radius { get; set; }
    }

    internal sealed class Drawing
    {
        public Shape? A { get; set; }
        public Shape? B { get; set; }
    }

    [Test]
    public async Task PolymorphicSharedInstance_KeepsTypeDiscriminatorAndIdentity()
    {
        var circle = new Circle { Label = "c", Radius = 2 };
        var json = await Serialize(new Drawing { A = circle, B = circle });

        json.Should().Contain("\"$type\":\"circle\"", "the discriminator is payload, not reference metadata");
        json.Should().Contain("\"$ref\":\"#/a\"");

        var restored = (await DeserializePooled<Drawing>(json))!;
        restored.B.Should().BeSameAs(restored.A);
        restored.A.Should().BeOfType<Circle>().Which.Radius.Should().Be(2);
    }

    [Test]
    public async Task HandWrittenPointerDocument_IdsMatchAsOpaqueStrings()
    {
        // Not serializer output: read-back needs no pointer evaluation, only byte-equal $id/$ref pairs in Preserve order.
        var restored = await DeserializePair(
            """{"left":{"$id":"#/left","name":"x"},"right":{"$ref":"#/left"}}""");

        restored!.Right.Should().BeSameAs(restored.Left);
        restored.Left!.Name.Should().Be("x");
    }

    [Test]
    public void RefPrecedingItsId_Throws()
    {
        // Pointers are never evaluated, so a forward reference has nothing to resolve against.
        Assert.ThrowsAsync<JsonException>(async () => await DeserializePair(
            """{"left":{"$ref":"#/right"},"right":{"$id":"#/right","name":"x"}}"""));
    }

    [Test]
    public void DuplicatePointerIds_Throw()
    {
        Assert.ThrowsAsync<JsonException>(async () => await DeserializePair(
            """{"left":{"$id":"#/left","name":"x"},"right":{"$id":"#/left","name":"y"}}"""));
    }

    [Test]
    public void UnknownPointerRef_Throws()
    {
        Assert.ThrowsAsync<JsonException>(async () => await DeserializePair(
            """{"left":{"$ref":"#/nowhere"}}"""));
    }

    [Test]
    public void StaleIdFromPreviousDocument_DoesNotResolve()
    {
        // maxRetained 1 pins one lease across both reads; a missed Reset would silently link doc 2 to doc 1's object instead of throwing.
        var deserializer = new PooledReferenceDeserializer<Pair>(SharedPooledOptions, maxRetained: 1);

        Assert.DoesNotThrowAsync(async () =>
        {
            using var first = new MemoryStream("""{"left":{"$id":"#/left","name":"x"},"right":{"$ref":"#/left"}}"""u8.ToArray());
            await deserializer.Deserialize(first);
        });

        Assert.ThrowsAsync<JsonException>(async () =>
        {
            using var second = new MemoryStream("""{"right":{"$ref":"#/left"}}"""u8.ToArray());
            await deserializer.Deserialize(second);
        });
    }

    [Test]
    public async Task IdNotMatchingItsDocumentLocation_StillResolves()
    {
        // Ids are never checked against the document structure — matching is purely textual.
        var restored = await DeserializePair(
            """{"left":{"$id":"#/wrong/9999","name":"x"},"right":{"$ref":"#/wrong/9999"}}""");

        restored!.Right.Should().BeSameAs(restored.Left);
    }

    [TestCase("#/items/-")] // dash: RFC 6901 append position, not a real index
    [TestCase("#/items/01")] // leading zero: invalid as an RFC array index
    [TestCase("#/items/4294967296")] // past int range
    [TestCase("#/")] // trailing slash: empty trailing token
    [TestCase("#//x")] // empty middle token
    [TestCase("#/a~2b")] // malformed escape
    [TestCase("")] // empty pointer: whole document in RFC terms
    public async Task PointerSyntaxIsNeverValidated_ExoticIdsMatchByteForByte(string id)
    {
        // RFC 6901 evaluation would reject or re-interpret these; the resolver requires only byte-equal $id/$ref.
        var restored = await DeserializePair(
            $$$"""{"left":{"$id":"{{{id}}}","name":"x"},"right":{"$ref":"{{{id}}}"}}""");

        restored!.Right.Should().BeSameAs(restored.Left);
    }
}
