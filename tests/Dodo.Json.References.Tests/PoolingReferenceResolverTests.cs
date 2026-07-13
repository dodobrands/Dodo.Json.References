using Dodo.Json.References;
using FluentAssertions;
using NUnit.Framework;

namespace Dodo.Json.References.Tests;

[TestFixture]
internal class PoolingReferenceResolverTests
{
    // Documents only grow over time, so Reset must retain the grown backing arrays for reuse —
    // trimming would recreate them on every pooled cycle and fragment the heap.
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

    // Large documents track tens of thousands of ids; capacity in that range must survive Reset
    // untouched, otherwise every pooled reuse would trim and regrow the maps.
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
