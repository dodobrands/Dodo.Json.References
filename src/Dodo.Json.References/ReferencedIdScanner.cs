using System.Numerics;
using System.Text.Json;

namespace Dodo.Json.References;

internal static class ReferencedIdScanner
{
    internal const int StackAllocIdCount = 128;

    // Learned grow floor, capped so one pathological document cannot inflate it for good.
    private const int MaxSizeHint = 65_536;
    private static int _sizeHint = 4096;

    internal static int SizeHint
        => Volatile.Read(ref _sizeHint);

    private static ReadOnlySpan<byte> RefPattern
        => "\"$ref\":"u8;

    internal static void Collect(
        ReadOnlySpan<byte> jsonSpan,
        ref PooledSpanBuilder<uint> ids,
        out uint maxNumericId,
        ref HashSet<string>? overflow)
    {
        maxNumericId = 0;
        var offset = 0;
        Span<char> decodeScratch = stackalloc char[NumericId.MaxEscapedLength];

        while (true)
        {
            var idx = jsonSpan[offset..].IndexOf(RefPattern);
            if (idx < 0)
                break;

            var afterColon = offset + idx + RefPattern.Length;
            var gap = jsonSpan[afterColon..].IndexOfAnyExcept(JsonReferenceTransformer.JsonWhitespace);
            if (gap < 0)
                break;

            var quoteAt = afterColon + gap;
            if (jsonSpan[quoteAt] != (byte)'"')
            {
                offset = quoteAt;
                continue;
            }

            var valueAt = quoteAt + 1;

            var endQuote = jsonSpan[valueAt..].IndexOf((byte)'"');
            if (endQuote < 0)
                break;

            var idBytes = jsonSpan.Slice(valueAt, endQuote);
            offset = valueAt + endQuote + 1;

            if (NumericId.TryParse(idBytes, out var numericId))
            {
                ids.Append(numericId);
                if (numericId > maxNumericId)
                {
                    maxNumericId = numericId;
                }

                continue;
            }

            if (idBytes.IndexOf((byte)'\\') < 0)
            {
                (overflow ??= []).Add(System.Text.Encoding.UTF8.GetString(idBytes));
                continue;
            }

            var escapedReader = new Utf8JsonReader(jsonSpan[quoteAt..], isFinalBlock: false, state: default);
            if (!escapedReader.Read() || escapedReader.TokenType != JsonTokenType.String)
            {
                continue;
            }

            offset = quoteAt + (int)escapedReader.BytesConsumed;
            if (escapedReader.ValueSpan.Length > NumericId.MaxEscapedLength)
            {
                (overflow ??= []).Add(escapedReader.GetString()!);
                continue;
            }

            var decodedLength = escapedReader.CopyString(decodeScratch);
            var decodedId = decodeScratch[..decodedLength];
            if (NumericId.TryParse(decodedId, out var escapedNumericId))
            {
                ids.Append(escapedNumericId);
                if (escapedNumericId > maxNumericId)
                {
                    maxNumericId = escapedNumericId;
                }

                continue;
            }

            (overflow ??= []).Add(new string(decodedId));
        }

        var learned = Math.Min(MaxSizeHint, (long)BitOperations.RoundUpToPowerOf2((uint)ids.Count));
        InterlockedMath.Max(ref _sizeHint, (int)learned);
    }
}
