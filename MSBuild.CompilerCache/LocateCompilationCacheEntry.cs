namespace MSBuild.CompilerCache;

using Microsoft.Build.Framework;

// ReSharper disable once UnusedType.Global
/// <summary>
/// Hashes compilation inputs and checks if a cache entry with the given hash exists. 
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class LocateCompilationCacheEntry : BaseTask
{
    private readonly Locator _locator;
#pragma warning disable CS8618
    // ReSharper disable UnusedAutoPropertyAccessor.Global
    [Output] public bool CacheHit { get; private set; }
    [Output] public string CacheKey { get; private set; }
    [Output] public string LocalInputsHash { get; set; }
    [Output] public DateTime PreCompilationTimeUtc { get; private set; }
    // ReSharper restore UnusedAutoPropertyAccessor.Global
#pragma warning restore CS8618

    public LocateCompilationCacheEntry()
    {
        _locator = new Locator();
    }
    
    public override bool Execute()
    {
        Log.LogMessage(MessageImportance.High, $"PropertyInputs={PropertyInputs}");
        var inputs = GatherInputs();
        var results = _locator.Locate(inputs, Log);

        CacheHit = results.CacheHit;
        CacheKey = results.CacheKey;
        LocalInputsHash = results.LocalInputsHash;
        PreCompilationTimeUtc = results.PreCompilationTimeUtc;
        
        return true;
    }

    public LocateResult LocateResult => new LocateResult(
        CacheHit: CacheHit,
        CacheKey: new CacheKey(CacheKey),
        LocalInputsHash: LocalInputsHash,
        PreCompilationTimeUtc: PreCompilationTimeUtc
    );
}