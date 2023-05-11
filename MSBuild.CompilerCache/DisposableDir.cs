namespace MSBuild.CompilerCache;

public class DisposableDir : IDisposable
{
    public DisposableDir()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Dir = Directory.CreateDirectory(path);
    }

    public DirectoryInfo Dir { get; set; }

    public string FullName => Dir.FullName;

    public static implicit operator DirectoryInfo(DisposableDir dir) => dir.Dir;

    public void Dispose()
    {
        Dir.Delete(recursive: true);
    }
}