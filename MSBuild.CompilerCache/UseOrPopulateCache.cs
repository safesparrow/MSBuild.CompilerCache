namespace MSBuild.CompilerCache;

using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

// ReSharper disable once UnusedType.Global
/// <summary>
/// Either use results from an existing cache entry, or populate it with newly compiled outputs.
/// Example usage: <UseOrPopulateCache OutputsToCache="@(CompileOutputsToCache)" CacheHit="$(CacheHit)" CacheDir="$(CacheDir)" IntermediateOutputPath="..." />
/// </summary>
public class UseOrPopulateCache : Task
{
#pragma warning disable CS8618
    [Required] public ITaskItem[] OutputsToCache { get; set; }
    [Required] public bool CacheHit { get; set; }
    [Required] public string CacheDir { get; set; }
    [Required] public string IntermediateOutputPath { get; set; }
#pragma warning restore CS8618
    // TODO Ignored for now
    public bool CheckCompileOutputAgainstCache { get; set; }

    public record OutputFileMap(string CachePath, string OutputPath);

    public override bool Execute()
    {
        var dir = new DirectoryInfo(CacheDir);

        var outputsMap =
            OutputsToCache
                .Select(o =>
                {
                    var outputPath = o.ItemSpec!;
                    var relativePath = Path.GetRelativePath(IntermediateOutputPath, outputPath);
                    var cachePath = Path.Combine(CacheDir, relativePath);
                    return new OutputFileMap(cachePath, outputPath);
                }).ToArray();

        if (CacheHit && !CheckCompileOutputAgainstCache)
        {
            Log.LogMessage(MessageImportance.High, $"CacheHit - copying {outputsMap.Length} files from cache");
            // TODO Parallel
            foreach (var entry in outputsMap)
            {
                Log.LogMessage(MessageImportance.High, $"Copy {entry.CachePath} -> {entry.OutputPath}");
                File.Delete(entry.OutputPath);
                File.Copy(entry.CachePath, entry.OutputPath, true);
                // TODO Set file timestamps - what timestamp to use? It should be compatible with the rest of the MSBuild build process.
            }
        }
        else if (CacheHit && CheckCompileOutputAgainstCache)
        {
            Log.LogMessage(MessageImportance.High, $"CacheHit but '{nameof(CheckCompileOutputAgainstCache)}' enabled - verifying newly compiled outputs are same as in the cache.");
            if (!dir.Exists)
            {
                Log.LogMessage(MessageImportance.High, $"CacheHit but cache directory does not exist.");
            }
            else
            {
                // TODO Parallel
                foreach (var entry in outputsMap)
                {
                    if (!File.Exists(entry.CachePath))
                        Log.LogMessage($"Despite cache hit, cache file does not exist: '{entry.CachePath}'.");
                    else
                    {
                        var outputHash = LocateCompilationCacheEntry.FileToSHA1String(new FileInfo(entry.OutputPath));
                        var cachedHash = LocateCompilationCacheEntry.FileToSHA1String(new FileInfo(entry.CachePath));
                        if (outputHash != cachedHash)
                        {
                            Log.LogMessage(MessageImportance.High, $"Output hash != cache cache. Output ({entry.OutputPath}) = {outputHash}. Cache ({entry.CachePath}) = {cachedHash}");
                        }
                    }
                }

                var markerPath = Helpers.GetMarkerPath(CacheDir);
                Log.LogMessage(MessageImportance.High, $"Creating marker {markerPath}");
                Helpers.CreateEmptyFile(markerPath);
            }
        }
        else
        {
            Log.LogMessage(MessageImportance.High, $"CacheMiss - copying {outputsMap.Length} files from output to cache");
            if (!dir.Exists) dir.Create();
            // TODO Parallel
            foreach (var entry in outputsMap)
            {
                if (File.Exists(entry.CachePath))
                    throw new Exception($"Despite cache miss, cache file exists: '{entry.CachePath}'.");
                var fileDir = Path.GetDirectoryName(entry.CachePath)!;
                Directory.CreateDirectory(fileDir);
                Log.LogMessage(MessageImportance.High, $"Copy {entry.OutputPath} -> {entry.CachePath}");
                File.Copy(entry.OutputPath, entry.CachePath, true);
            }

            var markerPath = Helpers.GetMarkerPath(CacheDir);
            Log.LogMessage(MessageImportance.High, $"Creating marker {markerPath}");
            Helpers.CreateEmptyFile(markerPath);
        }

        return true;
    }
}