using System.Buffers;
using System.Runtime.CompilerServices;

namespace Dodo.Json.References;

/// <summary>Stack-first growable buffer spilling to the shared ArrayPool; growFloor lands the first pool hop at a learned high-water mark.</summary>
internal ref struct PooledSpanBuilder<T>(Span<T> initialBuffer, int growFloor = 0)
{
    private Span<T> _buffer = initialBuffer;
    private T[]? _rented;

    public int Count { get; private set; }

    public readonly ReadOnlySpan<T> WrittenSpan
        => _buffer[..Count];

    public readonly Span<T> FreeSpan
        => _buffer[Count..];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(T item)
    {
        var pos = Count;
        if ((uint)pos < (uint)_buffer.Length)
        {
            _buffer[pos] = item;
            Count = pos + 1;
        }
        else
        {
            GrowAndAppend(item);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowAndAppend(T item)
    {
        Grow(Count + 1);
        _buffer[Count++] = item;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureFree(int length)
    {
        if (Count + length > _buffer.Length)
        {
            Grow(Count + length);
        }
    }

    public void Advance(int length)
        => Count += length;

    private void Grow(int required)
    {
        var grown = ArrayPool<T>.Shared.Rent(Math.Max(Math.Max(_buffer.Length * 2, required), growFloor));
        _buffer[..Count].CopyTo(grown);
        var old = _rented;
        _rented = grown;
        _buffer = grown;
        if (old is not null)
        {
            ArrayPool<T>.Shared.Return(old);
        }
    }

    public readonly T[] ToArray()
        => [.. WrittenSpan];

    public void Dispose()
    {
        var rented = _rented;
        if (rented is null) return;
        _rented = null;
        ArrayPool<T>.Shared.Return(rented);
    }
}