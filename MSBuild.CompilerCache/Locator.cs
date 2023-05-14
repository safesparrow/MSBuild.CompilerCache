using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;

namespace MSBuild.CompilerCache;

public record LocateResult(
    bool RunCompilation,
    bool CacheSupported,
    bool CacheHit,
    CacheKey CacheKey,
    string LocalInputsHash,
    DateTime PreCompilationTimeUtc
)
{
    public static LocateResult CreateNotSupported()
    {
        return new LocateResult(
            RunCompilation: true,
            CacheSupported: false,
            CacheHit: false,
            CacheKey: null,
            LocalInputsHash: null,
            PreCompilationTimeUtc: DateTime.MinValue
        );
    }
}

public class Locator
{
    private FullExtract _extract;
    private string _hashString;
    private BaseTaskInputs _inputs;

    public LocateResult Locate(BaseTaskInputs inputs, TaskLoggingHelper? log = null)
    {
        var decomposed = TargetsExtractionUtils.DecomposeCompilerProps(inputs.AllProps, log);
        if (decomposed.UnsupportedPropsSet.Any())
        {
            var s = string.Join(Environment.NewLine, decomposed.UnsupportedPropsSet.Select(nv => $"{nv.Name}={nv.Value}"));
            log?.LogMessage(MessageImportance.High, $"Some unsupported props set: {Environment.NewLine}{s}");
            return LocateResult.CreateNotSupported();
        }
        var configJson = File.ReadAllText(inputs.ConfigPath);
        var config = JsonConvert.DeserializeObject<Config>(configJson);
        var baseCacheDir = config.BaseCacheDir;
        var cache = new Cache(baseCacheDir);
        var localInputs = CalculateLocalInputs(decomposed);
        var extract = localInputs.ToFullExtract();
        _inputs = inputs;
        _extract = extract;
        var hashString = Utils.ObjectToSHA256Hex(extract);
        _hashString = hashString; 
        var cacheKey = UserOrPopulator.GenerateKey(inputs, hashString);
        var localInputsHash = Utils.ObjectToSHA256Hex(localInputs);

        var cacheHit = cache.Exists(cacheKey);
        if (!cacheHit)
        {
            log?.LogMessage(MessageImportance.High, $"Locate for {cacheKey} was a miss.");
        }
        else
        {
            log?.LogMessage(MessageImportance.High, $"Locate for {cacheKey} was a hit.");
            cacheHit = true;
        }

        var runCompilation = !cacheHit || config.CheckCompileOutputAgainstCache;
        
        return new LocateResult(
            CacheSupported: true,
            CacheHit: cacheHit,
            RunCompilation: runCompilation,
            CacheKey: cacheKey,
            LocalInputsHash: localInputsHash,
            PreCompilationTimeUtc: DateTime.UtcNow
        );
    }

    public static LocalInputs CalculateLocalInputs(DecomposedCompilerProps decomposed)
    {
        var allFileInputs = decomposed.FileInputs.Union(decomposed.References);
        var fileExtracts = allFileInputs.OrderBy(file => file).AsParallel().AsOrdered().Select(GetLocalFileExtract)
            .ToArray();

        return new LocalInputs(fileExtracts, decomposed.PropertyInputs.Select(kvp => (kvp.Key, kvp.Value)).OrderBy(kvp => kvp.Key).ToArray(), decomposed.OutputsToCache.OrderBy(x => x.Name).ToArray());
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