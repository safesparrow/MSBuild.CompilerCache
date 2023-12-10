using System.Text.Json.Serialization;
using Microsoft.Build.Framework;

namespace MSBuild.CompilerCache;

public class RefTrimmingConfig
{
    public bool Enabled { get; set; } = true;

    public bool IgnoreInternalsIfPossible { get; set; } = true;
    
    public string? RefCacheDir { get; set; }
}

public class Config
{
    [Required]
    public string CacheDir { get; set; }

    public RefTrimmingConfig RefTrimming { get; set; } = new RefTrimmingConfig();

    public string InferRefCacheDir() => RefTrimming.RefCacheDir ?? Path.Combine(CacheDir, ".refcache");
    
    public string InferFileHashCacheDir() => Path.Combine(CacheDir, ".filehashcache");
    
    public bool CheckCompileOutputAgainstCache { get; set; }

    public HasherType Hasher { get; set; } = HasherType.XxHash64;
}


[JsonSerializable(typeof(Config))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class ConfigJsonContext : JsonSerializerContext;