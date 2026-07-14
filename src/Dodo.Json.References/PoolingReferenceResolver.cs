using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dodo.Json.References;

/// <summary>A reference resolver whose maps outlive a single document; call <see cref="Reset"/> before reusing it.</summary>
public sealed class PoolingReferenceResolver : ReferenceResolver
{
    private static readonly string?[] NumberCache = new string[65_536];
    private readonly Dictionary<string, object> _referenceIdToObjectMap = new();
    private readonly IdentityIdMap _objectToReferenceIdMap = new();

    /// <inheritdoc />
    public override void AddReference(string referenceId, object value)
    {
        if (!_referenceIdToObjectMap.TryAdd(referenceId, value))
        {
            throw new JsonException();
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public override object ResolveReference(string referenceId) =>
        _referenceIdToObjectMap.TryGetValue(referenceId, out var value)
            ? value
            : throw new JsonException();

    /// <summary>Clears both maps for the next document.</summary>
    /// <remarks>Keeps capacity at the high-water mark; trimming would regrow the arrays every reuse cycle.</remarks>
    public void Reset()
    {
        _referenceIdToObjectMap.Clear();
        _objectToReferenceIdMap.Reset();
    }

    internal (int ReadMap, int WriteMap) RetainedCapacities
        => (_referenceIdToObjectMap.EnsureCapacity(0), _objectToReferenceIdMap.Capacity);
}
