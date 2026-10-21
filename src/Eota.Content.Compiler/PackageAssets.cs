using System.Security.Cryptography;

namespace Eota.Content.Compiler;

/// <summary>Portable package resources. Hashes cover file bytes; paths cannot escape the package directory.</summary>
public static class PackageAssets
{
    public const string Placeholder = "ui://card-placeholder";
    public static bool IsPath(string path) => path.Length is > 0 and <= 240 && !path.Contains('\\')
        && path.Split('/').All(part => part.Length > 0 && part is not "." and not ".."
            && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        && path.EndsWith(".png", StringComparison.Ordinal);

    public static (string PackageId, string Path) Split(string reference)
    {
        if (reference.StartsWith("pack://", StringComparison.Ordinal))
        {
            var parts = reference[7..].Split('/', 2);
            if (parts.Length == 2 && ContentPackages.IsId(parts[0]) && IsPath(parts[1])) { return (parts[0], parts[1]); }
        }
        throw new InvalidDataException("插图必须使用 pack://包ID/相对路径.png：" + reference);
    }

    public static string FilePath(ContentPackage pack, string relative)
    {
        if (pack.AssetDirectory is null || !IsPath(relative)) { throw new InvalidDataException("缺少内容包资源目录：" + pack.Id); }
        var root = Path.GetFullPath(pack.AssetDirectory);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("资源路径越界。"); }
        return path;
    }

    public static void ValidateFiles(ContentPackage pack)
    {
        long total = 0;
        Span<byte> signature = stackalloc byte[8];
        foreach (var (relative, hash) in pack.Assets)
        {
            var path = FilePath(pack, relative);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 16 * 1024 * 1024 || (total += info.Length) > 256 * 1024 * 1024)
            { throw new InvalidDataException($"内容包 {pack.Id} 的资源不存在或超过大小限制：{relative}"); }
            using var stream = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), hash, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidDataException($"内容包 {pack.Id} 的资源 hash 校验失败：{relative}"); }
            stream.Position = 0;
            if (stream.Read(signature) != 8 || !signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            { throw new InvalidDataException($"内容包资源不是 PNG：{relative}"); }
        }
    }

    public static void CopyFiles(ContentPackage pack, string destination)
    {
        ValidateFiles(pack);
        var target = pack with { AssetDirectory = Path.GetDirectoryName(Path.GetFullPath(destination)) };
        foreach (var relative in pack.Assets.Keys)
        {
            var source = FilePath(pack, relative); var path = FilePath(target, relative);
            if (string.Equals(source, path, StringComparison.OrdinalIgnoreCase)) { continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.Copy(source, path, overwrite: true);
        }
    }
}
