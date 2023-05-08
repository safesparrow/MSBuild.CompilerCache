﻿namespace MSBuild.CompilerCache;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

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
    [Output] public bool RunCompilation { get; private set; }
    [Output] public bool CacheSupported { get; set; }
    [Output] public bool CacheHit { get; private set; }
    [Output] public string CacheKey { get; private set; }
    [Output] public string LocalInputsHash { get; set; }
    [Output] public string PreCompilationTimeTicks { get; private set; }
    // ReSharper restore UnusedAutoPropertyAccessor.Global
#pragma warning restore CS8618

    public LocateCompilationCacheEntry()
    {
        _locator = new Locator();
    }
    
    public override bool Execute()
    {
        var inputs = GatherInputs();
        var results = _locator.Locate(inputs, Log);

        CacheSupported = results.CacheSupported;
        RunCompilation = results.RunCompilation;
        CacheHit = results.CacheHit;
        CacheKey = results.CacheKey;
        LocalInputsHash = results.LocalInputsHash;
        PreCompilationTimeTicks = results.PreCompilationTimeUtc.Ticks.ToString();
        
        return true;
    }

    public LocateResult LocateResult => new LocateResult(
        RunCompilation: RunCompilation,
        CacheSupported: CacheSupported,
        CacheHit: CacheHit,
        CacheKey: new CacheKey(CacheKey),
        LocalInputsHash: LocalInputsHash,
        PreCompilationTimeUtc: new DateTime(long.Parse(PreCompilationTimeTicks), DateTimeKind.Utc)
    );
}

public class TestTask : Task
{
    public static int X;
    
    [Required]
    public ITaskItem All { get; set; }
    
    public override bool Execute()
    {
        log($"X={X}");
        X++;
        var _copy = All.CloneCustomMetadata();
        var copy = _copy as IDictionary<string, string> ?? throw new Exception($"Expected the 'All' item's metadata to be IDictionary<string, string>, but was {_copy.GetType().FullName}");
        
        void log(string msg) => Log.LogMessage(MessageImportance.High, msg);
        log($"all={All.ItemSpec}");
        foreach (var (key, value) in copy)
        {
            log($"{key}= [{value?.GetType().FullName}] {value} = {string.Join("^", value?.Split(";"))}");
        }
        
        return true;
    }
}