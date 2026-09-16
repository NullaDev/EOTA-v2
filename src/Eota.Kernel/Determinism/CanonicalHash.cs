using System.Security.Cryptography;

namespace Eota.Kernel.Determinism;

public static class CanonicalHash
{
    public static Hash256 Compute(string domain, ushort schemaVersion, Action<CanonicalWriter> writePayload)
    {
        ArgumentNullException.ThrowIfNull(writePayload);
        var writer = new CanonicalWriter(domain, schemaVersion);
        writePayload(writer);
        Span<byte> digest = stackalloc byte[Hash256.ByteLength];
        SHA256.HashData(writer.WrittenMemory.Span, digest);
        return Hash256.FromBytes(digest);
    }
}
