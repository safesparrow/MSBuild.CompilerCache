namespace MSBuild.CompilerCache;

public class DisposableDir : IDisposable
{
    public DisposableDir()
    {
        // TODO Use Guid-like path instead?
        var tempFile = Path.GetTempFileName();
        File.Delete(tempFile);
        Dir = Directory.CreateDirectory(tempFile);
    }

    public DirectoryInfo Dir { get; set; }

    public string FullName => Dir.FullName;

    public static implicit operator DirectoryInfo(DisposableDir dir) => dir.Dir;

    public void Dispose()
    {
        Dir.Delete(recursive: true);
    }
}