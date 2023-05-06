namespace MSBuild.CompilerCache;

public static class DirectoryInfoExtensions
{
    public static string Combine(this DirectoryInfo dir, string suffix) => Path.Combine(dir.FullName, suffix);

    public static FileInfo CombineAsFile(this DirectoryInfo dir, string suffix) =>
        new FileInfo(Path.Combine(dir.FullName, suffix));
    
    public static DirectoryInfo CombineAsDir(this DirectoryInfo dir, string suffix) =>
        new DirectoryInfo(Path.Combine(dir.FullName, suffix));
}