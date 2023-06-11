using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;

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
    public static (Config config, ICache Cache, IRefCache RefCache) CreateCaches(string configPath,
        Action<string>? logTime = null)
    {
        using var fs = File.OpenRead(configPath);
        var config = JsonSerializer.Deserialize<Config>(fs);
        //logTime?.Invoke("Config deserialized");
        var cache = new Cache(config.CacheDir);
        var refCache = new RefCache(config.InferRefCacheDir());
        //logTime?.Invoke("Finish");
        return (config, cache, refCache);
    }

    // These fields are populated in the 'Locate' call and used in a subsequent 'Populate' call
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

    public LocateResult Locate(LocateInputs inputs, TaskLoggingHelper? log = null, Action<string> logTime = null)
    {
        //logTime?.Invoke("Start Locate");
        var preCompilationTimeUtc = DateTime.UtcNow;

        _decomposed = TargetsExtractionUtils.DecomposeCompilerProps(inputs.AllProps, log);
        //logTime?.Invoke("Decomposed compiler props");
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

        (_config, _cache, _refCache) = CreateCaches(inputs.ConfigPath, logTime);
        _assemblyName = inputs.AssemblyName;
        //logTime?.Invoke("Caches created");

        (_localInputs, _localInputsHash) = CalculateLocalInputsWithHash(logTime);
        //logTime?.Invoke("LocalInputs with hash created");
        
        _extract = _localInputs.ToFullExtract();
        var hashString = Utils.ObjectToSHA256Hex(_extract);
        _cacheKey = GenerateKey(inputs, hashString);
        //logTime?.Invoke("Key generated");

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
            //logTime?.Invoke("Got cache result");
            if (cachedOutputTmpZip != null)
            {
                try
                {
                    outcome = LocateOutcome.CacheUsed;
                    var outputs = _decomposed.OutputsToCache;
                    log?.LogMessage(MessageImportance.Normal,
                        $"CompilationCache: Locate for {_cacheKey} was a hit. Copying {outputs.Length} files from cache");

                    UseCachedOutputs(cachedOutputTmpZip!, outputs, preCompilationTimeUtc);
                    //logTime?.Invoke("Used cached outputs");
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
        //logTime?.Invoke("end");
        return _locateResult;
    }

    private (LocalInputs, string) CalculateLocalInputsWithHash(Action<string>? logTime = null)
    {
        var localInputs = CalculateLocalInputs(logTime);
        //logTime?.Invoke("localinputs done");
        var localInputsHash = Utils.ObjectToSHA256Hex(localInputs);
        //logTime?.Invoke("hash done");
        return (localInputs, localInputsHash);
    }

    private LocalInputs CalculateLocalInputs(Action<string>? logTime = null) =>
        CalculateLocalInputs(_decomposed, _refCache, _assemblyName, _config.RefTrimming, logTime);

    internal static LocalInputs CalculateLocalInputs(DecomposedCompilerProps decomposed, IRefCache refCache,
        string assemblyName, RefTrimmingConfig trimmingConfig, Action<string>? logTime = null)
    {
        var nonReferenceFileInputs = decomposed.FileInputs;
        var fileInputs = trimmingConfig.Enabled
            ? nonReferenceFileInputs
            : nonReferenceFileInputs.Concat(decomposed.References).ToArray();
        var fileExtracts =
            fileInputs
                .Chunk(4)
                .AsParallel()
                .AsOrdered()
                .WithDegreeOfParallelism(Math.Max(2, Environment.ProcessorCount / 2))
                // Preserve original order of files
                .SelectMany(paths => paths.Select(GetLocalFileExtract))
                .ToImmutableArray();

        //logTime?.Invoke("file extracts done");
        
        var refExtracts = trimmingConfig.Enabled
            ? decomposed.References
                .AsParallel()
                // Preserve original order of files
                .AsOrdered()
                .Select(dll =>
                {
                    var allRefData = GetAllRefData(dll, refCache);
                    return AllRefDataToExtract(allRefData, assemblyName, trimmingConfig.IgnoreInternalsIfPossible);
                })
                .ToImmutableArray()
            : ImmutableArray.Create<LocalFileExtract>();

        //logTime?.Invoke("ref extracts done");
        
        var allExtracts = fileExtracts.Union(refExtracts).ToArray();

        var props = decomposed.PropertyInputs.Select(kvp => (kvp.Key, kvp.Value)).OrderBy(kvp => kvp.Key).ToArray();
        var outputs = decomposed.OutputsToCache.OrderBy(x => x.Name).ToArray();
        
        //logTime?.Invoke("orders done");

        return new LocalInputs(allExtracts, props, outputs);
    }

    private static LocalFileExtract GetLocalFileExtract(string filepath)
    {
        var fileInfo = new FileInfo(filepath);
        if (!fileInfo.Exists)
        {
            throw new Exception($"File does not exist: '{filepath}'");
        }

        var hashString = Utils.FileToSHA256String(fileInfo);
        // As we populate the hash, there is no need to use the modify date - otherwise, build-generated source files will always
        // cause a cache miss.
        return new LocalFileExtract(fileInfo.FullName, hashString, fileInfo.Length, null);
    }

    private static CompilationMetadata GetCompilationMetadata(DateTime postCompilationTimeUtc) =>
        new(
            Hostname: Environment.MachineName,
            Username: Environment.UserName,
            StopTimeUtc: postCompilationTimeUtc,
            WorkingDirectory: Environment.CurrentDirectory
        );

    private static AllRefData GetAllRefData(string filepath, IRefCache refCache)
    {
        var fileInfo = new FileInfo(filepath);
        if (!fileInfo.Exists)
        {
            throw new Exception($"Reference file does not exist: '{filepath}'");
        }

        // Note we don't hash the file contents to create the cache key
        var extract = new LocalFileExtract(fileInfo.FullName, null, fileInfo.Length, fileInfo.LastWriteTimeUtc);
        var fileExtract = extract.ToFileExtract();
        var hashString = Utils.ObjectToSHA256Hex(fileExtract);
        var name = Path.GetFileNameWithoutExtension(fileInfo.Name);

        var cacheKey = BuildRefCacheKey(name, hashString);
        var cached = refCache.Get(cacheKey);
        if (cached == null)
        {
            var bytes = ImmutableArray.Create(File.ReadAllBytes(filepath));
            var trimmer = new RefTrimmer();
            var toBeCached = trimmer.GenerateRefData(bytes);
            cached = new RefDataWithOriginalExtract(Ref: toBeCached, Original: extract);
            refCache.Set(cacheKey, cached);
        }

        return new AllRefData(
            Original: cached.Original,
            InternalsVisibleToAssemblies: cached.Ref.InternalsVisibleTo,
            PublicRefHash: cached.Ref.PublicRefHash,
            PublicAndInternalsRefHash: cached.Ref.PublicAndInternalRefHash
        );
    }

    private static LocalFileExtract AllRefDataToExtract(AllRefData data, string assemblyName,
        bool ignoreInternalsIfPossible)
    {
        bool internalsVisibleToOurAssembly =
            data.InternalsVisibleToAssemblies.Any(x =>
                string.Equals(x, assemblyName, StringComparison.OrdinalIgnoreCase));

        var ignoreInternals = ignoreInternalsIfPossible && !internalsVisibleToOurAssembly;
        var trimmedHash =
            ignoreInternals ? data.PublicRefHash : data.PublicAndInternalsRefHash;

        // TODO It's not very clean that we keep other properties of the original dll and only modify the hash, but it's enough for caching to work.
        return data.Original with { Hash = trimmedHash };
    }

    private static CacheKey BuildRefCacheKey(string name, string hashString) => new($"{name}_{hashString}");

    internal static void UseCachedOutputs(string localTmpOutputsZipPath, OutputItem[] items,
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

    internal static CacheKey GenerateKey(LocateInputs inputs, string hash)
    {
        var name = Path.GetFileName(inputs.ProjectFullPath);
        return new CacheKey($"{name}_{hash}");
    }

    public UseOrPopulateResult UseOrPopulate(TaskLoggingHelper log, Action<string>? logTime = null)
    {
        //logTime?.Invoke("Start");
        var postCompilationTimeUtc = DateTime.UtcNow;
        var (_, localInputsHash) = CalculateLocalInputsWithHash(logTime);
        //logTime?.Invoke("Calculated local inputs with hash");
        var hashesMatch = _locateResult.LocalInputsHash == localInputsHash;
        log.LogMessage(MessageImportance.Normal,
            $"CompilationCache info: Match={hashesMatch} LocatorKey={_locateResult.LocalInputsHash} RecalculatedKey={localInputsHash}");

        if (hashesMatch)
        {
            //logTime?.Invoke("Hashes match");
            log.LogMessage(MessageImportance.Normal,
                $"CompilationCache miss - copying {_decomposed.OutputsToCache.Length} files from output to cache");
            var meta = GetCompilationMetadata(postCompilationTimeUtc);
            var stuff = new AllCompilationMetadata(Metadata: meta, LocalInputs: _localInputs);
            //logTime?.Invoke("Got stuff");
            using var tmpDir = new DisposableDir();
            var outputZip = BuildOutputsZip(tmpDir, _decomposed.OutputsToCache, stuff, log);
            //logTime?.Invoke("Outputs zip created");
            _cache.Set(_locateResult.CacheKey, _extract, outputZip);
            //logTime?.Invoke("cache entry set");
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

        var jsonOptions = new JsonSerializerOptions()
        {
            WriteIndented = true
        };
        
        var outputsExtractJsonPath = outputsDir.CombineAsFile("__outputs.json").FullName;
        {
            using var fs = File.OpenWrite(outputsExtractJsonPath);
            JsonSerializer.Serialize(fs, outputExtracts, jsonOptions);
        }

        var metaPath = outputsDir.CombineAsFile("__inputs.json").FullName;
        {
            using var fs = File.OpenWrite(metaPath);
            JsonSerializer.Serialize(fs, metadata, jsonOptions);
        }

        var tempZipPath = baseTmpDir.CombineAsFile($"{hashForFileName}.zip");
        ZipFile.CreateFromDirectory(outputsDir.FullName, tempZipPath.FullName,
            CompressionLevel.NoCompression, includeBaseDirectory: false);
        return tempZipPath;
    }
}