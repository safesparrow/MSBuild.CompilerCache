namespace MSBuild.CompilerCache;

using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using JsonSerializer = System.Text.Json.JsonSerializer;
using IFileHashCache = ICacheBase<FileCacheKey, string>;
using IRefCache = ICacheBase<CacheKey, RefDataWithOriginalExtract>;

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
    private readonly IRefCache _inMemoryRefCache;

    private (Config config, ICache Cache, IRefCache RefCache, IFileHashCache FileHashCache, IHash) CreateCaches(string configPath,
        Action<string>? logTime = null)
    {
        using var fs = File.OpenRead(configPath);
        var config = JsonSerializer.Deserialize<Config>(fs);
        //logTime?.Invoke("Config deserialized");
        var cache = new Cache(config.CacheDir);
        var refCache = new RefCache(config.InferRefCacheDir());
        //logTime?.Invoke("Finish");
        var fileBasedFileHashCache = new FileHashCache(config.InferFileHashCacheDir());
        var fileHashCache = new CacheCombiner<FileCacheKey, string>(_inMemoryFileHashCache, fileBasedFileHashCache);
        var hasher = HasherFactory.CreateHash(config.Hasher);
        return (config, cache, refCache, fileHashCache, hasher);
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
    private readonly IFileHashCache _inMemoryFileHashCache;
    private ICacheBase<FileCacheKey,string> _fileHashCache;
    private IHash _hasher;

    public LocatorAndPopulator(IRefCache? inMemoryRefCache = null, IFileHashCache? inMemoryFileHashCache = null)
    {
        _inMemoryRefCache = inMemoryRefCache ?? new DictionaryBasedCache<CacheKey, RefDataWithOriginalExtract>();
        _inMemoryFileHashCache = inMemoryFileHashCache ?? new DictionaryBasedCache<FileCacheKey, string>();
    }

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

        (_config, _cache, _refCache, _fileHashCache, _hasher) = CreateCaches(inputs.ConfigPath, logTime);
        _assemblyName = inputs.AssemblyName;
        //logTime?.Invoke("Caches created");

        (_localInputs, _localInputsHash) = CalculateLocalInputsWithHash(logTime);
        //logTime?.Invoke("LocalInputs with hash created");
        
        _extract = _localInputs.ToFullExtract();
        var hashString = Utils.ObjectToHash(_extract, _hasher);
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
        var localInputsHash = Utils.ObjectToHash(localInputs, _hasher);
        //logTime?.Invoke("hash done");
        return (localInputs, localInputsHash);
    }

    private LocalInputs CalculateLocalInputs(Action<string>? logTime = null) =>
        CalculateLocalInputs(_decomposed, _refCache, _assemblyName, _config.RefTrimming, _fileHashCache, _hasher, logTime);

    public record FileInputs(FileCacheKey[] References, FileCacheKey[] Other);

    private static FileInputs ExtractFileInputs(DecomposedCompilerProps decomposed)
    {
        static FileCacheKey[] Extract(string[] files) =>
            files
                .Chunk(Math.Max(1, files.Length / 4))
                .AsParallel()
                .SelectMany(refs => refs.Select(r => FileCacheKey.FromFileInfo(new FileInfo(r)))).ToArray();
        
        return new FileInputs(
            References: Extract(decomposed.References),
            Other: Extract(decomposed.FileInputs)
        );
    }
    
    internal static LocalInputs CalculateLocalInputs(DecomposedCompilerProps decomposed, IRefCache refCache,
        string assemblyName, RefTrimmingConfig trimmingConfig, IFileHashCache fileHashCache, IHash hasher, Action<string>? logTime = null)
    {
        var _fileInputs = ExtractFileInputs(decomposed);
        var fileInputs = trimmingConfig.Enabled
            ? _fileInputs.Other
            : _fileInputs.Other.Concat(_fileInputs.References).ToArray();
        var fileExtracts =
            fileInputs
                .Chunk(4)
                // .AsParallel()
                // .AsOrdered()
                // .WithDegreeOfParallelism(Math.Max(2, Environment.ProcessorCount / 2))
                // Preserve original order of files
                .SelectMany(paths => paths.Select(p => GetLocalFileExtract(p, fileHashCache)))
                .ToImmutableArray();

        //logTime?.Invoke("file extracts done");
        
        var refExtracts = trimmingConfig.Enabled
            ? decomposed.References
                .Chunk(20)
                .AsParallel()
                // Preserve original order of files
                .AsOrdered()
                .WithDegreeOfParallelism(8)
                .SelectMany(dlls =>
                {
                    return dlls.Select(dll =>
                    {
                        var allRefData = GetAllRefData(dll, refCache, fileHashCache, hasher);
                        return AllRefDataToExtract(allRefData, assemblyName, trimmingConfig.IgnoreInternalsIfPossible);
                    }).ToArray();
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

    private static LocalFileExtract GetLocalFileExtract(FileInfo fileInfo, FileCacheKey file)
    {
        var hashString = Utils.FileBytesToHashHex(fileInfo.FullName, Utils.DefaultHasher);
        return new LocalFileExtract(Info: file, Hash: hashString);
    }

    private static LocalFileExtract GetLocalFileExtract(FileCacheKey file, IFileHashCache fileHashCache)
    {
        var hashString = fileHashCache.Get(file);
        if (hashString == null)
        {
            var bytes = File.ReadAllBytes(file.FullName);
            hashString = Utils.BytesToHashHex(bytes);
            fileHashCache.Set(file, hashString);
        }
        return new LocalFileExtract(Info: file, Hash: hashString);
    }
    
    private static CompilationMetadata GetCompilationMetadata(DateTime postCompilationTimeUtc) =>
        new(
            Hostname: Environment.MachineName,
            Username: Environment.UserName,
            StopTimeUtc: postCompilationTimeUtc,
            WorkingDirectory: Environment.CurrentDirectory
        );

    internal static AllRefData GetAllRefData(string filepath, IRefCache refCache, IFileHashCache fileHashCache, IHash hasher)
    {
        var fileInfo = new FileInfo(filepath);
        if (!fileInfo.Exists)
        {
            throw new Exception($"Reference file does not exist: '{filepath}'");
        }

        var fileCacheKey = FileCacheKey.FromFileInfo(fileInfo);
        var hashString = fileHashCache.Get(fileCacheKey);
        byte[] bytes = null;
        if (hashString == null)
        {
            bytes = File.ReadAllBytes(filepath);
            hashString = Utils.BytesToHashHex(bytes, hasher);
            fileHashCache.Set(fileCacheKey, hashString);
        }

        var extract = new LocalFileExtract(fileCacheKey, hashString);
        var name = Path.GetFileNameWithoutExtension(fileInfo.Name);

        var cacheKey = BuildRefCacheKey(name, hashString);
        var cached = refCache.Get(cacheKey);
        if (cached == null)
        {
            bytes ??= File.ReadAllBytes(filepath);
            var trimmer = new RefTrimmer();
            var toBeCached = trimmer.GenerateRefData(ImmutableArray.Create(bytes));
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
        // We also depend on it working this way, when comparing file metadata in the 'PopulateCache' stage.
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
    
    public record struct SomeFileMetadata(long Length, DateTime LastWriteTimeUtc);

    public record struct InputsConsistencyResult(bool Changed,
        (SomeFileMetadata Before, SomeFileMetadata After)? ChangedFile);

    private string? CheckInputsConsistency(TaskLoggingHelper taskLoggingHelper)
    {
        bool FileChanged(FileCacheKey file)
        {
            var before = new SomeFileMetadata(file.Length, file.LastWriteTimeUtc);
            var refreshedFile = new FileInfo(file.FullName);
            var after = new SomeFileMetadata(refreshedFile.Length, refreshedFile.LastWriteTimeUtc);
            var changed = after != before;
            if (changed)
            {
                taskLoggingHelper.LogWarning($"{file.FullName} changed. Before: {before}, after: {after}");
            }
            return changed;
        }

        var changedFile =
            _localInputs.Files
                .Chunk(Math.Max(1, _localInputs.Files.Length / 4))
                .AsParallel()
                .Select(files =>
                {
                    foreach (var file in files)
                    {
                        if (FileChanged(file.Info))
                        {
                            return file.Info.FullName;
                        }
                    }

                    return null;
                })
                .FirstOrDefault(changedFile => changedFile != null);
        return changedFile;
    }
    
    public UseOrPopulateResult PopulateCache(TaskLoggingHelper log, Action<string>? logTime = null)
    {
        //logTime?.Invoke("Start");
        var postCompilationTimeUtc = DateTime.UtcNow;

        var changedFile = CheckInputsConsistency(log);
        
        //logTime?.Invoke("Calculated local inputs with hash");
        var consistent = changedFile == null;
        var consistencyString = consistent ? "Consistent" : $"Some files changed. Example: {changedFile}";
        log.LogMessage(MessageImportance.Normal, $"CompilationCache info: {consistencyString}");

        if (consistent)
        {
            //logTime?.Invoke("Hashes match");
            log.LogMessage(MessageImportance.Normal,
                $"CompilationCache - copying {_decomposed.OutputsToCache.Length} files from output to cache");
            var meta = GetCompilationMetadata(postCompilationTimeUtc);
            var stuff = new AllCompilationMetadata(Metadata: meta, LocalInputs: _localInputs);
            //logTime?.Invoke("Got stuff");
            using var tmpDir = new DisposableDir();
            var outputZip = BuildOutputsZip(tmpDir, _decomposed.OutputsToCache, stuff, Utils.DefaultHasher, log);
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
        AllCompilationMetadata metadata, IHash hasher,
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
                return GetLocalFileExtract(tempPath, FileCacheKey.FromFileInfo(tempPath)).ToFileExtract();
            }).ToArray();

        var hashForFileName = Utils.ObjectToHash(outputExtracts, hasher);

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