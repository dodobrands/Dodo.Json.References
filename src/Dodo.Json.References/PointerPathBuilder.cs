using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Dodo.Json.References;

// Negative PropertyNameOffset = nameless segment ($values wrapper or array-in-array): contributes only its index.
[StructLayout(LayoutKind.Sequential)]
internal struct PathSegment
{
    public int PropertyNameOffset;
    public int PropertyNameLength;
    public int ArrayIndex;
    public bool IsArray;
}

internal static class PointerPathBuilder
{
    // Covers the reader's default MaxDepth of 64; deeper documents spill into pooled growth.
    internal const int StackAllocDepth = 80;

    // Hoisted to one per-document scratch: zero-init is paid once, not per path.
    internal const int ScratchSize = 512;

    private static readonly byte[] RootPathBytes = [.. "\"#\""u8];

    internal static void ClearTrackedIdPaths(long[] idPaths, ulong[] referencedBitmap, int bitmapWords, uint maxNumericId, int trackedIdCount)
    {
        // Measured crossover: ~1/16-1/8 density on 128B-line arm64, higher on 64B-line x64; a too-low
        // threshold costs a full-range memset on large sparse ranges, so 1/8 errs on the cheap side.
        if (trackedIdCount > (int)(maxNumericId >> 3))
        {
            Array.Clear(idPaths, 0, (int)maxNumericId + 1);
        }
        else
        {
            // Set bits cover every slot pass 2 can touch (a dangling $ref's slot is tracked but never written).
            for (var w = 0; w < bitmapWords; w++)
            {
                var word = referencedBitmap[w];
                while (word != 0)
                {
                    idPaths[(w << 6) + BitOperations.TrailingZeroCount(word)] = 0;
                    word &= word - 1;
                }
            }
        }
    }

    // Arena handle: offset in the high 32 bits, length in the low 32.
    internal static ReadOnlySpan<byte> Slice(byte[] arena, long packed)
        => arena.AsSpan((int)(packed >> 32), (int)packed);

    private static long AppendToArena(ReadOnlySpan<byte> value, ref byte[] arena, ref int used)
    {
        if (arena.Length - used < value.Length)
        {
            var required = (long)used + value.Length;
            var grown = ArrayPool<byte>.Shared.Rent(
                (int)Math.Clamp(Math.Max((long)arena.Length * 2, required), required, Array.MaxLength)
            );
            arena.AsSpan(0, used).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(arena);
            arena = grown;
        }

        value.CopyTo(arena.AsSpan(used));
        var packed = ((long)used << 32) | (uint)value.Length;
        used += value.Length;
        return packed;
    }

    internal static long AppendCurrentPath(
        ReadOnlySpan<byte> jsonSpan,
        ReadOnlySpan<PathSegment> pathStack,
        int depth,
        Span<byte> scratch,
        ref byte[] arena,
        ref int used)
    {
        if (depth == 0)
            return AppendToArena(RootPathBytes, ref arena, ref used);

        var path = new PooledSpanBuilder<byte>(scratch);
        try
        {
            path.Append((byte)'"');
            path.Append((byte)'#');

            var segments = pathStack[..depth];
            for (var i = 0; i < segments.Length; i++)
            {
                ref readonly var seg = ref segments[i];

                // Worst case per segment: separators + fully escaped name + ten index digits + quote.
                path.EnsureFree(3 * seg.PropertyNameLength + 13);
                var dst = path.FreeSpan;
                var pos = 0;
                if (seg.PropertyNameOffset >= 0)
                {
                    var propNameSpan = jsonSpan.Slice(seg.PropertyNameOffset, seg.PropertyNameLength);
                    dst[pos++] = (byte)'/';
                    if (!propNameSpan.ContainsAnyExcept(JsonPointerEncoding.PointerLiteralBytes))
                    {
                        propNameSpan.CopyTo(dst[pos..]);
                        pos += propNameSpan.Length;
                    }
                    else
                    {
                        pos = JsonPointerEncoding.WriteEscaped(propNameSpan, dst, pos);
                    }
                }

                if (seg is { IsArray: true, ArrayIndex: >= 0 })
                {
                    dst[pos++] = (byte)'/';
                    seg.ArrayIndex.TryFormat(dst[pos..], out var written, provider: CultureInfo.InvariantCulture);
                    pos += written;
                }

                path.Advance(pos);
            }

            path.Append((byte)'"');

            return AppendToArena(path.WrittenSpan, ref arena, ref used);
        }
        finally
        {
            path.Dispose();
        }
    }

    // Primitive and null array elements advance the enclosing index too, or later pointer paths drift.
    internal static void BumpIndexForArrayElement(Span<PathSegment> pathStack, int depth, int pendingPropertyOffset)
    {
        if (pendingPropertyOffset >= 0 || depth == 0)
            return;

        ref var top = ref pathStack[depth - 1];
        if (top.IsArray)
            top.ArrayIndex++;
    }

    internal static Span<PathSegment> GrowPathStack(Span<PathSegment> current, ref PathSegment[]? rented)
    {
        var grown = ArrayPool<PathSegment>.Shared.Rent(current.Length * 2);
        current.CopyTo(grown);
        if (rented is not null)
        {
            ArrayPool<PathSegment>.Shared.Return(rented);
        }

        rented = grown;
        return grown;
    }
}
