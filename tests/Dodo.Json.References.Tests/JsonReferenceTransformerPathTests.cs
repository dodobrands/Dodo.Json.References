using System.Text.Json;
using System.Text.Json.Serialization;
using Dodo.Json.References;
using FluentAssertions;
using NUnit.Framework;

namespace Dodo.Json.References.Tests;

/// <summary>
/// Pointer-path tracking for array shapes beyond flat object arrays: $values wrappers at any depth,
/// arrays nested as array elements, and collections at the document root. Every referenced $id must
/// get a unique RFC 6901 path that reflects its real position, or deserialization dies on duplicate ids.
/// </summary>
[TestFixture]
internal class JsonReferenceTransformerPathTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.Preserve
    };

    internal sealed class Node
    {
        public string Name { get; set; } = string.Empty;
    }

    internal sealed class NestedListsGraph
    {
        public List<List<Node>> Outer { get; set; } = [];
    }

    internal sealed class JaggedGraph
    {
        public Node[][] Jagged { get; set; } = [];
    }

    internal sealed class DeepNest
    {
        public DeepNest? Child { get; set; }
        public Node? Leaf { get; set; }
        public Node? Echo { get; set; }
    }

    private static async Task<string> Serialize<T>(T payload, JsonSerializerOptions? options = null)
    {
        await using var buffer = new MemoryStream();
        await JsonReferenceTransformer.SerializeWithPointers(payload, buffer, options ?? Options);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    [Test]
    public async Task RootList_TwoSharedNodes_GetUniqueElementPointers()
    {
        var a = new Node { Name = "a" };
        var b = new Node { Name = "b" };
        var payload = new List<Node> { a, a, b, b };

        var json = await Serialize(payload);

        json.Should().Contain("\"$ref\":\"#/0\"", "first shared element is addressed by its root index");
        json.Should().Contain("\"$ref\":\"#/2\"", "second shared element is addressed by its root index");

        var restored = JsonSerializer.Deserialize<List<Node>>(json, Options)!;
        restored.Should().HaveCount(4);
        restored[0].Should().BeSameAs(restored[1]);
        restored[2].Should().BeSameAs(restored[3]);
        restored[0].Should().NotBeSameAs(restored[2]);
    }

    [Test]
    public async Task RootArray_TwoSharedNodes_GetUniqueElementPointers()
    {
        var a = new Node { Name = "a" };
        var b = new Node { Name = "b" };
        var payload = new[] { a, a, b, b };

        var json = await Serialize(payload);

        json.Should().Contain("\"$ref\":\"#/0\"");
        json.Should().Contain("\"$ref\":\"#/2\"");

        var restored = JsonSerializer.Deserialize<Node[]>(json, Options)!;
        restored[0].Should().BeSameAs(restored[1]);
        restored[2].Should().BeSameAs(restored[3]);
        restored[0].Should().NotBeSameAs(restored[2]);
    }

    [Test]
    public async Task NestedLists_SharedNodesInEachInnerList_GetUniquePointers()
    {
        var x = new Node { Name = "x" };
        var y = new Node { Name = "y" };
        var z = new Node { Name = "z" };
        var graph = new NestedListsGraph
        {
            Outer =
            [
                [x, x],
                [y, y, z, z]
            ]
        };

        var json = await Serialize(graph);

        json.Should().Contain("\"$ref\":\"#/outer/0/0\"", "x lives at inner list 0, element 0");
        json.Should().Contain("\"$ref\":\"#/outer/1/0\"", "y lives at inner list 1, element 0");
        json.Should().Contain("\"$ref\":\"#/outer/1/2\"", "z lives at inner list 1, element 2");

        var restored = JsonSerializer.Deserialize<NestedListsGraph>(json, Options)!;
        restored.Outer[0][0].Should().BeSameAs(restored.Outer[0][1]);
        restored.Outer[1][0].Should().BeSameAs(restored.Outer[1][1]);
        restored.Outer[1][2].Should().BeSameAs(restored.Outer[1][3]);
        restored.Outer[1][0].Should().NotBeSameAs(restored.Outer[0][0]);
    }

    [Test]
    public async Task JaggedArray_SharedNodesAcrossInnerArrays_GetUniquePointers()
    {
        var a = new Node { Name = "a" };
        var b = new Node { Name = "b" };
        var graph = new JaggedGraph
        {
            Jagged =
            [
                [a],
                [a, b],
                [b]
            ]
        };

        var json = await Serialize(graph);

        json.Should().Contain("\"$id\":\"#/jagged/0/0\"", "a is first written at inner array 0, element 0");
        json.Should().Contain("\"$ref\":\"#/jagged/0/0\"");
        json.Should().Contain("\"$id\":\"#/jagged/1/1\"", "b is first written at inner array 1, element 1");
        json.Should().Contain("\"$ref\":\"#/jagged/1/1\"");

        var restored = JsonSerializer.Deserialize<JaggedGraph>(json, Options)!;
        restored.Jagged[0][0].Should().BeSameAs(restored.Jagged[1][0]);
        restored.Jagged[1][1].Should().BeSameAs(restored.Jagged[2][0]);
    }

    internal sealed class DeepArrayGraph
    {
        public object[] Deep { get; set; } = [];
        public Node? Echo { get; set; }
    }

    [Test]
    public async Task DeeplyNestedArrays_TrackEveryLevelInPointerPaths()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = ReferenceHandler.Preserve,
            MaxDepth = 512
        };

        // 300 array levels: outgrows the 80-slot stack path segment scratch and then the first
        // pooled replacement (Rent(160) hands out a 256-slot bucket).
        var shared = new Node { Name = "leaf" };
        object[] nested = [shared];
        for (var i = 0; i < 299; i++)
        {
            nested = [nested];
        }

        var graph = new DeepArrayGraph { Deep = nested, Echo = shared };
        var json = await Serialize(graph, options);

        var leafPointer = "#/deep" + string.Concat(Enumerable.Repeat("/0", 300));
        json.Should().Contain($"\"$id\":\"{leafPointer}\"");
        json.Should().Contain($"\"$ref\":\"{leafPointer}\"");
    }

    [Test]
    public async Task DeepDocument_HonorsCallerMaxDepth()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = ReferenceHandler.Preserve,
            MaxDepth = 256
        };

        var shared = new Node { Name = "leaf" };
        var root = new DeepNest();
        var current = root;
        for (var i = 0; i < 100; i++)
        {
            current.Child = new DeepNest();
            current = current.Child;
        }

        current.Leaf = shared;
        root.Echo = shared;

        var json = await Serialize(root, options);

        json.Should().Contain("\"$ref\":\"", "the deep leaf is shared and must be reference-rewritten");

        var restored = JsonSerializer.Deserialize<DeepNest>(json, options)!;
        var walker = restored;
        while (walker.Child is not null)
        {
            walker = walker.Child;
        }

        walker.Leaf.Should().BeSameAs(restored.Echo);
    }
}
