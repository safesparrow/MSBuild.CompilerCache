using System.Security.Cryptography;

namespace MSBuild.CompilerCache;

public class SHA256Adapter : IHash
{
    private readonly SHA256 _hash = SHA256.Create();

    public byte[] ComputeHash(ReadOnlySpan<byte> data) => _hash.ComputeHash(data.ToArray());

    public byte[] ComputeHash(byte[] data) => _hash.ComputeHash(data);
}