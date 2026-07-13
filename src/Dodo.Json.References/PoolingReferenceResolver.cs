using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dodo.Json.References;

public sealed class PoolingReferenceResolver : ReferenceResolver
{
    private static readonly string?[] NumberCache = new string[65_536];
    private readonly Dictionary<string, object> _referenceIdToObjectMap = new();
    // GetReference runs once per tracked object; see IdentityIdMap for why it beats Dictionary here.
    private readonly IdentityIdMap _objectToReferenceIdMap = new();

    public override void AddReference(string referenceId, object value)
    {
        if (!_referenceIdToObjectMap.TryAdd(referenceId, value))
        {
            throw new JsonException();
        }
    }

    public override string GetReference(object value, out bool alreadyExists)
    {
        // Boxed value types compare by content, not reference; skip tracking to avoid false hits.
        if (value is ValueType)
        {
            alreadyExists = false;
            return string.Empty;
        }

        var id = _objectToReferenceIdMap.GetOrAdd(value, out alreadyExists);
        return UInt32ToDecStr(id);
    }

    private static string UInt32ToDecStr(uint value)
        => value <= NumberCache.Length ? UInt32ToDecStrForKnownSmallNumber(value) : value.ToString(CultureInfo.InvariantCulture);

    private static string UInt32ToDecStrForKnownSmallNumber(uint value)
    {
        return NumberCache[value - 1] ?? CreateAndCacheString(value);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string CreateAndCacheString(uint value)
            => NumberCache[value - 1] = value.ToString(CultureInfo.InvariantCulture);
    }

    public override object ResolveReference(string referenceId) =>
        _referenceIdToObjectMap.TryGetValue(referenceId, out var value)
            ? value
            : throw new JsonException();

    // Keeps capacity at the high-water mark; trimming would regrow the arrays every reuse cycle.
    public void Reset()
    {
        _referenceIdToObjectMap.Clear();
        _objectToReferenceIdMap.Reset();
    }

    internal (int ReadMap, int WriteMap) RetainedCapacities
        => (_referenceIdToObjectMap.EnsureCapacity(0), _objectToReferenceIdMap.Capacity);
}
