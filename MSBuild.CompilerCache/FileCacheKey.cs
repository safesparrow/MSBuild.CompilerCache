namespace MSBuild.CompilerCache;

[Serializable]
public record struct FileCacheKey
{
    public FileCacheKey(string FullName, long Length, DateTime LastWriteTimeUtc)
    {
        this.FullName = FullName ?? throw new Exception("Null FullName");
        this.Length = Length;
        this.LastWriteTimeUtc = LastWriteTimeUtc;
    }

    public static FileCacheKey FromFileInfo(FileInfo file) => new FileCacheKey(FullName: file.FullName, Length: file.Length,
        LastWriteTimeUtc: file.LastWriteTimeUtc);

    public string FullName { get; set; }
    public long Length { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }

    public readonly void Deconstruct(out string FullName, out long Length, out DateTime LastWriteTimeUtc)
    {
        FullName = this.FullName;
        Length = this.Length;
        LastWriteTimeUtc = this.LastWriteTimeUtc;
    }
}
