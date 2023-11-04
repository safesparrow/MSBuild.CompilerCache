using System.Text.Json.Serialization;

namespace MSBuild.CompilerCache;

using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using JsonSerializer = System.Text.Json.JsonSerializer;
using IFileHashCache = ICacheBase<FileHashCacheKey, string>;
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

    private (Config config, ICompilationResultsCache Cache, IRefCache RefCache, IFileHashCache FileHashCache, IHash) CreateCaches(string configPath,
        Action<string>? logTime = null)
    {
        using var fs = File.OpenRead(configPath);
        var config = JsonSerializer.Deserialize<Config>(fs)!;
        //logTime?.Invoke("Config deserialized");
        var cache = new CompilationResultsCache(config.CacheDir);
        var refCache = new RefCache(config.InferRefCacheDir());
        var combinedRefCache = CacheCombiner.Combine(_inMemoryRefCache, refCache);
        //logTime?.Invoke("Finish");
        var fileBasedFileHashCache = new FileHashCache(config.InferFileHashCacheDir());
        var fileHashCache = new CacheCombiner<FileHashCacheKey, string>(_inMemoryFileHashCache, fileBasedFileHashCache);
        var hasher = HasherFactory.CreateHash(config.Hasher);
        return (config, cache, combinedRefCache, fileHashCache, hasher);
    }

    // These fields are populated in the 'Locate' call and used in a subsequent 'Populate' call
    private IRefCache _refCache;
    private ICompilationResultsCache _cache;
    private Config _config;
    private DecomposedCompilerProps _decomposed;
    private string _assemblyName;
    private LocateResult _locateResult;
    private FullExtract _extract;
    private CacheKey _cacheKey;
    private LocalInputs _localInputs;
    private string _localInputsHash;
    private readonly IFileHashCache _inMemoryFileHashCache;
    private ICacheBase<FileHashCacheKey,string> _fileHashCache;
    private IHash _hasher;

    public LocatorAndPopulator(IRefCache? inMemoryRefCache = null, IFileHashCache? inMemoryFileHashCache = null)
    {
        _inMemoryRefCache = inMemoryRefCache ?? new DictionaryBasedCache<CacheKey, RefDataWithOriginalExtract>();
        _inMemoryFileHashCache = inMemoryFileHashCache ?? new DictionaryBasedCache<FileHashCacheKey, string>();
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
        CalculateLocalInputs(_decomposed, _refCache, _assemblyName, _config.RefTrimming, _fileHashCache, logTime);

    public record FileInputs(FileHashCacheKey[] References, FileHashCacheKey[] Other);
    public record FileInputs2(string[] References, string[] Other);

    private static FileInputs ExtractFileInputs(DecomposedCompilerProps decomposed)
    {
        static FileHashCacheKey[] Extract(string[] files) =>
            files
                .Chunk(Math.Max(1, files.Length / 4))
                .AsParallel()
                .AsOrdered()
                .SelectMany(refs => refs.Select(r => FileHashCacheKey.FromFileInfo(new FileInfo(r)))).ToArray();
        
        return new FileInputs(
            References: Extract(decomposed.References),
            Other: Extract(decomposed.FileInputs)
        );
    }

    public static async Task<InputResult> ProcessSourceFile(string relativePath, IFileHashCache fileHashCache)
    {
        // Open the file for reading, and don't allow anyone to modify it.
        var f = File.Open(relativePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var info = new FileInfo(relativePath);
        var fileHashCacheKey = FileHashCacheKey.FromFileInfo(info);
        var hash = await fileHashCache.GetAsync(fileHashCacheKey);
        if (hash == null)
        {
            var bytes = await File.ReadAllBytesAsync(fileHashCacheKey.FullName);
            hash = Utils.BytesToHashHex(bytes);
            fileHashCache.Set(fileHashCacheKey, hash);
        }

        var localFileExtract = new LocalFileExtract(fileHashCacheKey, hash);

        return new InputResult(f, localFileExtract);
    }
    
    
    public static async Task<InputResult> ProcessReference(string relativePath, string compilingAssemblyName, IFileHashCache fileHashCache, IRefCache refCache, RefTrimmingConfig refTrimmingConfig)
    {
        // Open the file for reading, and don't allow anyone to modify it.
        var f = File.Open(relativePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var info = new FileInfo(relativePath);
        var fileHashCacheKey = FileHashCacheKey.FromFileInfo(info);
        var hash = await fileHashCache.GetAsync(fileHashCacheKey);
        byte[]? bytes = null;
        
        if (hash == null)
        {
            bytes ??= await File.ReadAllBytesAsync(fileHashCacheKey.FullName);
            hash = Utils.BytesToHashHex(bytes);
            fileHashCache.Set(fileHashCacheKey, hash);
        }
        
        var referenceDllName = Path.GetFileNameWithoutExtension(fileHashCacheKey.FullName);
        var originalExtract = new LocalFileExtract(fileHashCacheKey, hash);

        var refCacheKey = BuildRefCacheKey(referenceDllName, hash);
        var cached = await refCache.GetAsync(refCacheKey);
        if (cached == null)
        {
            bytes ??= await File.ReadAllBytesAsync(fileHashCacheKey.FullName);

            var toBeCached = await new RefTrimmer().GenerateRefData(ImmutableArray.Create(bytes));
            cached = new RefDataWithOriginalExtract(Ref: toBeCached, Original: originalExtract);
            refCache.Set(refCacheKey, cached);
        }

        var allRefData = new AllRefData(Original: cached.Original, RefData: cached.Ref);
        
        var data = AllRefDataToExtract(allRefData, compilingAssemblyName, refTrimmingConfig.IgnoreInternalsIfPossible);
        var extract = new LocalFileExtract(fileHashCacheKey, data.Hash);
        return new InputResult(f, extract);
    }
    
    internal static LocalInputs CalculateLocalInputs(DecomposedCompilerProps decomposed, IRefCache refCache,
        string compilingAssemblyName, RefTrimmingConfig trimmingConfig, IFileHashCache fileHashCache, Action<string>? logTime = null)
    {
        string[] fileInputs2, references2;
        if (trimmingConfig.Enabled)
        {
            fileInputs2 = decomposed.FileInputs;
            references2 = decomposed.References;
        }
        else
        {
            fileInputs2 = decomposed.FileInputs.Concat(decomposed.References).ToArray();
            references2 = Array.Empty<string>();
        }
        
        var fileTasks = fileInputs2.Select(f => (Func<Task<InputResult>>)(() => ProcessSourceFile(f, fileHashCache)));
        var refTasks = references2.Select(r => (Func<Task<InputResult>>)(() => ProcessReference(r, compilingAssemblyName, fileHashCache, refCache, trimmingConfig)));
        var allTaskFuncs = fileTasks.Concat(refTasks).ToArray();
        var allTasks =
            allTaskFuncs
                .Chunk(20)
                .SelectMany(taskFuncs => taskFuncs.Select(tf => tf()))
                .AsParallel()
                .ToArray();
        var allItems = System.Threading.Tasks.Task.WhenAll(allTasks).GetAwaiter().GetResult();

        //logTime?.Invoke("ref extracts done");
        
        var props = decomposed.PropertyInputs.Select(kvp => (kvp.Key, kvp.Value)).OrderBy(kvp => kvp.Key).ToArray();
        var outputs = decomposed.OutputsToCache.OrderBy(x => x.Name).ToArray();
        
        //logTime?.Invoke("orders done");

        return new LocalInputs(allItems, props, outputs);
    }

    private static LocalFileExtract GetLocalFileExtract(FileInfo fileInfo, FileHashCacheKey fileHash)
    {
        var hashString = Utils.FileBytesToHashHex(fileInfo.FullName, Utils.DefaultHasher);
        return new LocalFileExtract(Info: fileHash, Hash: hashString);
    }

    public static string CreateHashString(FileHashCacheKey fileHashCacheKey)
    {
        var bytes = File.ReadAllBytes(fileHashCacheKey.FullName);
        return Utils.BytesToHashHex(bytes);
    }

    private static async Task<LocalFileExtract> GetLocalFileExtract(FileHashCacheKey fileKey, IFileHashCache fileHashCache)
    {
        var hashString = await fileHashCache.GetOrSet(fileKey, CreateHashString);
        
        return new LocalFileExtract(Info: fileKey, Hash: hashString);
    }
    
    private static CompilationMetadata GetCompilationMetadata(DateTime postCompilationTimeUtc) =>
        new(
            Hostname: Environment.MachineName,
            Username: Environment.UserName,
            StopTimeUtc: postCompilationTimeUtc,
            WorkingDirectory: Environment.CurrentDirectory
        );

    internal static async Task<AllRefData> GetAllRefData(string filepath, IRefCache refCache, IFileHashCache fileHashCache, IHash hasher)
    {
        var fileInfo = new FileInfo(filepath);
        if (!fileInfo.Exists)
        {
            throw new Exception($"Reference file does not exist: '{filepath}'");
        }

        var fileCacheKey = FileHashCacheKey.FromFileInfo(fileInfo);
        var hashString = await fileHashCache.GetAsync(fileCacheKey);
        byte[]? bytes = null;
        if (hashString == null)
        {
            bytes ??= await File.ReadAllBytesAsync(filepath);
            hashString = Utils.BytesToHashHex(bytes, hasher);
            fileHashCache.Set(fileCacheKey, hashString);
        }

        var extract = new LocalFileExtract(fileCacheKey, hashString);
        var name = Path.GetFileNameWithoutExtension(fileInfo.Name);

        var cacheKey = BuildRefCacheKey(name, hashString);
        var cached = refCache.GetAsync(cacheKey).GetAwaiter().GetResult();
        if (cached == null)
        {
            bytes ??= await File.ReadAllBytesAsync(filepath);
            var trimmer = new RefTrimmer();
            var toBeCached = await trimmer.GenerateRefData(ImmutableArray.Create(bytes));
            cached = new RefDataWithOriginalExtract(Ref: toBeCached, Original: extract);
            refCache.Set(cacheKey, cached);
        }

        return new AllRefData(Original: cached.Original, RefData: cached.Ref);
    }

    /// <summary>
    /// Decide which trimmed version of an assembly to use as an "input" for hashing,
    /// and return a <see cref="LocalFileExtract"/> that represents that version.
    /// </summary>
    /// <param name="data">Data about the original DLL and its trimmed versions</param>
    /// <param name="compilingAssemblyName">Name of the assembly being compiled - used to decide whether internal symbols are relevant to that assembly.</param>
    /// <param name="ignoreInternalsIfPossible">If false, will always use the assembly version with both Public and Internal symbols</param>
    /// <returns></returns>
    private static LocalFileExtract AllRefDataToExtract(AllRefData data, string compilingAssemblyName,
        bool ignoreInternalsIfPossible)
    {
        bool internalsVisibleToOurAssembly =
            data.InternalsVisibleToAssemblies.Any(x =>
                string.Equals(x, compilingAssemblyName, StringComparison.OrdinalIgnoreCase));

        bool ignoreInternals = ignoreInternalsIfPossible && !internalsVisibleToOurAssembly;
        string? trimmedHash = ignoreInternals ? data.PublicRefHash : data.PublicAndInternalsRefHash;

        // This extract is used to find cached compilation results.
        // We want it to return the same value if the appropriately trimmed version of the dll is the same.
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

    public UseOrPopulateResult PopulateCache(TaskLoggingHelper log, Action<string>? logTime = null)
    {
        // Trigger RefTrimmer for newly-built dlls/exe files - should speed up builds of dependent projects,
        // and avoid any duplicate calculations due to multiple dependants redoing the same thing if timings are bad.
        
        //logTime?.Invoke("Start");
        var postCompilationTimeUtc = DateTime.UtcNow;

        // TODO Refactor, deduplicate
        async System.Threading.Tasks.Task RefasmCompiledDlls()
        {
            static bool RefasmableOutputItem(OutputItem o) => Path.GetExtension(o.LocalPath) is ".dll";
            var dllsToRefasm = _decomposed.OutputsToCache.Where(RefasmableOutputItem).ToArray();
            foreach (var dll in dllsToRefasm)
            {
                var bytes = await File.ReadAllBytesAsync(dll.LocalPath);
                var trimmer = new RefTrimmer();
                var toBeCached = await trimmer.GenerateRefData(ImmutableArray.Create(bytes));
                var fileInfo = new FileInfo(dll.LocalPath);
                var fileCacheKey = FileHashCacheKey.FromFileInfo(fileInfo);
                var hashString = await _fileHashCache.GetOrSet(fileCacheKey, CreateHashString);
                var extract = new LocalFileExtract(fileCacheKey, hashString);
                var name = Path.GetFileNameWithoutExtension(fileInfo.Name);
                var cacheKey = BuildRefCacheKey(name, hashString);
                var cached = new RefDataWithOriginalExtract(Ref: toBeCached, Original: extract);
                _refCache.Set(cacheKey, cached);
            }
        }

        var refasmCompiledDllsTask = System.Threading.Tasks.Task.Factory.StartNew(RefasmCompiledDlls);

        var meta = GetCompilationMetadata(postCompilationTimeUtc);
        var stuff = new AllCompilationMetadata(Metadata: meta, LocalInputs: _localInputs);
        //logTime?.Invoke("Got stuff");
        
        using var tmpDir = new DisposableDir();
        var outputZip = BuildOutputsZip(tmpDir, _decomposed.OutputsToCache, stuff, Utils.DefaultHasher, log);

        //logTime?.Invoke("Hashes match");
        log.LogMessage(MessageImportance.Normal,
            $"CompilationCache - copying {_decomposed.OutputsToCache.Length} files from output to cache");
        //logTime?.Invoke("Outputs zip created");
        _cache.Set(_locateResult.CacheKey!.Value, _extract, outputZip);
        //logTime?.Invoke("cache entry set");

        refasmCompiledDllsTask.GetAwaiter().GetResult();
        
        return new UseOrPopulateResult();
    }

    public static FileInfo BuildOutputsZip(DirectoryInfo baseTmpDir, OutputItem[] items,
        AllCompilationMetadata metadata, IHash hasher,
        TaskLoggingHelper? log = null)
    {
        var outputsDir = baseTmpDir.CreateSubdirectory("outputs_zip_building");

        async System.Threading.Tasks.Task SaveInputs()
        {
            var metaPath = outputsDir!.CombineAsFile("__inputs.json").FullName;
            // ReSharper disable once UseAwaitUsing
            using var fs = File.OpenWrite(metaPath);
            await JsonSerializer.SerializeAsync(fs, metadata,
                AllCompilationMetadataJsonContext.Default.AllCompilationMetadata);
        }

        // Write inputs json in parallel to the rest
        var saveInputsTask = SaveInputs();

        var outputExtracts =
            items.Select(item =>
            {
                var tempPath = outputsDir.CombineAsFile(item.CacheFileName);
                log?.LogMessage(MessageImportance.Normal,
                    $"CompilationCache: Copy {item.LocalPath} -> {tempPath.FullName}");
                File.Copy(item.LocalPath, tempPath.FullName);
                return GetLocalFileExtract(tempPath, FileHashCacheKey.FromFileInfo(tempPath)).ToFileExtract();
            }).ToArray();

        var hashForFileName = Utils.ObjectToHash(outputExtracts, hasher);

        var outputsExtractJsonPath = outputsDir.CombineAsFile("__outputs.json").FullName;
        {
            using var fs = File.OpenWrite(outputsExtractJsonPath);
            JsonSerializer.Serialize(fs, outputExtracts, OutputExtractsJsonContext.Default.FileExtractArray);
        }

        // Make sure inputs json written before zipping the whole directory
        saveInputsTask.GetAwaiter().GetResult();

        var tempZipPath = baseTmpDir.CombineAsFile($"{hashForFileName}.zip");
        // TODO Create zip archive from in-memory data rather than files on disk
        
        ZipFile.CreateFromDirectory(outputsDir.FullName, tempZipPath.FullName,
            CompressionLevel.NoCompression, includeBaseDirectory: false);
        return tempZipPath;
    }
}

[JsonSerializable(typeof(FileExtract[]))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class OutputExtractsJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(AllCompilationMetadata))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class AllCompilationMetadataJsonContext : JsonSerializerContext;

public record struct FileHash(string Hash);

public record InputResult(FileStream f, LocalFileExtract fileHashCacheKey);
