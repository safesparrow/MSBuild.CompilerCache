using System.Diagnostics.CodeAnalysis;
using Microsoft.Build.Utilities;

namespace MSBuild.CompilerCache;

using Microsoft.Build.Framework;

public record UseOrPopulateInputs(
    BaseTaskInputs Inputs,
    bool CacheHit,
    string CacheDir,
    string IntermediateOutputPath,
    // TODO Ignored for now
    bool CheckCompileOutputAgainstCache
);

public class UserOrPopulator
{
    public void UseOrPopulate(UseOrPopulateInputs inputs, TaskLoggingHelper log)
    {
        var dir = new DirectoryInfo(inputs.CacheDir);

        var outputsMap =
            inputs.Inputs.OutputsToCache
                .Select(outputPath =>
                {
                    var relativePath = Path.GetRelativePath(inputs.IntermediateOutputPath, outputPath);
                    var cachePath = Path.Combine(inputs.CacheDir, relativePath);
                    return new OutputFileMap(cachePath, outputPath);
                }).ToArray();

        if (inputs.CacheHit && !inputs.CheckCompileOutputAgainstCache)
        {
            log.LogMessage(MessageImportance.High, $"CacheHit - copying {outputsMap.Length} files from cache");
            // TODO Parallel
            foreach (var entry in outputsMap)
            {
                log.LogMessage(MessageImportance.High, $"Copy {entry.CachePath} -> {entry.OutputPath}");
                File.Delete(entry.OutputPath);
                File.Copy(entry.CachePath, entry.OutputPath, true);
                // TODO Set file timestamps - what timestamp to use? It should be compatible with the rest of the MSBuild build process.
            }
        }
        else if (inputs.CacheHit && inputs.CheckCompileOutputAgainstCache)
        {
            log.LogMessage(MessageImportance.High, $"CacheHit but '{nameof(inputs.CheckCompileOutputAgainstCache)}' enabled - verifying newly compiled outputs are same as in the cache.");
            if (!dir.Exists)
            {
                log.LogMessage(MessageImportance.High, $"CacheHit but cache directory does not exist.");
            }
            else
            {
                // TODO Parallel
                foreach (var entry in outputsMap)
                {
                    if (!File.Exists(entry.CachePath))
                        log.LogMessage($"Despite cache hit, cache file does not exist: '{entry.CachePath}'.");
                    else
                    {
                        var outputHash = Utils.FileToSHA256String(new FileInfo(entry.OutputPath));
                        var cachedHash = Utils.FileToSHA256String(new FileInfo(entry.CachePath));
                        if (outputHash != cachedHash)
                        {
                            log.LogMessage(MessageImportance.High, $"Output hash != cache cache. Output ({entry.OutputPath}) = {outputHash}. Cache ({entry.CachePath}) = {cachedHash}");
                        }
                    }
                }

                var markerPath = Helpers.GetMarkerPath(inputs.CacheDir);
                log.LogMessage(MessageImportance.High, $"Creating marker {markerPath}");
                Helpers.CreateEmptyFile(markerPath);
            }
        }
        else
        {
            log.LogMessage(MessageImportance.High, $"CacheMiss - copying {outputsMap.Length} files from output to cache");
            if (!dir.Exists) dir.Create();
            // TODO Parallel
            foreach (var entry in outputsMap)
            {
                if (File.Exists(entry.CachePath))
                    throw new Exception($"Despite cache miss, cache file exists: '{entry.CachePath}'.");
                var fileDir = Path.GetDirectoryName((string?)entry.CachePath)!;
                Directory.CreateDirectory(fileDir);
                log.LogMessage(MessageImportance.High, $"Copy {entry.OutputPath} -> {entry.CachePath}");
                File.Copy(entry.OutputPath, entry.CachePath, true);
            }

            var markerPath = Helpers.GetMarkerPath(inputs.CacheDir);
            log.LogMessage(MessageImportance.High, $"Creating marker {markerPath}");
            Helpers.CreateEmptyFile(markerPath);
        }
    }

    public record OutputFileMap(string CachePath, string OutputPath);
}

// ReSharper disable once UnusedType.Global
/// <summary>
/// Either use results from an existing cache entry, or populate it with newly compiled outputs.
/// Example usage: <UseOrPopulateCache OutputsToCache="@(CompileOutputsToCache)" CacheHit="$(CacheHit)" CacheDir="$(CacheDir)" IntermediateOutputPath="..." />
/// </summary>
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public class UseOrPopulateCache : BaseTask
{
    private readonly UserOrPopulator _userOrPopulator;
#pragma warning disable CS8618
    [Required] public bool CacheHit { get; set; }
    [Required] public string CacheDir { get; set; }
    [Required] public string IntermediateOutputPath { get; set; }
#pragma warning restore CS8618
    public bool CheckCompileOutputAgainstCache { get; set; }

    public UseOrPopulateCache()
    {
        _userOrPopulator = new UserOrPopulator();
    }

    public override bool Execute()
    {
        var inputs = new UseOrPopulateInputs(
            CacheHit: CacheHit,
            CacheDir: CacheDir,
            IntermediateOutputPath: IntermediateOutputPath,
            Inputs: GatherInputs(),
            CheckCompileOutputAgainstCache: CheckCompileOutputAgainstCache
        );
        _userOrPopulator.UseOrPopulate(inputs, Log);
        return true;
    }
}