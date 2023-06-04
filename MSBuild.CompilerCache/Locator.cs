using System.Collections.Immutable;
using System.IO.Compression;
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
    DateTime PreCompilationTimeUtc,
    Guid Guid
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
            PreCompilationTimeUtc: DateTime.MinValue,
            Guid: Guid.Empty
        );
    }
}

public class Locator
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

    public LocateResult Locate(BaseTaskInputs inputs, TaskLoggingHelper? log = null)
    {
        var guid = Guid.NewGuid();
        var preCompilationTimeUtc = DateTime.UtcNow;
        
        var decomposed = TargetsExtractionUtils.DecomposeCompilerProps(inputs.AllProps, log);
        if (decomposed.UnsupportedPropsSet.Any())
        {
            var s = string.Join(Environment.NewLine,
                decomposed.UnsupportedPropsSet.Select(nv => $"{nv.Name}={nv.Value}"));
            log?.LogMessage(MessageImportance.Normal, $"CompilationCache: Some unsupported MSBuild properties set - the cache will be disabled: {Environment.NewLine}{s}");
            return LocateResult.CreateNotSupported();
        }

        var (config, cache, refCache) = CreateCaches(inputs.ConfigPath);
        var assemblyName = inputs.AssemblyName;
        var localInputs = CalculateLocalInputs(decomposed, refCache, assemblyName, config.RefTrimming);
        var extract = localInputs.ToFullExtract();
        var hashString = Utils.ObjectToSHA256Hex(extract);
        var cacheKey = Populator.GenerateKey(inputs, hashString);
        var localInputsHash = Utils.ObjectToSHA256Hex(localInputs);

        bool cacheHit;

        if (config.CheckCompileOutputAgainstCache)
        {
            cacheHit = false;
        }
        else
        {
            var cachedOutputTmpZip = cache.Get(cacheKey);
            cacheHit = cachedOutputTmpZip != null;

            if (cacheHit)
            {
                var outputs = decomposed.OutputsToCache;
                log?.LogMessage(MessageImportance.Normal, $"CompilationCache: Locate for {cacheKey} was a hit. Copying {outputs.Length} files from cache");
                try
                {
                    UseCachedOutputs(cachedOutputTmpZip!, outputs, preCompilationTimeUtc);
                }
                finally
                {
                    File.Delete(cachedOutputTmpZip!);
                }
            }
            else
            {
                log?.LogMessage(MessageImportance.Normal, $"CompilationCache: Locate for {cacheKey} was a miss.");
            }
        }

        var runCompilation = !cacheHit || config.CheckCompileOutputAgainstCache;

        return new LocateResult(
            CacheSupported: true,
            CacheHit: cacheHit,
            RunCompilation: runCompilation,
            CacheKey: cacheKey,
            LocalInputsHash: localInputsHash,
            PreCompilationTimeUtc: preCompilationTimeUtc,
            Guid: guid
        );
    }

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

    public static void UseCachedOutputs(string outputsZipPath, OutputItem[] items, DateTime postCompilationTimeUtc)
    {
        var tempPath = Path.GetTempFileName();
        File.Copy(outputsZipPath, tempPath, overwrite: true);
        try
        {
            using var a = ZipFile.OpenRead(tempPath);
            foreach (var entry in a.Entries.Where(e => !e.Name.StartsWith("__")))
            {
                var outputItem =
                    items.SingleOrDefault(it => it.CacheFileName == entry.Name)
                    ?? throw new Exception($"Cached outputs contain an unknown file '{entry.Name}'.");
                entry.ExtractToFile(outputItem.LocalPath, overwrite: true);
                File.SetLastWriteTimeUtc(outputItem.LocalPath, postCompilationTimeUtc);
            }
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}