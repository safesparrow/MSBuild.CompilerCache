namespace MSBuild.CompilerCache;

public sealed class TempFile : IDisposable
{
    public static string GetTempFilePath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    public FileInfo File { get; } = new FileInfo(GetTempFilePath());
    public string FullName => File.FullName;

    public void Dispose() => File.Delete();
}