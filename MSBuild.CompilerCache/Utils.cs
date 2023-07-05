using System.Collections.Immutable;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using FastHashes;

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

public interface IHash
{
    byte[] ComputeHash(ReadOnlySpan<byte> data);

    byte[] ComputeHash(byte[] data) => ComputeHash(data.AsSpan());
}

public enum HasherType
{
    SHA256,
    XxHash64
}

public class SHA256Adapter : IHash
{
    private readonly SHA256 _hash = SHA256.Create();

    public byte[] ComputeHash(ReadOnlySpan<byte> data) => _hash.ComputeHash(data.ToArray());

    public byte[] ComputeHash(byte[] data) => _hash.ComputeHash(data);
}

public class FastHashAdapter : IHash
{
    private readonly Hash _hash;

    public FastHashAdapter(Hash hash)
    {
        _hash = hash;
    }
    
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        return _hash.ComputeHash(data);
    }
}

public static class HasherFactory
{
    public static IHash CreateHash(HasherType type) =>
        type switch
        {
            HasherType.SHA256 => new SHA256Adapter(),
            HasherType.XxHash64 => new FastHashAdapter(new XxHash64(0)),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
}

public static class Utils
{
    private static readonly IHash DefaultHasher = new FastHashAdapter(new XxHash64(0));
    
    public static string ObjectToSHA256Hex(object item, IHash hasher = null)
    {
        using var ms = new MemoryStream();
#pragma warning disable SYSLIB0011
        var binaryFormatter = new BinaryFormatter();
        binaryFormatter.Serialize(ms, item);
        ms.Position = 0;
#pragma warning restore SYSLIB0011

        hasher = hasher ?? DefaultHasher;
        var hash = hasher.ComputeHash(ms.ToArray());
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