using System.Runtime.Serialization.Formatters.Binary;

namespace MSBuild.CompilerCache;

public static class Utils
{
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
    
    public static string BytesToHash(ReadOnlySpan<byte> bytes, IHash hasher)
    {
        var hash = hasher.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }
}