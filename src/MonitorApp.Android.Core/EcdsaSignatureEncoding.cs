using System.Formats.Asn1;

namespace MonitorApp.Android.Core;

public static class EcdsaSignatureEncoding
{
    public static byte[] DerToP1363P256(ReadOnlyMemory<byte> derSignature)
    {
        var reader = new AsnReader(derSignature, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        var r = sequence.ReadIntegerBytes().Span;
        var s = sequence.ReadIntegerBytes().Span;
        sequence.ThrowIfNotEmpty();
        reader.ThrowIfNotEmpty();

        var result = new byte[64];
        WriteUnsignedFixed(r, result.AsSpan(0, 32));
        WriteUnsignedFixed(s, result.AsSpan(32, 32));
        return result;
    }

    private static void WriteUnsignedFixed(ReadOnlySpan<byte> encoded, Span<byte> destination)
    {
        if (encoded.IsEmpty || (encoded[0] & 0x80) != 0)
        {
            throw new ArgumentException("ECDSA integer must be a nonnegative DER integer.");
        }

        while (encoded.Length > 1 && encoded[0] == 0)
        {
            encoded = encoded[1..];
        }

        if (encoded.Length > destination.Length)
        {
            throw new ArgumentException("ECDSA integer exceeds the P-256 field width.");
        }

        encoded.CopyTo(destination[^encoded.Length..]);
    }
}
