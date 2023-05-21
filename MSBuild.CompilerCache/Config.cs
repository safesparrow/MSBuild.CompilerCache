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
    public string BaseCacheDir { get; set; }

    public RefTrimmingConfig RefTrimming { get; set; } = new RefTrimmingConfig();

    public string InferRefCacheDir() => RefTrimming.RefCacheDir ?? Path.Combine(BaseCacheDir, ".refcache");
    
    public bool CheckCompileOutputAgainstCache { get; set; }
}