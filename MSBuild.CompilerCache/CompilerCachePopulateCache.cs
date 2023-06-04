using System.Diagnostics.CodeAnalysis;

namespace MSBuild.CompilerCache;

using Microsoft.Build.Framework;

// ReSharper disable once UnusedType.Global
/// <summary>
/// Either use results from an existing cache entry, or populate it with newly compiled outputs.
/// </summary>
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public class CompilerCachePopulateCache : BaseTask
{
#pragma warning disable CS8618
    [Required] public string Guid { get; set; }
    public bool CheckCompileOutputAgainstCache { get; set; }
#pragma warning restore CS8618

    public override bool Execute()
    {
        var _locateResults =
            BuildEngine4.GetRegisteredTaskObject(Guid, RegisteredTaskObjectLifetime.Build)
            ?? throw new Exception($"Could not find registered task object for cached results from the Locate task, using key {Guid}");
        var locateResults = _locateResults as LocateResult ??
                            throw new Exception("Cached result is of unexpected type");
        Log.LogWarning($"Use - cached LocateResult = {locateResults}");
        
        var inputs = new UseOrPopulateInputs(LocateResult: locateResults);
        var (config, cache, refCache) = Locator.CreateCaches(inputs.LocateResult.Inputs.ConfigPath);
        var populator = new Populator(cache, refCache);
        var results = populator.UseOrPopulate(inputs, Log, config.RefTrimming);
        return true;
    }
}