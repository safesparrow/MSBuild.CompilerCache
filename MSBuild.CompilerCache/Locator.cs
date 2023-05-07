using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;

namespace MSBuild.CompilerCache;

public record LocateResult(
    bool CacheHit,
    CacheKey CacheKey,
    string LocalInputsHash,
    DateTime PreCompilationTimeUtc
);

public class Locator
{
    public LocateResult Locate(BaseTaskInputs inputs, TaskLoggingHelper log)
    {
        var baseCacheDir = inputs.BaseCacheDir;
        var cache = new Cache(baseCacheDir);
        var localInputs = CalculateLocalInputs(inputs);
        var extract = localInputs.ToFullExtract();
        var hashString = Utils.ObjectToSHA256Hex(extract);
        var cacheKey = UserOrPopulator.GenerateKey(inputs, hashString);
        var localInputsHash = Utils.ObjectToSHA256Hex(localInputs);

        var cacheHit = cache.Exists(cacheKey);
        if (!cacheHit)
        {
            log.LogMessage(MessageImportance.High, $"Locate for {cacheKey} was a miss.");
        }
        else
        {
            log.LogMessage(MessageImportance.High, $"Locate for {cacheKey} was a hit.");
            cacheHit = true;
        }

        return new LocateResult(
            CacheHit: cacheHit,
            CacheKey: cacheKey,
            LocalInputsHash: localInputsHash,
            PreCompilationTimeUtc: DateTime.UtcNow
        );
    }

    public static LocalInputs CalculateLocalInputs(BaseTaskInputs inputs)
    {
        var allFileInputs = inputs.FileInputs.Union(inputs.References);
        var fileExtracts = allFileInputs.OrderBy(file => file).AsParallel().AsOrdered().Select(GetLocalFileExtract)
            .ToArray();

        return new LocalInputs(fileExtracts, inputs.PropertyInputs, inputs.OutputsToCache);
    }

    public static LocalFileExtract GetLocalFileExtract(string filepath)
    {
        var fileInfo = new FileInfo(filepath);
        if (!fileInfo.Exists)
        {
            throw new Exception($"File does not exist: '{filepath}'");
        }

        var hashString = Utils.FileToSHA256String(fileInfo);
        return new LocalFileExtract(fileInfo.FullName, hashString, fileInfo.Length, fileInfo.LastWriteTimeUtc);
    }

    public static CompilationMetadata GetCompilationMetadata(DateTime postCompilationTimeUtc) =>
        new(
            Hostname: Environment.MachineName,
            Username: Environment.UserName,
            StopTimeUtc: postCompilationTimeUtc,
            WorkingDirectory: Environment.CurrentDirectory
        );
}