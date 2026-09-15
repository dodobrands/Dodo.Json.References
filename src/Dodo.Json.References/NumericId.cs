using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Dodo.Json.References;

internal static class NumericId
{
    // Exclusive: renting bound + 1 slots would round up to the next ArrayPool bucket (32MB, not 16MB).
    private const uint MaxDense = 1 << 21;

    internal const int MaxEscapedLength = 7 * 6;

    // Digits only, no leading zero, under the dense bound — exotic ids can never alias a dense slot.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryParse(ReadOnlySpan<byte> value, out uint id)
    {
        id = 0;
        if (value.Length is < 1 or > 7 || (value[0] == (byte)'0' && value.Length != 1))
        {
            return false;
        }

        uint parsed = 0;
        foreach (var b in value)
        {
            var digit = (uint)(b - (byte)'0');
            if (digit > 9)
            {
                return false;
            }

            parsed = parsed * 10 + digit;
        }

        if (parsed >= MaxDense)
        {
            return false;
        }

        id = parsed;
        return true;
    }

    internal static bool TryParse(ReadOnlySpan<char> value, out uint id)
    {
        id = 0;
        if (value.Length is < 1 or > 7 || (value[0] == '0' && value.Length != 1))
        {
            return false;
        }

        uint parsed = 0;
        foreach (var c in value)
        {
            var digit = (uint)(c - '0');
            if (digit > 9)
            {
                return false;
            }

            parsed = parsed * 10 + digit;
        }

        if (parsed >= MaxDense)
        {
            return false;
        }

        id = parsed;
        return true;
    }

    internal static bool TryRead(ref Utf8JsonReader reader, scoped Span<char> decodeScratch, out uint id)
    {
        id = 0;
        if (reader.TokenType != JsonTokenType.String)
        {
            return false;
        }

        if (!reader.ValueIsEscaped)
        {
            return TryParse(reader.ValueSpan, out id);
        }

        if (reader.ValueSpan.Length > MaxEscapedLength)
        {
            return false;
        }

        var length = reader.CopyString(decodeScratch);
        return TryParse(decodeScratch[..length], out id);
    }
}
