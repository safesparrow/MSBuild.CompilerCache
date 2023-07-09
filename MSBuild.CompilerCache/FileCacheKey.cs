namespace MSBuild.CompilerCache;

[Serializable]
public record struct FileCacheKey(string FullName, long Length, DateTime LastWriteTimeUtc)
{
    public static FileCacheKey FromFileInfo(FileInfo file) => new FileCacheKey(FullName: file.FullName, Length: file.Length,
        LastWriteTimeUtc: file.LastWriteTimeUtc);
}
