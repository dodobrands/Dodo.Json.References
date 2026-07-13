using System.Text.Json;
using Dodo.Json.References;
using FluentAssertions;
using NUnit.Framework;

namespace Dodo.Json.References.Tests;

[TestFixture]
internal class PooledInfrastructureTests
{
    [Test]
    public void PooledJsonBufferWriter_GetSpanAndGetMemory_AccumulateWrites()
    {
        using var writer = new PooledJsonBufferWriter();

        "abc"u8.CopyTo(writer.GetSpan(3));
        writer.Advance(3);

        "def"u8.CopyTo(writer.GetMemory(3).Span);
        writer.Advance(3);

        // A non-positive size hint still hands out at least one writable byte.
        var tail = writer.GetSpan(0);
        tail.Length.Should().BeGreaterThan(0);
        tail[0] = (byte)'!';
        writer.Advance(1);

        writer.WrittenMemory.ToArray().Should().Equal("abcdef!"u8.ToArray());
    }

    [Test]
    public void InterlockedMax_IsMonotonicUnderConcurrency()
    {
        var location = 0;
        Parallel.For(1, 10_000, i => InterlockedMath.Max(ref location, i % 977));

        location.Should().Be(976, "no stale writer may regress the learned maximum");

        InterlockedMath.Max(ref location, 5);
        location.Should().Be(976);
        InterlockedMath.Max(ref location, 10_000);
        location.Should().Be(10_000);
    }

    [Test]
    public void PoolingReferenceResolver_TracksReferenceTypesAndSkipsValueTypes()
    {
        var resolver = new PoolingReferenceResolver();
        var first = new object();
        var second = new object();

        resolver.GetReference(first, out var firstExists).Should().Be("1");
        firstExists.Should().BeFalse();
        resolver.GetReference(second, out _).Should().Be("2");
        resolver.GetReference(first, out var firstAgainExists).Should().Be("1");
        firstAgainExists.Should().BeTrue();

        // Boxed value types compare by content, so tracking them would produce false shared refs.
        resolver.GetReference(42, out var boxedExists).Should().BeEmpty();
        boxedExists.Should().BeFalse();
        resolver.GetReference(42, out boxedExists).Should().BeEmpty();
        boxedExists.Should().BeFalse();
    }

    [Test]
    public void PoolingReferenceResolver_ResolvesAddedReferencesAndThrowsOnUnknownOrDuplicate()
    {
        var resolver = new PoolingReferenceResolver();
        var value = new object();

        resolver.AddReference("#/items/0", value);
        resolver.ResolveReference("#/items/0").Should().BeSameAs(value);

        Assert.Throws<JsonException>(() => resolver.AddReference("#/items/0", new object()));
        Assert.Throws<JsonException>(() => resolver.ResolveReference("#/missing"));
    }

    [Test]
    public void PoolingReferenceResolver_IdsStayCorrectPastTheNumberCache()
    {
        var resolver = new PoolingReferenceResolver();
        string last = string.Empty;
        for (var i = 0; i < 65_540; i++)
        {
            last = resolver.GetReference(new object(), out _);
        }

        // 65,536 sits exactly on the cache boundary, 65,537+ takes the uncached path.
        last.Should().Be("65540");
    }

    [Test]
    public void DictionaryPool_ReusesClearedInstancesRegardlessOfGrownSize()
    {
        var pool = ObjectPoolFactory.CreateDictionaryPool<string, int>();

        var small = pool.Get();
        small["a"] = 1;
        pool.Return(small);

        var reused = pool.Get();
        reused.Should().BeSameAs(small, "a returned dictionary is retained");
        reused.Should().BeEmpty("retained dictionaries come back cleared");

        for (var i = 0; i < 20_000; i++)
        {
            reused[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = i;
        }

        var capacityBefore = reused.EnsureCapacity(0);
        pool.Return(reused);

        var big = pool.Get();
        big.Should().BeSameAs(reused, "grown capacity is the point of the pooling — no size cap on return");
        big.Should().BeEmpty();
        big.EnsureCapacity(0).Should().Be(capacityBefore, "the grown backing arrays survive the pooled cycle");
    }

    [Test]
    public void DictionaryPool_HonorsComparerAndInitialCapacity()
    {
        var pool = ObjectPoolFactory.CreateDictionaryPool<string, int>(comparer: StringComparer.OrdinalIgnoreCase);
        var dict = pool.Get();
        dict["Key"] = 1;
        dict.ContainsKey("kEY").Should().BeTrue();

        var sized = new AlwaysReturnPooledDictionaryPolicy<string, int>(capacity: 128).Create();
        sized.EnsureCapacity(0).Should().BeGreaterThanOrEqualTo(128);
    }

}
