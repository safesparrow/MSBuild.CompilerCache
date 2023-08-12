using FastHashes;

namespace MSBuild.CompilerCache;

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