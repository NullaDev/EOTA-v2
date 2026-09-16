namespace Eota.Server.Infrastructure;

internal static class AtomicFile
{
    public static void Write(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(flushToDisk: true); }
        File.Move(temporary, path, overwrite: true);
    }
}
