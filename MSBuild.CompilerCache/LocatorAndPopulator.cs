using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis.FlowAnalysis;
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
    private readonly InMemoryRefCache _inMemoryRefCache;

    private (Config config, ICache Cache, IRefCache RefCache) CreateCaches(string configPath,
        Action<string>? logTime = null)
    {
        using var fs = File.OpenRead(configPath);
        var config = JsonSerializer.Deserialize<Config>(fs);
        //logTime?.Invoke("Config deserialized");
        var cache = new Cache(config.CacheDir);
        var refCache = new RefCache(config.InferRefCacheDir(), _inMemoryRefCache);
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
    private FileHashCache _fileHashCache;

    public LocatorAndPopulator(InMemoryRefCache? inMemoryRefCache = null, FileHashCache? fileHashCache = null)
    {
        _inMemoryRefCache = inMemoryRefCache ?? new InMemoryRefCache();
        _fileHashCache = fileHashCache ?? new FileHashCache();
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
        CalculateLocalInputs(_decomposed, _refCache, _assemblyName, _config.RefTrimming, _fileHashCache, logTime);

    internal static LocalInputs CalculateLocalInputs(DecomposedCompilerProps decomposed, IRefCache refCache,
        string assemblyName, RefTrimmingConfig trimmingConfig, IFileHashCache fileHashCache, Action<string>? logTime = null)
    {
        var nonReferenceFileInputs = decomposed.FileInputs;
        var fileInputs = trimmingConfig.Enabled
            ? nonReferenceFileInputs
            : nonReferenceFileInputs.Concat(decomposed.References).ToArray();
        var fileExtracts =
            fileInputs
                .Chunk(4)
                // .AsParallel()
                // .AsOrdered()
                // .WithDegreeOfParallelism(Math.Max(2, Environment.ProcessorCount / 2))
                // Preserve original order of files
                .SelectMany(paths => paths.Select(GetLocalFileExtract))
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
                        var allRefData = GetAllRefData(dll, refCache, fileHashCache);
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

    public record struct FileCacheKey(string FullName, long Length, DateTime LastWriteTimeUtc);

    public interface ICacheBase<TKey, TValue>
    {
        bool Exists(TKey key);
        TValue Get(TKey key);
        bool Set(TKey key, TValue value);
    }

    public class CacheCombiner<TKey, TValue> : ICacheBase<TKey, TValue>
    {
        private readonly ICacheBase<TKey, TValue> _cache1;
        private readonly ICacheBase<TKey, TValue> _cache2;

        public CacheCombiner(ICacheBase<TKey, TValue> cache1, ICacheBase<TKey, TValue> cache2)
        {
            _cache1 = cache1;
            _cache2 = cache2;
        }


        public bool Exists(TKey key) => _cache1.Exists(key) || _cache2.Exists(key);

        public TValue Get(TKey key) => _cache1.Get(key) ?? _cache2.Get(key);

        public bool Set(TKey key, TValue value)
        {
            if (_cache1.Set(key, value))
            {
                _cache2.Set(key, value);
                return true;
            }
            else
            {
                return false;
            }
        }
    }
    
    public interface IFileHashCache : ICacheBase<FileCacheKey, string> { }

    public class FSFileHashCache : IFileHashCache
    {
        private readonly string _cacheDir;

        public FSFileHashCache(string cacheDir)
        {
            _cacheDir = cacheDir;
            Directory.CreateDirectory(_cacheDir);
        }

        public string EntryPath(CacheKey key) => Path.Combine(_cacheDir, $"{key.Key}");

        public CacheKey ExtractKey(FileCacheKey key) => new(Utils.ObjectToSHA256Hex(key));

        public bool Exists(FileCacheKey rawKey)
        {
            var key = ExtractKey(rawKey);
            var entryPath = EntryPath(key);
            return File.Exists(entryPath);
        }

        public string Get(FileCacheKey rawKey)
        {
            var key = ExtractKey(rawKey);
            var entryPath = EntryPath(key);
            if (File.Exists(entryPath))
            {
                string Read() => File.ReadAllText(entryPath);
                return IOActionWithRetries(Read);
            }
            else
            {
                return null;
            }
        }

        private static T IOActionWithRetries<T>(Func<T> action)
        {
            var attempts = 5;
            var retryDelay = 50;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    return action();
                }
                catch(IOException e) 
                {
                    if (attempt < attempts)
                    {
                        var delay = (int)(Math.Pow(2, attempt-1) * retryDelay);
                        Thread.Sleep(delay);
                    }
                    else
                    {
                        throw;
                    }
                }
            }

            throw new InvalidOperationException("Unexpected code location reached");
        }

        public bool Set(FileCacheKey originalKey, string value)
        {
            var key = ExtractKey(originalKey);
            var entryPath = EntryPath(key);
            if (File.Exists(entryPath))
            {
                return false;
            }

            using var tmpFile = new TempFile();
            {
                using var fs = tmpFile.File.OpenWrite();
                using var sw = new StreamWriter(fs, Encoding.UTF8);
                sw.WriteLine(value);
            }
            Cache.AtomicCopy(tmpFile.FullName, entryPath, throwIfDestinationExists: false);
        }
    }

    public class FileHashCache : IFileHashCache
    {
        private ConcurrentDictionary<FileCacheKey, string> _cache = new ConcurrentDictionary<FileCacheKey, string>();

        public bool Exists(FileCacheKey key) => _cache.ContainsKey(key);

        public string Get(FileCacheKey key)
        {
            _cache.TryGetValue(key, out var hash);
            return hash;
        }

        public void Set(FileCacheKey key, string value) => _cache.TryAdd(key, value);
    }
    
    internal static AllRefData GetAllRefData(string filepath, IRefCache refCache, IFileHashCache fileHashCache)
    {
        var fileInfo = new FileInfo(filepath);
        if (!fileInfo.Exists)
        {
            throw new Exception($"Reference file does not exist: '{filepath}'");
        }

        var fileCacheKey = new FileCacheKey(fileInfo.FullName, fileInfo.Length, fileInfo.LastWriteTimeUtc);
        var hashString = fileHashCache.Get(fileCacheKey);
        ImmutableArray<byte> bytes = ImmutableArray<byte>.Empty;
        if (hashString == null)
        {
            bytes = ImmutableArray.Create(File.ReadAllBytes(filepath));
            hashString = Utils.BytesToSHA256Hex(bytes);
        }

        var extract = new LocalFileExtract(fileInfo.FullName, hashString, fileInfo.Length, null);
        var name = Path.GetFileNameWithoutExtension(fileInfo.Name);

        var cacheKey = BuildRefCacheKey(name, hashString);
        var cached = refCache.Get(cacheKey);
        if (cached == null)
        {
            if (bytes.IsDefaultOrEmpty)
            {
                bytes = ImmutableArray.Create(File.ReadAllBytes(filepath));
            }
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