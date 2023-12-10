using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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

    private (
        Config config,
        CompilationResultsCache cache,
        CacheBaseLoggingDecorator<CacheKey, RefDataWithOriginalExtract> loggingRefCache,
        CacheBaseLoggingDecorator<CacheKey, RefDataWithOriginalExtract> combinedRefCache,
        CacheBaseLoggingDecorator<FileHashCacheKey, string> fileHashCache,
        CacheBaseLoggingDecorator<FileHashCacheKey, string>,
        IHash hasher
    )
        CreateCaches(string configPath,
            Action<string>? logTime = null)
    {
        using var fs = File.OpenRead(configPath);
        var config = JsonSerializer.Deserialize(fs, ConfigJsonContext.Default.Config)!;
        //logTime?.Invoke("Config deserialized");
        var cache = new CompilationResultsCache(config.CacheDir);
        var refCache = new RefCache(config.InferRefCacheDir());
        var loggingRefCache = refCache.WithLogging();
        var combinedRefCache = CacheCombiner.Combine(_inMemoryRefCache, loggingRefCache).WithLogging();
        
        //logTime?.Invoke("Finish");
        var hasher = HasherFactory.CreateHash(config.Hasher);
        var fileBasedFileHashCache = new FileHashCache(config.InferFileHashCacheDir(), hasher, _counters).WithLogging();
        var fileHashCache = CacheCombiner.Combine(_inMemoryFileHashCache, fileBasedFileHashCache).WithLogging();
        return (config, cache, loggingRefCache, combinedRefCache, fileHashCache, fileBasedFileHashCache, hasher);
    }

    // These fields are populated in the 'Locate' call and used in a subsequent 'Populate' call
    private CacheBaseLoggingDecorator<CacheKey, RefDataWithOriginalExtract> _combinedRefCache;
    private ICompilationResultsCache _cache;
    private Config _config;
    private DecomposedCompilerProps _decomposed;
    private string _assemblyName;
    private LocateResult _locateResult;
    private FullExtract _extract;
    private CacheKey _cacheKey;
    private LocalInputs _localInputs;
    private readonly IFileHashCache _inMemoryFileHashCache;
    private CacheBaseLoggingDecorator<FileHashCacheKey, string> _combinedFileHashCache;
    private IHash _hasher;
    private CacheBaseLoggingDecorator<CacheKey,RefDataWithOriginalExtract> _loggingRefCache;
    private CacheBaseLoggingDecorator<FileHashCacheKey,string> _rawFileHashCache;
    private readonly Counters _counters = new Counters();
    public MetricsCollector MetricsCollector { get; }

    public LocatorAndPopulator(IRefCache inMemoryRefCache, IFileHashCache inMemoryFileHashCache,
        MetricsCollector metricsMetricsCollector)
    {
        _inMemoryRefCache = inMemoryRefCache;
        _inMemoryFileHashCache = inMemoryFileHashCache;
        MetricsCollector = metricsMetricsCollector;
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
            MetricsCollector.EndLocateTask(_locateResult, _counters);
            return _locateResult;
        }

        (_config, _cache, _loggingRefCache, _combinedRefCache, _combinedFileHashCache, _rawFileHashCache, _hasher) = CreateCaches(inputs.ConfigPath, logTime);
        MetricsCollector.RefCacheStats = _loggingRefCache.Stats;
        MetricsCollector.CombinedRefCacheStats = _combinedRefCache.Stats;
        MetricsCollector.FileHashCacheStats = _rawFileHashCache.Stats;
        MetricsCollector.CombinedFileHashCacheStats = _combinedFileHashCache.Stats;
        
        MetricsCollector.StartLocateTask();
        
        _assemblyName = inputs.AssemblyName;
        _localInputs = CalculateLocalInputs(logTime);
        _extract = _localInputs.ToSlim().ToFullExtract();
        var extractBytes = JsonSerializerExt.SerializeToUtf8Bytes(_extract, _counters?.JsonSerialise, FullExtractJsonContext.Default.FullExtract);
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

        MetricsCollector.EndLocateTask(_locateResult, _counters);
        
        return _locateResult;
    }

    private Task _locateRefasmWarmupTask;
    
    private LocalInputs CalculateLocalInputs(Action<string>? logTime = null) =>
        CalculateLocalInputs(_decomposed, _combinedRefCache, _assemblyName, _config.RefTrimming, _combinedFileHashCache, _hasher, _counters,
            logTime);

    public sealed class Counters
    {
        public TimeCounter ProcessFile { get; } = new TimeCounter();
        public TimeCounter ProcessReferenceC { get; } = new TimeCounter();
        public TimeCounter ReadFile { get; } = new TimeCounter();
        public TimeCounter Refasm { get; } = new TimeCounter();
        public TimeCounter BuildOutputs { get; } = new TimeCounter();
        public TimeCounter JsonSerialise { get; } = new TimeCounter();
        public TimeCounter HashBytes { get; } = new TimeCounter();
    }
    
    public static async Task<InputResult> ProcessSourceFile(string relativePath, IFileHashCache fileHashCache,
        IHash hasher, Counters? counters = null)
    {
        var sw = Stopwatch.StartNew();
        using var activity = Tracing.Source.StartActivity("ProcessSourceFile");
        activity?.SetTag("relativePath", relativePath);
        // Open the file for reading, and don't allow anyone to modify it.
        var info = new FileInfo(relativePath);
        var f = info.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        var fileHashCacheKey = FileHashCacheKey.FromFileInfo(info);
        var hash = await fileHashCache.GetAsync(fileHashCacheKey);
        if (hash == null)
        {
            var bytes = await ReadFileAsync(fileHashCacheKey.FullName, counters);
            hash = Utils.BytesToHash(bytes, hasher);
            await fileHashCache.SetAsync(fileHashCacheKey, hash);
        }

        var localFileExtract = new LocalFileExtract(fileHashCacheKey, hash);
        counters?.ProcessFile.Add(sw.Elapsed);
        return new InputResult(f, localFileExtract);
    }

    public static Task<byte[]> ReadFileAsync(string path, Counters? counters)
    {
        var sw = Stopwatch.StartNew();
        using var activity = Tracing.Source.StartActivity("ReadFileAsync");
        activity?.SetTag("path", path);
        var res = File.ReadAllBytesAsync(path);
        counters?.ReadFile.Add(sw.Elapsed);
        return res;
    }

    public static async Task<InputResult> ProcessReference(string relativePath, string compilingAssemblyName,
        IFileHashCache fileHashCache, IRefCache refCache, RefTrimmingConfig refTrimmingConfig, IHash hasher,
        Counters? counters = null)
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
            bytes ??= await ReadFileAsync(fileHashCacheKey.FullName, counters);
            hash = Utils.BytesToHash(bytes, hasher);
            await fileHashCache.SetAsync(fileHashCacheKey, hash);
        }

        var referenceDllName = Path.GetFileNameWithoutExtension(fileHashCacheKey.FullName);
        var originalExtract = new LocalFileExtract(fileHashCacheKey, hash);

        var refCacheKey = BuildRefCacheKey(referenceDllName, hash);
        var cached = await refCache.GetAsync(refCacheKey);
        if (cached == null)
        {
            bytes ??= await ReadFileAsync(fileHashCacheKey.FullName, counters);

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
        Counters? counters = null,
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
            fileInputs.Select(f => (Func<InputResult>)(() => ProcessSourceFile(f, fileHashCache, hasher).GetAwaiter().GetResult()));
        var refTasks = references.Select(r => (Func<InputResult>)(() =>
            ProcessReference(r, compilingAssemblyName, fileHashCache, refCache, trimmingConfig, hasher, counters).GetAwaiter().GetResult()));
        var allTaskFuncs = fileTasks.Concat(refTasks).ToArray();
        var allItems =
            allTaskFuncs
                .Chunk(50)
                .AsParallel()
                .AsOrdered()
                .WithDegreeOfParallelism(3)
                .SelectMany(taskFuncs => taskFuncs.Select(t => t()).ToArray())
                .ToArray();

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
        IFileHashCache fileHashCache, IHash hasher, Counters? counters = null)
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
            bytes ??= await ReadFileAsync(filepath, counters);
            hashString = Utils.BytesToHash(bytes, hasher);
            await fileHashCache.SetAsync(fileCacheKey, hashString);
        }

        var extract = new LocalFileExtract(fileCacheKey, hashString);
        var name = Path.GetFileNameWithoutExtension(fileInfo.Name);

        var cacheKey = BuildRefCacheKey(name, hashString);
        var cached = refCache.GetAsync(cacheKey).GetAwaiter().GetResult();
        if (cached == null)
        {
            bytes ??= await ReadFileAsync(filepath, counters);
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
            .Select(outputItem => GatherSingleOutputData(outputItem, _hasher, _counters)).ToArray());

        // Trigger RefTrimmer for newly-built dlls/exe files - should speed up builds of dependent projects,
        // and avoid any duplicate calculations due to multiple dependants redoing the same thing if timings are bad.

        async Task RefasmCompiledDllsAsync(OutputData[] outputs)
        {
            OutputData[] ToRefasm(OutputData[] outputs)
            {
                static bool IsDll(OutputData o) => Path.GetExtension(o.Item.LocalPath) is ".dll";
                bool IsRefAssemblyPath(string o) => o.ToLowerInvariant().Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p == "refint");
                bool IsRefAssemblyItem(OutputData o) => IsRefAssemblyPath(o.Item.LocalPath);
                var dlls = outputs.Where(IsDll).ToArray();
                var refints = outputs.Where(IsRefAssemblyItem).ToArray();
                if (refints.Length > 0)
                {
                    return refints;
                }
                else
                {
                    return dlls;
                }
            }

            var outputsToRefasm = ToRefasm(outputs);

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
        var outputZip = await BuildOutputsZip(tmpDir, outputs, allCompMetadata, _hasher, log, _counters);

        log.LogMessage(MessageImportance.Normal,
            $"CompilationCache - copying {_decomposed.OutputsToCache.Length} files from output to cache");
        //logTime?.Invoke("Outputs zip created");
        await _cache.SetAsync(_locateResult.CacheKey!.Value, _extract, outputZip);
        //logTime?.Invoke("cache entry set");

        await refasmCompiledDllsTask;

        return new UseOrPopulateResult(CachePopulated: true);
    }

    internal static async Task<OutputData> GatherSingleOutputData(OutputItem outputItem, IHash hasher,
        Counters? counters = null)
    {
        await using var f = File.Open(outputItem.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = await ReadFileAsync(outputItem.LocalPath, counters);
        var bytesHash = hasher.ComputeHash(bytes);
        var bytesHashString = Utils.BytesToHash(bytesHash, hasher);
        return new OutputData(outputItem, bytes, bytesHash, bytesHashString);
    }

    private async ValueTask RefasmAndPopulateCacheWithOutputDll(OutputData dll)
    {
        var sw = Stopwatch.StartNew();
        var trimmer = new RefTrimmer(_hasher);
        var toBeCached = await trimmer.GenerateRefData(ImmutableArray.Create(dll.Content));
        var fileCacheKey = FileHashCacheKey.FromFileInfo(new FileInfo(dll.Item.LocalPath));
        var dllHash = await _combinedFileHashCache.GetAsync(fileCacheKey);
        if (dllHash == null)
        {
            dllHash = Utils.BytesToHash(dll.Content, _hasher);
            await _combinedFileHashCache.SetAsync(fileCacheKey, dllHash);
        }

        var extract = new LocalFileExtract(fileCacheKey, dllHash);
        var name = Path.GetFileNameWithoutExtension(fileCacheKey.FullName);
        var cacheKey = BuildRefCacheKey(name, dllHash);
        var cached = new RefDataWithOriginalExtract(Ref: toBeCached, Original: extract);
        var res = await _combinedRefCache.SetAsync(cacheKey, cached);
        _counters?.Refasm.Add(sw.Elapsed);
    }

    record ArchiveEntry(string Name, byte[] Bytes);

    public static async Task<FileInfo> BuildOutputsZip(DirectoryInfo baseTmpDir, OutputData[] items,
        AllCompilationMetadata metadata, IHash hasher,
        TaskLoggingHelper? log = null, Counters? counters = null)
    {
        var sw = Stopwatch.StartNew();
        // Write inputs json in parallel to the rest
        var saveInputsTask = Task.Run(
            () => JsonSerializerExt.SerializeToUtf8Bytes(metadata, counters?.JsonSerialise,
                AllCompilationMetadataJsonContext.Default.AllCompilationMetadata));
        var objectToHash = metadata.LocalInputs.ToFullExtract();
        var extractBytes =
            JsonSerializerExt.SerializeToUtf8Bytes(objectToHash, counters?.JsonSerialise, FullExtractJsonContext.Default.FullExtract);
        var hashForFileName = Utils.BytesToHash(extractBytes, hasher);

        var outputExtracts = items.Select(i => new FileExtract(i.Item.Name, i.BytesHashString, i.Length)).ToArray();
        byte[] outputsJsonBytes =
            JsonSerializerExt.SerializeToUtf8Bytes(outputExtracts, counters?.JsonSerialise, FileExtractsJsonContext.Default.FileExtractArray);

        // Make sure inputs json written before zipping the whole directory
        var inputsJsonBytes = await saveInputsTask;

        var metaEntries = new[]
        {
            new ArchiveEntry("__inputs.json", inputsJsonBytes),
            new ArchiveEntry("__outputs.json", outputsJsonBytes),
        };
        var outputsEntries = items.Select(o => new ArchiveEntry(o.Item.CacheFileName, o.Content)).ToArray();
        var archiveEntries = metaEntries.Concat(outputsEntries).ToArray();

        var res = await BuildZip();
        counters?.BuildOutputs.Add(sw.Elapsed);
        return res;

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
            MetricsCollector.EndPopulateTask();
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
            _localInputs = null;
        }
        
        _locateRefasmWarmupTask?.GetAwaiter().GetResult();
        
        MetricsCollector.Collect();
    }
}

public static class JsonSerializerExt
{
    public static byte[] SerializeToUtf8Bytes<T>(T value, TimeCounter? counter = null, JsonTypeInfo<T>? options = null)
    {
        var sw = Stopwatch.StartNew();
        var res = JsonSerializer.SerializeToUtf8Bytes(value, options);
        counter?.Add(sw.Elapsed);
        return res;
    }
}
