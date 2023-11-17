using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace MSBuild.CompilerCache;

// ReSharper disable once UnusedType.Global
/// <summary>
/// Task that populates the compilation cache with newly compiled outputs.
/// </summary>
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public class CompilerCachePopulateCache : Task
{
#pragma warning disable CS8618
    [Required] public string Guid { get; set; }
    [Required] public bool CompilationSucceeded { get; set; }
#pragma warning restore CS8618

    public override bool Execute()
    {
        using var otel = CompilerCacheLocate.SetupOtelIfEnabled();
        using var activity = Tracing.Source.StartActivity("CompilerCacheLocate");
        activity?.SetTag("guid", Guid);
        object _locator =
            BuildEngine4.UnregisterTaskObject(Guid, RegisteredTaskObjectLifetime.Build)
            ?? throw new Exception($"Could not find registered task object for {nameof(LocatorAndPopulator)} from the Locate task, using key {Guid}");
        var locator = _locator as LocatorAndPopulator ?? throw new Exception("Cached result is of unexpected type");
        locator.MetricsCollector.StartPopulateTask();
        var sw = Stopwatch.StartNew();
        void LogTime(string name) => Log.LogMessage($"[{sw.ElapsedMilliseconds}ms] {name}");
        UseOrPopulateResult result = locator.PopulateCacheOrJustDispose(Log, LogTime, CompilationSucceeded).GetAwaiter().GetResult();
        return true;
    }
}