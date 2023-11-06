namespace MSBuild.CompilerCache;

/// <summary>
/// File metadata used as a key for the <see cref="FileHashCache"/>.
/// If two input files' full paths, length & LastWriteTimeUtc are the same, we assume their contents are the same too. 
/// </summary>
[Serializable]
public record struct FileHashCacheKey
{
    public FileHashCacheKey(string FullName, long Length, DateTime LastWriteTimeUtc)
    {
        this.FullName = FullName ?? throw new Exception("Null FullName");
        this.Length = Length;
        this.LastWriteTimeUtc = LastWriteTimeUtc;
    }

    public static FileHashCacheKey FromFileInfo(FileInfo file) => new FileHashCacheKey(FullName: file.FullName, Length: file.Length,
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
