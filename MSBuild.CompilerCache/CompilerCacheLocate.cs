namespace MSBuild.CompilerCache;

using Microsoft.Build.Framework;

// ReSharper disable once UnusedType.Global
/// <summary>
/// Hashes compilation inputs and checks if a cache entry with the given hash exists. 
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class CompilerCacheLocate : BaseTask
{
#pragma warning disable CS8618
    // ReSharper disable UnusedAutoPropertyAccessor.Global
    [Output] public bool RunCompilation { get; private set; }
    [Output] public bool CacheSupported { get; set; }
    [Output] public bool CacheHit { get; private set; }
    [Output] public string CacheKey { get; private set; }
    [Output] public string LocalInputsHash { get; set; }
    [Output] public string PreCompilationTimeTicks { get; private set; }
    [Output] public string Guid { get; private set; }
    // ReSharper restore UnusedAutoPropertyAccessor.Global
#pragma warning restore CS8618
    
    public override bool Execute()
    {
        var guid = System.Guid.NewGuid();
        BuildEngine4.RegisterTaskObject("foo", $"bar_{guid}", RegisteredTaskObjectLifetime.Build, false);
        var bar = BuildEngine4.GetRegisteredTaskObject("foo", RegisteredTaskObjectLifetime.Build);
        Log.LogWarning($"Locate - Bar = {bar} ({bar?.GetType()}");
        
        var _locator = new Locator();
        var inputs = GatherInputs();
        var results = _locator.Locate(inputs, Log);

        CacheSupported = results.CacheSupported;
        RunCompilation = results.RunCompilation;
        CacheHit = results.CacheHit;
        CacheKey = results.CacheKey?.Key ?? null;
        LocalInputsHash = results.LocalInputsHash;
        PreCompilationTimeTicks = results.PreCompilationTimeUtc.Ticks.ToString();
        Guid = results.Guid.ToString();
        
        return true;
    }

    public LocateResult LocateResult => new LocateResult(
        RunCompilation: RunCompilation,
        CacheSupported: CacheSupported,
        CacheHit: CacheHit,
        CacheKey: new CacheKey(CacheKey),
        LocalInputsHash: LocalInputsHash,
        PreCompilationTimeUtc: new DateTime(long.Parse(PreCompilationTimeTicks), DateTimeKind.Utc),
        Guid: new Guid(Guid)
    );
}