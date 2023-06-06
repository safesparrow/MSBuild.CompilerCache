using System.Collections.Immutable;
using System.IO.Compression;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;

namespace MSBuild.CompilerCache;

public record UseOrPopulateResult;

public record LocateInputs(
    string ConfigPath,
    string ProjectFullPath,
    string AssemblyName,
    IDictionary<string, string> AllProps
);

public enum LocateOutcome
{
    CacheNotSupported,
    OnlyPopulateCache,
    CacheUsed,
    CacheMiss
}

public record LocateResult(
    LocateOutcome Outcome,
    CacheKey? CacheKey,
    string? LocalInputsHash,
    DateTime PreCompilationTimeUtc,
    LocateInputs Inputs,
    DecomposedCompilerProps? DecomposedCompilerProps
)
{
    public bool RunCompilation => Outcome != LocateOutcome.CacheUsed;
    public bool CacheSupported => Outcome != LocateOutcome.CacheNotSupported;
    public bool CacheHit => Outcome == LocateOutcome.CacheUsed;
    public bool PopulateCache => RunCompilation && CacheSupported;

    public static LocateResult CreateNotSupported(LocateInputs inputs) =>
        new(
            Outcome: LocateOutcome.CacheNotSupported,
            CacheKey: null,
            LocalInputsHash: null,
            PreCompilationTimeUtc: DateTime.MinValue,
            Inputs: inputs,
            DecomposedCompilerProps: null
        );
}

public class LocatorAndPopulator
{
    public static (Config config, ICache Cache, IRefCache RefCache) CreateCaches(string configPath)
    {
        var configJson = File.ReadAllText(configPath);
        var config = JsonConvert.DeserializeObject<Config>(configJson);
        var baseCacheDir = config.CacheDir;
        var cache = new Cache(baseCacheDir);
        var refCache = new RefCache(config.InferRefCacheDir());
        return (config, cache, refCache);
    }

    private IRefCache _refCache;
    private ICache _cache;
    private Config _config;
    private DecomposedCompilerProps _decomposed;
    private string _assemblyName;
    private LocateResult _locateResult;
    private FullExtract _extract;
    private CacheKey _cacheKey;
    private LocalInputs _localInputs;
    private string _localInputsHash;

    public LocateResult Locate(LocateInputs inputs, TaskLoggingHelper? log = null)
    {
        var preCompilationTimeUtc = DateTime.UtcNow;

        _decomposed = TargetsExtractionUtils.DecomposeCompilerProps(inputs.AllProps, log);
        if (_decomposed.UnsupportedPropsSet.Any())
        {
            var s = string.Join(Environment.NewLine,
                _decomposed.UnsupportedPropsSet.Select(nv => $"{nv.Name}={nv.Value}"));
            log?.LogMessage(MessageImportance.Normal,
                $"CompilationCache: Some unsupported MSBuild properties set - the cache will be disabled: {Environment.NewLine}{s}");
            var notSupported = LocateResult.CreateNotSupported(inputs);
            _locateResult = notSupported;
            return _locateResult;
        }

        (_config, _cache, _refCache) = CreateCaches(inputs.ConfigPath);
        _assemblyName = inputs.AssemblyName;

        (_localInputs, _localInputsHash) = CalculateLocalInputsWithHash();
        _extract = _localInputs.ToFullExtract();
        var hashString = Utils.ObjectToSHA256Hex(_extract);
        _cacheKey = GenerateKey(inputs, hashString);

        LocateOutcome outcome;

        if (_config.CheckCompileOutputAgainstCache)
        {
            outcome = LocateOutcome.OnlyPopulateCache;
            log?.LogMessage(MessageImportance.Normal,
                $"CompilationCache: CheckCompileOutputAgainstCache is set - not using cached outputs.");
        }
        else
        {
            var cachedOutputTmpZip = _cache.Get(_cacheKey);
            if (cachedOutputTmpZip != null)
            {
                try
                {
                    outcome = LocateOutcome.CacheUsed;
                    var outputs = _decomposed.OutputsToCache;
                    log?.LogMessage(MessageImportance.Normal,
                        $"CompilationCache: Locate for {_cacheKey} was a hit. Copying {outputs.Length} files from cache");

                    UseCachedOutputs(cachedOutputTmpZip!, outputs, preCompilationTimeUtc);
                }
                finally
                {
                    File.Delete(cachedOutputTmpZip!);
                }
            }
            else
            {
                outcome = LocateOutcome.CacheMiss;
                log?.LogMessage(MessageImportance.Normal, $"CompilationCache: Locate for {_cacheKey} was a miss.");
            }
        }

        _locateResult = new LocateResult(
            CacheKey: _cacheKey,
            LocalInputsHash: _localInputsHash,
            PreCompilationTimeUtc: preCompilationTimeUtc,
            Inputs: inputs,
            Outcome: outcome,
            DecomposedCompilerProps: _decomposed
        );
        return _locateResult;
    }

