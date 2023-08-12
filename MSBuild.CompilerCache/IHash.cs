namespace MSBuild.CompilerCache;

public interface IHash
{
    byte[] ComputeHash(ReadOnlySpan<byte> data);

    byte[] ComputeHash(byte[] data) => ComputeHash(data.AsSpan());
}