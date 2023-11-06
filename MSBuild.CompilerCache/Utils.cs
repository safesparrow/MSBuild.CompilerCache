using System.Runtime.Serialization.Formatters.Binary;

namespace MSBuild.CompilerCache;

public sealed class TempFile : IDisposable
{
    public static string GetTempFilePath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    public FileInfo File { get; } = new FileInfo(GetTempFilePath());
    public string FullName => File.FullName;

    public void Dispose() => File.Delete();
}

public record RelativePath(string Path)
{
    public AbsolutePath ToAbsolute(AbsolutePath relativeTo) =>
        System.IO.Path.Combine(relativeTo, Path).AsAbsolutePath();

    public static implicit operator string(RelativePath path) => path.Path;
}

public record AbsolutePath(string Path)
{
    public static implicit operator string(AbsolutePath path) => path.Path;
}

public static class StringExtensions
{
    public static AbsolutePath AsAbsolutePath(this string x) => new AbsolutePath(x);
    public static RelativePath AsRelativePath(this string x) => new RelativePath(x);
}

public static class Utils
{
    internal static readonly IHash DefaultHasher = HasherFactory.CreateHash(HasherType.XxHash64);
    
    public static string ObjectToHash(object item, IHash hasher)
    {
        var bytes = ObjectToBytes(item);
        return BytesToHash(bytes, hasher);
    }

    public static byte[] ObjectToBytes(object item)
    {
        using var ms = new MemoryStream();
#pragma warning disable SYSLIB0011
        var binaryFormatter = new BinaryFormatter();
        binaryFormatter.Serialize(ms, item);
        ms.Position = 0;
#pragma warning restore SYSLIB0011
        var bytes = ms.ToArray();
        return bytes;
    }

    public static string FileBytesToHash(string path, IHash hasher)
    {
        var bytes = File.ReadAllBytes(path);
        return BytesToHash(bytes, hasher);
    }

    public static string BytesToHash(ReadOnlySpan<byte> bytes, IHash hasher)
    {
        var hash = hasher.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }
}