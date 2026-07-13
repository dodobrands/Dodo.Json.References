using System.Buffers;
using System.Numerics;

namespace Dodo.Json.References;

/// <summary>
/// ArrayPool-backed whole-document buffer renting at a learned high-water hint: the first document
/// pays a grow ladder once, then every rent is right-sized and the shared pool handles trimming.
/// </summary>
internal sealed class PooledJsonBufferWriter: IBufferWriter<byte>, IDisposable
{
    private const int InitialCapacity = 64 * 1024;

    // Cap: one pathological document must not permanently inflate every future rent.
    private const int MaxCapacityHint = 64 * 1024 * 1024;

    // Ratchets monotonically to the observed high-water mark (see InterlockedMath.Max).
    private static int _initialCapacityHint = InitialCapacity;

    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(Volatile.Read(ref _initialCapacityHint));
    private int _written;

    public ReadOnlyMemory<byte> WrittenMemory
        => _buffer.AsMemory(0, _written);

    public void Advance(int count)
        => _written += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void Dispose()
    {
        var learned = Math.Min(MaxCapacityHint, (long)BitOperations.RoundUpToPowerOf2((uint)_written));
        InterlockedMath.Max(ref _initialCapacityHint, (int)learned);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 1)
        {
            sizeHint = 1;
        }

        if (_buffer.Length - _written >= sizeHint)
        {
            return;
        }

        // Long math so doubling near 2^31 cannot wrap; an unfittable document fails on Clamp's guard.
        var required = (long)_written + sizeHint;
        var grown = ArrayPool<byte>.Shared.Rent(
            (int)Math.Clamp(Math.Max((long)_buffer.Length * 2, required), required, Array.MaxLength)
        );
        _buffer.AsSpan(0, _written).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }
}
