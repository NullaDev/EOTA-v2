using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Eota.Kernel.Determinism;

public sealed class CanonicalWriter
{
    private static readonly byte[] Magic = "EOTA"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public CanonicalWriter(string domain, ushort schemaVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        WriteRaw(Magic);
        WriteString(domain);
        WriteUInt16(schemaVersion);
    }

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.WrittenMemory;

    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteByte(byte value)
    {
        var destination = _buffer.GetSpan(1);
        destination[0] = value;
        _buffer.Advance(1);
    }

    public void WriteUInt16(ushort value)
    {
        var destination = _buffer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(destination, value);
        _buffer.Advance(sizeof(ushort));
    }

    public void WriteInt32(int value)
    {
        var destination = _buffer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(destination, value);
        _buffer.Advance(sizeof(int));
    }

    public void WriteUInt32(uint value)
    {
        var destination = _buffer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        _buffer.Advance(sizeof(uint));
    }

    public void WriteInt64(long value)
    {
        var destination = _buffer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(destination, value);
        _buffer.Advance(sizeof(long));
    }

    public void WriteUInt64(ulong value)
    {
        var destination = _buffer.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(destination, value);
        _buffer.Advance(sizeof(ulong));
    }

    public void WriteCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        WriteUInt32((uint)count);
    }

    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.Normalize(NormalizationForm.FormC);
        var byteCount = StrictUtf8.GetByteCount(normalized);
        WriteUInt32((uint)byteCount);
        var destination = _buffer.GetSpan(byteCount);
        StrictUtf8.GetBytes(normalized, destination);
        _buffer.Advance(byteCount);
    }

    public void WriteHash(Hash256 hash)
    {
        var destination = _buffer.GetSpan(Hash256.ByteLength);
        hash.WriteBytes(destination);
        _buffer.Advance(Hash256.ByteLength);
    }

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    private void WriteRaw(ReadOnlySpan<byte> value)
    {
        value.CopyTo(_buffer.GetSpan(value.Length));
        _buffer.Advance(value.Length);
    }
}
