using System.Collections.Immutable;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;

namespace MSBuild.CompilerCache;

public class TempFile : IDisposable
{
    public FileInfo File { get; }
    public string FullName => File.FullName;

    public TempFile()
    {
        File = new FileInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
    }

    public void Dispose()
    {
        File.Delete();
    }
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
    public static string ObjectToSHA256Hex(object item)
    {
        using var ms = new MemoryStream();
#pragma warning disable SYSLIB0011
        var binaryFormatter = new BinaryFormatter();
        binaryFormatter.Serialize(ms, item);
        ms.Position = 0;
#pragma warning restore SYSLIB0011
        var hash = SHA256.Create().ComputeHash(ms);
        var hashString = Convert.ToHexString(hash);
        return hashString;
    }

    public static string BytesToSHA256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
    
    public static string BytesToSHA256Hex(ImmutableArray<byte> bytes)
    {
        var hash = SHA256.HashData(bytes.AsSpan());
        return Convert.ToHexString(hash);
    }

    public static string FileToSHA256String(FileInfo fileInfo)
    {
        using var hash = SHA256.Create();
        using var f = fileInfo.OpenRead();
        var bytes = hash.ComputeHash(f);
        return Convert.ToHexString(bytes);
    }
}