namespace MSBuild.CompilerCache;

public static class Helpers
{
    public static string GetMarkerPath(string cacheDir) => Path.Combine(cacheDir, "marker.data");

    public static void CreateEmptyFile(string markerPath)
    {
        // ReSharper disable once EmptyEmbeddedStatement
        using (File.Create(markerPath)) ;
    }
}