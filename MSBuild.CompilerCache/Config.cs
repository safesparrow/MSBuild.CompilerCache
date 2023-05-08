using Microsoft.Build.Framework;

namespace MSBuild.CompilerCache;

public class Config
{
    [Required]
    public string BaseCacheDir { get; set; }
    
    public bool CheckCompileOutputAgainstCache { get; set; }
}