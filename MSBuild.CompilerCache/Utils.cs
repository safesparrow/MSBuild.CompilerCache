using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;

namespace MSBuild.CompilerCache;

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
    
    public static string ObjectToSHA256Hex(byte[] bytes)
    {
        var hash = SHA256.Create().ComputeHash(bytes);
        var hashString = Convert.ToHexString(hash);
        return hashString;
    }

    public static string FileToSHA256String(FileInfo fileInfo)
    {
        using var hash = SHA256.Create();
        using var f = fileInfo.OpenRead();
        var bytes = hash.ComputeHash(f);
        return Convert.ToHexString(bytes);
    }
}