using System.Numerics;
using System.Runtime.CompilerServices;

namespace Dodo.Json.References;

/// <summary>Open-addressing identity-to-sequential-id map (null slot = free, no deletions); ~25% faster than Dictionary + ReferenceEqualityComparer.</summary>
internal sealed class IdentityIdMap
{
    private object?[] _keys;
    private uint[] _ids;
    private int _count;
    private int _mask;

    public IdentityIdMap(int capacity = 1024)
    {
        // Long math: capacity * 2 wraps negative at 2^30 and would silently yield a 16-slot table.
        var size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(capacity * 2L, 16, 1L << 30));
        _keys = new object?[size];
        _ids = new uint[size];
        _mask = size - 1;
    }

    public int Capacity
        => _keys.Length;

    // Clearing the keys releases the previous document's object roots; the grown arrays stay.
    public void Reset()
    {
        Array.Clear(_keys, 0, _keys.Length);
        _count = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetOrAdd(object key, out bool existed)
    {
        // Hash once: stable per object, so the grow-retry re-probe reuses it.
        var hash = RuntimeHelpers.GetHashCode(key);
        while (true)
        {
            var keys = _keys;
            var mask = _mask;
            var i = hash & mask;
            while (true)
            {
                var slot = keys[i];
                if (slot is null)
                {
                    if (_count >= (mask + 1) >> 1)
                    {
                        break; // grow, then retry against the new arrays
                    }

                    keys[i] = key;
                    var id = (uint)++_count;
                    _ids[i] = id;
                    existed = false;
                    return id;
                }

                if (ReferenceEquals(slot, key))
                {
                    existed = true;
                    return _ids[i];
                }

                i = (i + 1) & mask;
            }

            Grow();
        }
    }

    private void Grow()
    {
        var oldKeys = _keys;
        var oldIds = _ids;
        var newSize = oldKeys.Length * 2;
        var keys = new object?[newSize];
        var ids = new uint[newSize];
        var mask = newSize - 1;
        for (var j = 0; j < oldKeys.Length; j++)
        {
            if (oldKeys[j] is { } key)
            {
                var i = RuntimeHelpers.GetHashCode(key) & mask;
                while (keys[i] is not null)
                {
                    i = (i + 1) & mask;
                }

                keys[i] = key;
                ids[i] = oldIds[j];
            }
        }

        _keys = keys;
        _ids = ids;
        _mask = mask;
    }
}
