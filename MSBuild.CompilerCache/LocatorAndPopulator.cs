using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Task = System.Threading.Tasks.Task;

namespace MSBuild.CompilerCache;

using JsonSerializer = JsonSerializer;
using IFileHashCache = ICacheBase<FileHashCacheKey, string>;
using IRefCache = ICacheBase<CacheKey, RefDataWithOriginalExtract>;

public record UseOrPopulateResult(bool CachePopulated);

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

    private (Config config, ICompilationResultsCache Cache, IRefCache RefCache, IFileHashCache FileHashCache, IHash)
        CreateCaches(string configPath,
            Action<string>? logTime = null)
    {
        using var fs = File.OpenRead(configPath);
        var config = JsonSerializer.Deserialize(fs, ConfigJsonContext.Default.Config)!;
        //logTime?.Invoke("Config deserialized");
        var cache = new CompilationResultsCache(config.CacheDir);
        var refCache = new RefCache(config.InferRefCacheDir());
        var combinedRefCache = CacheCombiner.Combine(_inMemoryRefCache, refCache);
        //logTime?.Invoke("Finish");
        var hasher = HasherFactory.CreateHash(config.Hasher);
        var fileBasedFileHashCache = new FileHashCache(config.InferFileHashCacheDir(), hasher);
        var fileHashCache = new CacheCombiner<FileHashCacheKey, string>(_inMemoryFileHashCache, fileBasedFileHashCache);
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
    private ICacheBase<FileHashCacheKey, string> _fileHashCache;
    private IHash _hasher;

    public LocatorAndPopulator(IRefCache? inMemoryRefCache = null, IFileHashCache? inMemoryFileHashCache = null)
    {
        _inMemoryRefCache = inMemoryRefCache ?? new DictionaryBasedCache<CacheKey, RefDataWithOriginalExtract>();
        _inMemoryFileHashCache = inMemoryFileHashCache ?? new DictionaryBasedCache<FileHashCacheKey, string>();
    }

    public LocateResult Locate(LocateInputs inputs, TaskLoggingHelper? log = null, Action<string> logTime = null)
    {
        using var activity = Tracing.Source.StartActivity("Locate");
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

        (_config, _cache, _refCache, _fileHashCache, _hasher) = CreateCaches(inputs.ConfigPath, logTime);
        _assemblyName = inputs.AssemblyName;
        _localInputs = CalculateLocalInputs(logTime);
        _extract = _localInputs.ToSlim().ToFullExtract();
        var extractBytes = JsonSerializer.SerializeToUtf8Bytes(_extract, FullExtractJsonContext.Default.FullExtract);
        File.WriteAllBytes("c:/projekty/dwa.json", extractBytes);
        var hashString = Utils.BytesToHash(extractBytes, _hasher);
        _cacheKey = GenerateKey(inputs, hashString);
        
        LocateOutcome outcome;

        if (_config.CheckCompileOutputAgainstCache)
        {
            outcome = LocateOutcome.OnlyPopulateCache;
            log?.LogMessage(MessageImportance.Normal,
                "CompilationCache: CheckCompileOutputAgainstCache is set - not using cached outputs.");
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

    private LocalInputs CalculateLocalInputs(Action<string>? logTime = null) =>
        CalculateLocalInputs(_decomposed, _refCache, _assemblyName, _config.RefTrimming, _fileHashCache, _hasher,
            logTime);

    public static async Task<InputResult> ProcessSourceFile(string relativePath, IFileHashCache fileHashCache,
        IHash hasher)
    {
        using var activity = Tracing.Source.StartActivity("ProcessSourceFile");
        activity?.SetTag("relativePath", relativePath);
        // Open the file for reading, and don't allow anyone to modify it.
        var info = new FileInfo(relativePath);
        var f = info.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        var fileHashCacheKey = FileHashCacheKey.FromFileInfo(info);
        var hash = await fileHashCache.GetAsync(fileHashCacheKey);
        if (hash == null)
        {
            var bytes = await ReadFileAsync(fileHashCacheKey.FullName);
            hash = Utils.BytesToHash(bytes, hasher);
            await fileHashCache.SetAsync(fileHashCacheKey, hash);
        }

        var localFileExtract = new LocalFileExtract(fileHashCacheKey, hash);

        return new InputResult(f, localFileExtract);
    }

    public static Task<byte[]> ReadFileAsync(string path)
    {
        using var activity = Tracing.Source.StartActivity("ReadFileAsync");
        activity?.SetTag("path", path);
        return File.ReadAllBytesAsync(path);
    }

    public static async Task<InputResult> ProcessReference(string relativePath, string compilingAssemblyName,
        IFileHashCache fileHashCache, IRefCache refCache, RefTrimmingConfig refTrimmingConfig, IHash hasher)
    {
        using var activity = Tracing.Source.StartActivity("ProcessReference");
        activity?.SetTag("relativePath", relativePath);
        // Open the file for reading, and don't allow anyone to modify it.
        var info = new FileInfo(relativePath);
        var f = info.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        var fileHashCacheKey = FileHashCacheKey.FromFileInfo(info);
        string? hash = await fileHashCache.GetAsync(fileHashCacheKey);
        byte[]? bytes = null;
        bool hashFound = hash != null;
        activity?.SetTag("hashFound", hashFound);
        if (!hashFound)
        {
            bytes ??= await ReadFileAsync(fileHashCacheKey.FullName);
            hash = Utils.BytesToHash(bytes, hasher);
            await fileHashCache.SetAsync(fileHashCacheKey, hash);
        }

        var referenceDllName = Path.GetFileNameWithoutExtension(fileHashCacheKey.FullName);
        var originalExtract = new LocalFileExtract(fileHashCacheKey, hash);

        var refCacheKey = BuildRefCacheKey(referenceDllName, hash);
        var cached = await refCache.GetAsync(refCacheKey);
        if (cached == null)
        {
            bytes ??= await ReadFileAsync(fileHashCacheKey.FullName);

            var toBeCached = await new RefTrimmer(hasher).GenerateRefData(ImmutableArray.Create(bytes));
            cached = new RefDataWithOriginalExtract(Ref: toBeCached, Original: originalExtract);
            await refCache.SetAsync(refCacheKey, cached);
        }

        var allRefData = new AllRefData(Original: cached.Original, RefData: cached.Ref);

        var data = AllRefDataToExtract(allRefData, compilingAssemblyName, refTrimmingConfig.IgnoreInternalsIfPossible);
        var extract = new LocalFileExtract(fileHashCacheKey, data.Hash);
        extract = originalExtract; // TODO Revert once bug found
        return new InputResult(f, extract);
    }

    internal static LocalInputs CalculateLocalInputs(DecomposedCompilerProps decomposed, IRefCache refCache,
        string compilingAssemblyName, RefTrimmingConfig trimmingConfig, IFileHashCache fileHashCache, IHash hasher,
        Action<string>? logTime = null)
    {
        string[] fileInputs, references;
        if (trimmingConfig.Enabled)
        {
            fileInputs = decomposed.FileInputs;
            references = decomposed.References;
        }
        else
        {
            fileInputs = decomposed.FileInputs.Concat(decomposed.References).ToArray();
            references = Array.Empty<string>();
        }

        var fileTasks =
            fileInputs.Select(f => (Func<Task<InputResult>>)(() => ProcessSourceFile(f, fileHashCache, hasher)));
        var refTasks = references.Select(r => (Func<Task<InputResult>>)(() =>
            ProcessReference(r, compilingAssemblyName, fileHashCache, refCache, trimmingConfig, hasher)));
        var allTaskFuncs = fileTasks.Concat(refTasks).ToArray();
        var allTasks =
            allTaskFuncs
                .Chunk(20)
                .AsParallel()
                .AsOrdered()
                .SelectMany(taskFuncs => taskFuncs.Select(tf => tf()))
                .ToArray();
        var allItems = Task.WhenAll(allTasks).GetAwaiter().GetResult();

        //logTime?.Invoke("ref extracts done");

        var props = decomposed.PropertyInputs.Select(kvp => (kvp.Key, kvp.Value)).OrderBy(kvp => kvp.Key).ToArray();
        var outputs = decomposed.OutputsToCache.OrderBy(x => x.Name).ToArray();

        //logTime?.Invoke("orders done");

        return new LocalInputs(allItems, props, outputs);
    }

    private static CompilationMetadata GetCompilationMetadata(DateTime postCompilationTimeUtc) =>
        new(
            Hostname: Environment.MachineName,
            Username: Environment.UserName,
            StopTimeUtc: postCompilationTimeUtc,
            WorkingDirectory: Environment.CurrentDirectory
        );

    internal static async Task<AllRefData> GetAllRefData(string filepath, IRefCache refCache,
        IFileHashCache fileHashCache, IHash hasher)
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
            bytes ??= await ReadFileAsync(filepath);
            hashString = Utils.BytesToHash(bytes, hasher);
            await fileHashCache.SetAsync(fileCacheKey, hashString);
        }

        var extract = new LocalFileExtract(fileCacheKey, hashString);
        var name = Path.GetFileNameWithoutExtension(fileInfo.Name);

        var cacheKey = BuildRefCacheKey(name, hashString);
        var cached = refCache.GetAsync(cacheKey).GetAwaiter().GetResult();
        if (cached == null)
        {
            bytes ??= await ReadFileAsync(filepath);
            var trimmer = new RefTrimmer(hasher);
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
        string trimmedHash =
            ignoreInternals ? data.PublicRefHash : data.PublicAndInternalsRefHash ?? data.PublicRefHash;

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
        Console.WriteLine($"GeneratedKey. Name={name}_Hash={hash}");
        return new CacheKey($"{name}_{hash}");
    }

    public record OutputData(OutputItem Item, byte[] Content, byte[] BytesHash, string BytesHashString)
    {
        public long Length => Content.Length;
    }

    public async Task<UseOrPopulateResult> PopulateCacheAsync(TaskLoggingHelper log, Action<string>? logTime = null)
    {
        var postCompilationTimeUtc = DateTime.UtcNow;

        var outputs = await Task.WhenAll(_decomposed.OutputsToCache
            .Select(outputItem => GatherSingleOutputData(outputItem, _hasher)).ToArray());

        // Trigger RefTrimmer for newly-built dlls/exe files - should speed up builds of dependent projects,
        // and avoid any duplicate calculations due to multiple dependants redoing the same thing if timings are bad.

        async Task RefasmCompiledDllsAsync(OutputData[] outputs)
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
                await Parallel.ForEachAsync(outputsToRefasm,
                    (output, _) => RefasmAndPopulateCacheWithOutputDll(output));
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

        return new UseOrPopulateResult(CachePopulated: true);
    }

    internal static async Task<OutputData> GatherSingleOutputData(OutputItem outputItem, IHash hasher)
    {
        await using var f = File.Open(outputItem.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = await ReadFileAsync(outputItem.LocalPath);
        var bytesHash = hasher.ComputeHash(bytes);
        var bytesHashString = Utils.BytesToHash(bytesHash, hasher);
        return new OutputData(outputItem, bytes, bytesHash, bytesHashString);
    }

    private async ValueTask RefasmAndPopulateCacheWithOutputDll(OutputData dll)
    {
        var trimmer = new RefTrimmer(_hasher);
        var toBeCached = await trimmer.GenerateRefData(ImmutableArray.Create(dll.Content));
        var fileCacheKey = FileHashCacheKey.FromFileInfo(new FileInfo(dll.Item.LocalPath));
        var dllHash = await _fileHashCache.GetAsync(fileCacheKey);
        if (dllHash == null)
        {
            dllHash = Utils.BytesToHash(dll.Content, _hasher);
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
        // Write inputs json in parallel to the rest
        var saveInputsTask = Task.Run(
            () => JsonSerializer.SerializeToUtf8Bytes(metadata,
                AllCompilationMetadataJsonContext.Default.AllCompilationMetadata));
        var objectToHash = metadata.LocalInputs.ToFullExtract();
        var extractBytes =
            JsonSerializer.SerializeToUtf8Bytes(objectToHash, FullExtractJsonContext.Default.FullExtract);
        File.WriteAllBytes("c:/projekty/raz.json", extractBytes);
        var hashForFileName = Utils.BytesToHash(extractBytes, hasher);

        var outputExtracts = items.Select(i => new FileExtract(i.Item.Name, i.BytesHashString, i.Length)).ToArray();
        byte[] outputsJsonBytes =
            JsonSerializer.SerializeToUtf8Bytes(outputExtracts, FileExtractsJsonContext.Default.FileExtractArray);

        // Make sure inputs json written before zipping the whole directory
        var inputsJsonBytes = await saveInputsTask;

        var metaEntries = new[]
        {
            new ArchiveEntry("__inputs.json", inputsJsonBytes),
            new ArchiveEntry("__outputs.json", outputsJsonBytes),
        };
        var outputsEntries = items.Select(o => new ArchiveEntry(o.Item.CacheFileName, o.Content)).ToArray();
        var archiveEntries = metaEntries.Concat(outputsEntries).ToArray();

        return await BuildZip();

        async Task<FileInfo> BuildZip()
        {
            var tempZipPath = baseTmpDir.CombineAsFile($"{hashForFileName}.zip");
            await using var zip = tempZipPath.OpenWrite();
            using var a = new ZipArchive(zip, ZipArchiveMode.Create);
            foreach (var ae in archiveEntries)
            {
                var e = a.CreateEntry(ae.Name, CompressionLevel.NoCompression);
                await using var es = e.Open();
                await es.WriteAsync(ae.Bytes);
            }

            return tempZipPath;
        }
    }

    public async Task<UseOrPopulateResult> PopulateCacheOrJustDispose(TaskLoggingHelper log, Action<string> logTime, bool compilationSucceeded)
    {
        try
        {
            if (compilationSucceeded)
            {
                return await PopulateCacheAsync(log, logTime);
            }
            else
            {
                return new UseOrPopulateResult(CachePopulated: false);
            }
        }
        finally
        {
            Dispose();
        }
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
                    // This is a best effort attempt to clean up files.
                }
            }
        }
    }
}