    private (LocalInputs, string) CalculateLocalInputsWithHash()
    {
        var localInputs = CalculateLocalInputs();
        var localInputsHash = Utils.ObjectToSHA256Hex(localInputs);
        return (localInputs, localInputsHash);
    }

    private LocalInputs CalculateLocalInputs() =>
        CalculateLocalInputs(_decomposed, _refCache, _assemblyName, _config.RefTrimming);

    public static LocalInputs CalculateLocalInputs(DecomposedCompilerProps decomposed, IRefCache refCache,
        string assemblyName, RefTrimmingConfig trimmingConfig)
    {
        var nonReferenceFileInputs = decomposed.FileInputs;
        var fileInputs = trimmingConfig.Enabled
            ? nonReferenceFileInputs
            : nonReferenceFileInputs.Union(decomposed.References);
        var fileExtracts =
            fileInputs
                .OrderBy(file => file)
                .AsParallel()
                .AsOrdered()
                .Select(GetLocalFileExtract)
                .ToImmutableArray();

        var refExtracts = trimmingConfig.Enabled
            ? decomposed.References
                .OrderBy(dll => dll)
                .AsParallel()
                .AsOrdered()
                .Select(dll =>
                {
                    var allRefData = GetAllRefData(dll, refCache);
                    return AllRefDataToExtract(allRefData, assemblyName, trimmingConfig.IgnoreInternalsIfPossible);
                })
                .ToImmutableArray()
            : ImmutableArray.Create<LocalFileExtract>();

        var allExtracts = fileExtracts.Union(refExtracts).ToArray();

        var props = decomposed.PropertyInputs.Select(kvp => (kvp.Key, kvp.Value)).OrderBy(kvp => kvp.Key).ToArray();
        var outputs = decomposed.OutputsToCache.OrderBy(x => x.Name).ToArray();

        return new LocalInputs(allExtracts, props, outputs);
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

    public static AllRefData GetAllRefData(string filepath, IRefCache refCache)
    {
        var fileInfo = new FileInfo(filepath);
        if (!fileInfo.Exists)
        {
            throw new Exception($"Reference file does not exist: '{filepath}'");
        }

        var bytes = ImmutableArray.Create(File.ReadAllBytes(filepath));
        var hashString = Utils.BytesToSHA256Hex(bytes);
        var extract = new LocalFileExtract(fileInfo.FullName, hashString, fileInfo.Length, fileInfo.LastWriteTimeUtc);
        var name = Path.GetFileNameWithoutExtension(fileInfo.Name);

        var cacheKey = BuildRefCacheKey(name, hashString);
        var cached = refCache.Get(cacheKey);
        if (cached == null)
        {
            var trimmer = new RefTrimmer();
            var toBeCached = trimmer.GenerateRefData(bytes);
            var x = new RefDataWithOriginalExtract(Ref: toBeCached, Original: extract);
            refCache.Set(cacheKey, x);
            cached = x;
        }

        return new AllRefData(
            Original: extract,
            InternalsVisibleToAssemblies: cached.Ref.InternalsVisibleTo,
            PublicRefHash: cached.Ref.PublicRefHash,
            PublicAndInternalsRefHash: cached.Ref.PublicAndInternalRefHash
        );
    }

    public static LocalFileExtract AllRefDataToExtract(AllRefData data, string assemblyName,
        bool ignoreInternalsIfPossible)
    {
        bool internalsVisibleToOurAssembly =
            data.InternalsVisibleToAssemblies.Any(x =>
                string.Equals(x, assemblyName, StringComparison.OrdinalIgnoreCase));

        var ignoreInternals = ignoreInternalsIfPossible || !internalsVisibleToOurAssembly;
        var trimmedHash =
            ignoreInternals ? data.PublicAndInternalsRefHash : data.PublicRefHash;

        // TODO It's not very clean that we keep other properties of the original dll and only modify the hash, but it's enough for caching to work.
        return data.Original with { Hash = trimmedHash };
    }

    public static CacheKey BuildRefCacheKey(string name, string hashString) => new($"{name}_{hashString}");

    public static void UseCachedOutputs(string localTmpOutputsZipPath, OutputItem[] items,
        DateTime postCompilationTimeUtc)
    {
        using var a = ZipFile.OpenRead(localTmpOutputsZipPath);
        foreach (var entry in a.Entries.Where(e => !e.Name.StartsWith("__")))
        {
            var outputItem =
                items.SingleOrDefault(it => it.CacheFileName == entry.Name)
                ?? throw new Exception($"Cached outputs contain an unknown file '{entry.Name}'.");
            entry.ExtractToFile(outputItem.LocalPath, overwrite: true);
            File.SetLastWriteTimeUtc(outputItem.LocalPath, postCompilationTimeUtc);
        }
    }

    public static CacheKey GenerateKey(LocateInputs inputs, string hash)
    {
        var name = Path.GetFileName(inputs.ProjectFullPath);
        return new CacheKey($"{name}_{hash}");
    }

    public UseOrPopulateResult UseOrPopulate(TaskLoggingHelper log)
    {
        var postCompilationTimeUtc = DateTime.UtcNow;
        var (_, localInputsHash) = CalculateLocalInputsWithHash();
        var hashesMatch = _locateResult.LocalInputsHash == localInputsHash;
        log.LogMessage(MessageImportance.Normal,
            $"CompilationCache info: Match={hashesMatch} LocatorKey={_locateResult.LocalInputsHash} RecalculatedKey={localInputsHash}");

        if (hashesMatch)
        {
            log.LogMessage(MessageImportance.Normal,
                $"CompilationCache miss - copying {_decomposed.OutputsToCache.Length} files from output to cache");
            var meta = GetCompilationMetadata(postCompilationTimeUtc);
            var stuff = new AllCompilationMetadata(Metadata: meta, LocalInputs: _localInputs);
            using var tmpDir = new DisposableDir();
            var outputZip = BuildOutputsZip(tmpDir, _decomposed.OutputsToCache, stuff, log);
            _cache.Set(_locateResult.CacheKey, _extract, outputZip);
        }
        else
        {
            log.LogMessage(MessageImportance.Normal,
                $"CompilationCache miss and inputs changed during compilation. The cache will not be populated as we are not certain what inputs the compiler used.");
        }

        return new UseOrPopulateResult();
    }

    public static FileInfo BuildOutputsZip(DirectoryInfo baseTmpDir, OutputItem[] items,
        AllCompilationMetadata metadata,
        TaskLoggingHelper? log = null)
    {
        var outputsDir = baseTmpDir.CreateSubdirectory("outputs_zip_building");

        var outputExtracts =
            items.Select(item =>
            {
                var tempPath = outputsDir.CombineAsFile(item.CacheFileName);
                log?.LogMessage(MessageImportance.Normal,
                    $"CompilationCache: Copy {item.LocalPath} -> {tempPath.FullName}");
                File.Copy(item.LocalPath, tempPath.FullName);
                return GetLocalFileExtract(tempPath.FullName).ToFileExtract();
            }).ToArray();

        var hashForFileName = Utils.ObjectToSHA256Hex(outputExtracts);

        var outputsExtractJson = JsonConvert.SerializeObject(outputExtracts, Formatting.Indented);
        var outputsExtractJsonPath = outputsDir.CombineAsFile("__outputs.json");
        File.WriteAllText(outputsExtractJsonPath.FullName, outputsExtractJson);

        var metaJson = JsonConvert.SerializeObject(metadata, Formatting.Indented);
        var metaPath = outputsDir.CombineAsFile("__inputs.json");
        File.WriteAllText(metaPath.FullName, metaJson);

        var tempZipPath = baseTmpDir.CombineAsFile($"{hashForFileName}.zip");
        ZipFile.CreateFromDirectory(outputsDir.FullName, tempZipPath.FullName,
            CompressionLevel.NoCompression, includeBaseDirectory: false);
        return tempZipPath;
    }
}