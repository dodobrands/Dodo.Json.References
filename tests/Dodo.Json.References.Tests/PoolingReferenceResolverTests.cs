using FluentAssertions;
using NUnit.Framework;

namespace Dodo.Json.References.Tests;

[TestFixture]
internal sealed class PoolingReferenceResolverTests
{
    // Reset must retain the grown backing arrays; trimming would regrow them on every pooled cycle.
    [Test]
    public void ResetRetainsGrownCapacityForReuse()
    {
        var resolver = new PoolingReferenceResolver();
        for (var i = 0; i < 100_000; i++)
        {
            var value = new object();
            resolver.AddReference($"#/items/{i}", value);
            resolver.GetReference(value, out _);
        }

        var (readBefore, writeBefore) = resolver.RetainedCapacities;
        resolver.Reset();
        var (readAfter, writeAfter) = resolver.RetainedCapacities;

        readBefore.Should().BeGreaterThan(100_000);
        writeBefore.Should().BeGreaterThan(100_000);
        readAfter.Should().Be(readBefore);
        writeAfter.Should().Be(writeBefore);
    }

    [Test]
    public void ResetKeepsCapacityOfMenuScaleMaps()
    {
        var resolver = new PoolingReferenceResolver();
        for (var i = 0; i < 40_000; i++)
        {
            resolver.AddReference($"#/items/{i}", new object());
        }

        var (readBefore, _) = resolver.RetainedCapacities;
        resolver.Reset();
        var (readAfter, _) = resolver.RetainedCapacities;

        readAfter.Should().Be(readBefore);
    }
}
