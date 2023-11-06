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
            PreCompilationTimeUtc: DateTime.MinValue,
            Inputs: inputs,
            DecomposedCompilerProps: null
        );
}

public class LocatorAndPopulator : IDisposable
{
    private readonly IRefCache _inMemoryRefCache;

    private (Config config, ICompilationResultsCache Cache, IRefCache RefCache, IFileHashCache FileHashCache, IHash) CreateCaches(string configPath,
        Action<string>? logTime = null)
    {
        using var fs = File.OpenRead(configPath);
        var config = JsonSerializer.Deserialize(fs, ConfigJsonContext.Default.Config)!;
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

        
        _localInputs = CalculateLocalInputsWithHash(logTime);
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
            PreCompilationTimeUtc: preCompilationTimeUtc,
            Inputs: inputs,
            Outcome: outcome,
            DecomposedCompilerProps: _decomposed
        );
        //logTime?.Invoke("end");
        return _locateResult;
    }

    private LocalInputs CalculateLocalInputsWithHash(Action<string>? logTime = null) => CalculateLocalInputs(logTime);

    private LocalInputs CalculateLocalInputs(Action<string>? logTime = null) =>
        CalculateLocalInputs(_decomposed, _refCache, _assemblyName, _config.RefTrimming, _fileHashCache, logTime);

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
            await fileHashCache.SetAsync(fileHashCacheKey, hash);
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
            await fileHashCache.SetAsync(fileHashCacheKey, hash);
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
            await refCache.SetAsync(refCacheKey, cached);
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

    public static string CreateHashString(FileHashCacheKey fileHashCacheKey)
    {
        var bytes = File.ReadAllBytes(fileHashCacheKey.FullName);
        return Utils.BytesToHashHex(bytes);
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
            await fileHashCache.SetAsync(fileCacheKey, hashString);
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
            await refCache.SetAsync(cacheKey, cached);
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
        string trimmedHash = ignoreInternals ? data.PublicRefHash : data.PublicAndInternalsRefHash ?? data.PublicRefHash;

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

    public record OutputData(OutputItem Item, byte[] Content, byte[] BytesHash, string BytesHashString)
    {
        public long Length => Content.Length;
    }
    
    public async Task<UseOrPopulateResult> PopulateCacheAsync(TaskLoggingHelper log, Action<string>? logTime = null)
    {
        var postCompilationTimeUtc = DateTime.UtcNow;

        var outputs = await System.Threading.Tasks.Task.WhenAll(_decomposed.OutputsToCache.Select(outputItem => GatherSingleOutputData(outputItem, _hasher)).ToArray());
     
        // Trigger RefTrimmer for newly-built dlls/exe files - should speed up builds of dependent projects,
        // and avoid any duplicate calculations due to multiple dependants redoing the same thing if timings are bad.

        async System.Threading.Tasks.Task RefasmCompiledDllsAsync(OutputData[] outputs)
        {
            static bool RefasmableOutputItem(OutputData o) => Path.GetExtension(o.Item.LocalPath) is ".dll";
            var outputsToRefasm = outputs.Where(RefasmableOutputItem).ToArray();
            if (outputsToRefasm.Length <= 1)
            {
                foreach (var dll in outputsToRefasm)
                {
                    await RefasmAndPopulateCacheWithOutputDll(dll);
                }
            }
            else
            {
                await Parallel.ForEachAsync(outputsToRefasm, (output, _) => RefasmAndPopulateCacheWithOutputDll(output));
            }
        }
        
        var refasmCompiledDllsTask = RefasmCompiledDllsAsync(outputs);
        var meta = GetCompilationMetadata(postCompilationTimeUtc);
        var allCompMetadata = new AllCompilationMetadata(Metadata: meta, LocalInputs: _localInputs.ToSlim());
    
        using var tmpDir = new DisposableDir();
        var outputZip = await BuildOutputsZip(tmpDir, outputs, allCompMetadata, _hasher, log);
        
        log.LogMessage(MessageImportance.Normal,
            $"CompilationCache - copying {_decomposed.OutputsToCache.Length} files from output to cache");
        //logTime?.Invoke("Outputs zip created");
        await _cache.SetAsync(_locateResult.CacheKey!.Value, _extract, outputZip);
        //logTime?.Invoke("cache entry set");

        await refasmCompiledDllsTask;
    
        return new UseOrPopulateResult();
    }

    internal static async Task<OutputData> GatherSingleOutputData(OutputItem outputItem, IHash hasher)
    {
        await using var f = File.Open(outputItem.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = await File.ReadAllBytesAsync(outputItem.LocalPath);
        var bytesHash = hasher.ComputeHash(bytes);
        var bytesHashString = Utils.BytesToHashHex(bytesHash, hasher);
        return new OutputData(outputItem, bytes, bytesHash, bytesHashString);
    }

    private async ValueTask RefasmAndPopulateCacheWithOutputDll(OutputData dll)
    {
        var trimmer = new RefTrimmer();
        var toBeCached = await trimmer.GenerateRefData(ImmutableArray.Create(dll.Content));
        var fileCacheKey = FileHashCacheKey.FromFileInfo(new FileInfo(dll.Item.LocalPath));
        var dllHash = await _fileHashCache.GetAsync(fileCacheKey);
        if (dllHash == null)
        {
            dllHash = Utils.BytesToHashHex(dll.Content);
            await _fileHashCache.SetAsync(fileCacheKey, dllHash);
        }
                
        var extract = new LocalFileExtract(fileCacheKey, dllHash);
        var name = Path.GetFileNameWithoutExtension(fileCacheKey.FullName);
        var cacheKey = BuildRefCacheKey(name, dllHash);
        var cached = new RefDataWithOriginalExtract(Ref: toBeCached, Original: extract);
        await _refCache.SetAsync(cacheKey, cached);
    }

    record ArchiveEntry(string Name, byte[] Bytes);
    
    public static async Task<FileInfo> BuildOutputsZip(DirectoryInfo baseTmpDir, OutputData[] items,
        AllCompilationMetadata metadata, IHash hasher,
        TaskLoggingHelper? log = null)
    {
        var outputsDir = baseTmpDir.CreateSubdirectory("outputs_zip_building");

        async Task<byte[]> SaveInputsAndReturnBytesAsync()
        {
            string metaPath = outputsDir.CombineAsFile("__inputs.json").FullName;
            await using var fs = File.OpenWrite(metaPath);
            byte[] bytes2 = JsonSerializer.SerializeToUtf8Bytes(metadata, AllCompilationMetadataJsonContext.Default.AllCompilationMetadata);
            await fs.WriteAsync(bytes2);
            return bytes2;
        }

        // Write inputs json in parallel to the rest
        var saveInputsTask = SaveInputsAndReturnBytesAsync();

        var objectToHash = items.Select(i => (i.Item.Name, i.BytesHash)).ToArray();

        var hashForFileName = Utils.ObjectToHash(objectToHash, hasher);

        var outputsExtractJsonPath = outputsDir.CombineAsFile("__outputs.json").FullName;
        byte[] outputsJsonBytes;
        {
            await using var fs = File.OpenWrite(outputsExtractJsonPath);
            var outputExtracts = items.Select(i => new FileExtract(i.Item.Name, i.BytesHashString, i.Length)).ToArray();
            outputsJsonBytes = JsonSerializer.SerializeToUtf8Bytes(outputExtracts, OutputExtractsJsonContext.Default.FileExtractArray);
            await fs.WriteAsync(outputsJsonBytes);
        }

        // Make sure inputs json written before zipping the whole directory
        var inputsJsonBytes = await saveInputsTask;

        var metaEntries = new[]
        {
            new ArchiveEntry("__inputs.json", inputsJsonBytes),
            new ArchiveEntry("__outputs.json", outputsJsonBytes),
        };
        var outputsEntries = items.Select(o => new ArchiveEntry(o.Item.CacheFileName, o.Content)).ToArray();
        var archiveEntries = metaEntries.Concat(outputsEntries).ToArray();
        
        var tempZipPath = baseTmpDir.CombineAsFile($"{hashForFileName}.zip");

        {
            await using var zip = File.OpenWrite(tempZipPath.FullName);
            using var a = new ZipArchive(zip, ZipArchiveMode.Create);
            foreach (var ae in archiveEntries)
            {
                var e = a.CreateEntry(ae.Name, CompressionLevel.NoCompression);
                await using var es = e.Open();
                await es.WriteAsync(ae.Bytes);
            }
        }
        
        return tempZipPath;
    }

    public void Dispose()
    {
        var files = _localInputs?.Files;
        if (files != null)
        {
            foreach (var f in files)
            {
                try
                {
                    f.f.Dispose();
                }
                catch (Exception)
                {
                    // ignored
                }
            }
        }
    }
}

[JsonSerializable(typeof(FileExtract[]))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class OutputExtractsJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(AllCompilationMetadata))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class AllCompilationMetadataJsonContext : JsonSerializerContext;

[Serializable]
public record InputResult(FileStream f, LocalFileExtract fileHashCacheKey);
