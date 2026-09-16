using System.Buffers.Binary;

namespace Eota.Kernel.Determinism;

public readonly record struct Hash256(ulong A, ulong B, ulong C, ulong D)
{
    public const int ByteLength = 32;

    public static Hash256 FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength)
        {
            throw new ArgumentException("A SHA-256 digest must contain exactly 32 bytes.", nameof(bytes));
        }

        return new Hash256(
            BinaryPrimitives.ReadUInt64BigEndian(bytes),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[24..]));
    }

    public static Hash256 Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != ByteLength * 2)
        {
            throw new FormatException("A SHA-256 hexadecimal digest must contain exactly 64 characters.");
        }

        return FromBytes(Convert.FromHexString(value));
    }

    public void WriteBytes(Span<byte> destination)
    {
        if (destination.Length < ByteLength)
        {
            throw new ArgumentException("The destination must contain at least 32 bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, A);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], B);
        BinaryPrimitives.WriteUInt64BigEndian(destination[16..], C);
        BinaryPrimitives.WriteUInt64BigEndian(destination[24..], D);
    }

    public byte[] ToArray()
    {
        var bytes = new byte[ByteLength];
        WriteBytes(bytes);
        return bytes;
    }

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        WriteBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
