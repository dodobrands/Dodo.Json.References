using FluentAssertions;
using NUnit.Framework;

#pragma warning disable CA5394 // deterministic seed drives test data, not security

namespace Dodo.Json.References.Tests;

[TestFixture]
internal sealed class IdentityIdMapTests
{
    [Test]
    public void DegenerateCapacitiesFloorAtMinimumSize()
    {
        new IdentityIdMap(0).Capacity.Should().Be(16);
        new IdentityIdMap(-5).Capacity.Should().Be(16);
    }

    [Test]
    public void AssignsSequentialIdsAndRecognizesRepeats()
    {
        var map = new IdentityIdMap();
        var a = new object();
        var b = new object();

        map.GetOrAdd(a, out var existed).Should().Be(1u);
        existed.Should().BeFalse();
        map.GetOrAdd(b, out existed).Should().Be(2u);
        existed.Should().BeFalse();
        map.GetOrAdd(a, out existed).Should().Be(1u, "a repeat returns the originally issued id");
        existed.Should().BeTrue();
    }

    [Test]
    public void GrowthPreservesIssuedIdsAndLookups()
    {
        var map = new IdentityIdMap(capacity: 4);
        var objects = Enumerable.Range(0, 10_000).Select(_ => new object()).ToArray();

        for (var i = 0; i < objects.Length; i++)
        {
            map.GetOrAdd(objects[i], out var existed).Should().Be((uint)(i + 1));
            existed.Should().BeFalse();
        }

        for (var i = 0; i < objects.Length; i++)
        {
            map.GetOrAdd(objects[i], out var existed).Should().Be((uint)(i + 1));
            existed.Should().BeTrue();
        }
    }

    [Test]
    public void ResetRestartsIdsAndRetainsCapacity()
    {
        var map = new IdentityIdMap(capacity: 4);
        for (var i = 0; i < 50_000; i++)
        {
            map.GetOrAdd(new object(), out _);
        }

        var capacityBefore = map.Capacity;
        map.Reset();
        map.Capacity.Should().Be(capacityBefore, "reset keeps the grown backing arrays for reuse");

        var fresh = new object();
        map.GetOrAdd(fresh, out var existed).Should().Be(1u, "ids restart per document");
        existed.Should().BeFalse();
    }

    [Test]
    public void MatchesDictionaryOracleAcrossReuseCycles()
    {
        var map = new IdentityIdMap(capacity: 8);
        var rnd = new Random(42);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            map.Reset();
            var oracle = new Dictionary<object, uint>(ReferenceEqualityComparer.Instance);
            var pool = Enumerable.Range(0, 40_000).Select(_ => new object()).ToArray();

                for (var i = 0; i < 120_000; i++)
            {
                var o = pool[rnd.Next(pool.Length)];
                var id = map.GetOrAdd(o, out var existed);

                if (oracle.TryGetValue(o, out var expected))
                {
                    existed.Should().BeTrue();
                    id.Should().Be(expected);
                }
                else
                {
                    existed.Should().BeFalse();
                    id.Should().Be((uint)(oracle.Count + 1));
                    oracle[o] = id;
                }
            }
        }
    }
}
