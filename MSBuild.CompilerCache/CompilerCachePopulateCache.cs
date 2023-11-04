using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace MSBuild.CompilerCache;

using Microsoft.Build.Framework;

// ReSharper disable once UnusedType.Global
/// <summary>
/// Task that populates the compilation cache with newly compiled outputs.
/// </summary>
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public class CompilerCachePopulateCache : Microsoft.Build.Utilities.Task
{
#pragma warning disable CS8618
    [Required] public string Guid { get; set; }
    public bool CheckCompileOutputAgainstCache { get; set; }
#pragma warning restore CS8618

    public override bool Execute()
    {
        var _locator =
            BuildEngine4.GetRegisteredTaskObject(Guid, RegisteredTaskObjectLifetime.Build)
            ?? throw new Exception($"Could not find registered task object for {nameof(LocatorAndPopulator)} from the Locate task, using key {Guid}");
        BuildEngine4.UnregisterTaskObject(Guid, RegisteredTaskObjectLifetime.Build);
        var locator = _locator as LocatorAndPopulator ??
                            throw new Exception("Cached result is of unexpected type");
        var sw = Stopwatch.StartNew();
        void LogTime(string name) => Log.LogMessage($"[{sw.ElapsedMilliseconds}ms] {name}");
        var results = locator.PopulateCache(Log, LogTime);
        return true;
    }
}