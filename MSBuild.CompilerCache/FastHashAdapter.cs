using FastHashes;

namespace MSBuild.CompilerCache;

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