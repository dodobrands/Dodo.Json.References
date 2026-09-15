using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace Dodo.Json.References;

/// <summary>RFC 6901 section 3 token escaping followed by RFC 3986 percent-encoding of the fragment-disallowed octets.</summary>
internal static class JsonPointerEncoding
{
    internal static readonly SearchValues<byte> PointerLiteralBytes = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._!$&'()*+,;=:@?"u8
    );

    private static readonly byte[] UpperHexDigits = [.. "0123456789ABCDEF"u8];

    internal static int WriteEscaped(ReadOnlySpan<byte> propertyName, Span<byte> buffer, int pos)
    {
        Span<byte> decoded = stackalloc byte[4];
        var i = 0;
        while (i < propertyName.Length)
        {
            scoped ReadOnlySpan<byte> logical;
            if (propertyName[i] == (byte)'\\')
            {
                i += DecodeJsonEscape(propertyName[i..], decoded, out var written);
                logical = decoded[..written];
            }
            else
            {
                logical = propertyName.Slice(i, 1);
                i++;
            }

            foreach (var b in logical)
            {
                pos = WritePointerByte(b, buffer, pos);
            }
        }

        return pos;
    }

    private static int WritePointerByte(byte b, Span<byte> buffer, int pos)
    {
        switch (b)
        {
            case (byte)'/':
                buffer[pos++] = (byte)'~';
                buffer[pos++] = (byte)'1';
                return pos;

            case (byte)'~':
                buffer[pos++] = (byte)'~';
                buffer[pos++] = (byte)'0';
                return pos;
        }

        if (PointerLiteralBytes.Contains(b))
        {
            buffer[pos++] = b;
            return pos;
        }

        buffer[pos++] = (byte)'%';
        buffer[pos++] = UpperHexDigits[b >> 4];
        buffer[pos++] = UpperHexDigits[b & 0xF];
        return pos;
    }

    private static int DecodeJsonEscape(ReadOnlySpan<byte> tail, scoped Span<byte> decoded, out int written)
    {
        written = 1;
        if (tail.Length < 2)
        {
            decoded[0] = tail[0];
            return 1;
        }

        switch (tail[1])
        {
            case (byte)'b':
                decoded[0] = 0x08;
                return 2;

            case (byte)'f':
                decoded[0] = 0x0C;
                return 2;

            case (byte)'n':
                decoded[0] = 0x0A;
                return 2;

            case (byte)'r':
                decoded[0] = 0x0D;
                return 2;

            case (byte)'t':
                decoded[0] = 0x09;
                return 2;

            case (byte)'u' when tail.Length >= 6:
                break;

            default:
                decoded[0] = tail[1];
                return 2;
        }

        _ = Utf8Parser.TryParse(tail[2..6], out int scalar, out _, 'x');
        var consumed = 6;
        if (char.IsHighSurrogate((char)scalar)
            && tail.Length >= 12
            && tail[6] == (byte)'\\'
            && tail[7] == (byte)'u'
            && Utf8Parser.TryParse(tail[8..12], out int low, out _, 'x')
            && char.IsLowSurrogate((char)low))
        {
            scalar = char.ConvertToUtf32((char)scalar, (char)low);
            consumed = 12;
        }

        written = (Rune.TryCreate(scalar, out var rune) ? rune : Rune.ReplacementChar).EncodeToUtf8(decoded);
        return consumed;
    }
}
