using FluentAssertions;
using NUnit.Framework;

namespace Dodo.Json.References.Tests;

[TestFixture]
internal sealed class PooledSpanBuilderTests
{
    [Test]
    public void GrowsFromStackScratchPreservingContent()
    {
        var builder = new PooledSpanBuilder<byte>(stackalloc byte[8]);
        for (var i = 0; i < 300; i++)
        {
            builder.Append((byte)(i % 251));
        }

        builder.Count.Should().Be(300);
        var result = builder.ToArray();
        builder.Dispose();

        var expected = new byte[300];
        for (var i = 0; i < 300; i++)
        {
            expected[i] = (byte)(i % 251);
        }

        result.Should().Equal(expected);
    }

    [Test]
    public void FirstGrowLandsAtGrowFloor()
    {
        var builder = new PooledSpanBuilder<uint>(stackalloc uint[4], growFloor: 4096);
        for (var i = 0u; i < 5; i++)
        {
            builder.Append(i);
        }

        (builder.Count + builder.FreeSpan.Length).Should().BeGreaterThanOrEqualTo(4096);
        builder.Dispose();
    }

    [Test]
    public void EnsureFreeAdvanceRoundTripsAndDisposeIsIdempotent()
    {
        var builder = new PooledSpanBuilder<byte>(stackalloc byte[4]);
        builder.EnsureFree(100);
        builder.FreeSpan.Length.Should().BeGreaterThanOrEqualTo(100);

        "hello"u8.CopyTo(builder.FreeSpan);
        builder.Advance(5);

        builder.ToArray().Should().Equal("hello"u8.ToArray());
        builder.Dispose();
        builder.Dispose();
    }
}
