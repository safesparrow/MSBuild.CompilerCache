using System.Diagnostics.CodeAnalysis;

namespace MSBuild.CompilerCache;

using Microsoft.Build.Framework;

// ReSharper disable once UnusedType.Global
/// <summary>
/// Either use results from an existing cache entry, or populate it with newly compiled outputs.
/// Example usage: <UseOrPopulateCache OutputsToCache="@(CompileOutputsToCache)" CacheHit="$(CacheHit)" CacheDir="$(CacheDir)" IntermediateOutputPath="..." />
/// </summary>
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public class UseOrPopulateCache : BaseTask
{
#pragma warning disable CS8618
    [Required] public bool CacheHit { get; set; }
    [Required] public string CacheKey { get; set; }
    [Required] public string LocalInputsHash { get; set; }
    public bool CheckCompileOutputAgainstCache { get; set; }
#pragma warning restore CS8618

    public override bool Execute()
    {
        Log.LogMessage(MessageImportance.High, $"PropertyInputs={string.Join(",", PropertyInputs)}");
        var _userOrPopulator = new UserOrPopulator(new Cache(BaseCacheDir));
        var inputs = new UseOrPopulateInputs(
            CacheHit: CacheHit,
            CacheKey: new CacheKey(CacheKey),
            Inputs: GatherInputs(),
            LocatorLocalInputsHash: LocalInputsHash,
            CheckCompileOutputAgainstCache: CheckCompileOutputAgainstCache
        );
        var results = _userOrPopulator.UseOrPopulate(inputs, Log);
        return true;
    }

    public void SetAllInputs(UseOrPopulateInputs inputs)
    {
        CacheHit = inputs.CacheHit;
        CacheKey = inputs.CacheKey;
        LocalInputsHash = inputs.LocatorLocalInputsHash;
        CheckCompileOutputAgainstCache = inputs.CheckCompileOutputAgainstCache;
        base.SetInputs(inputs.Inputs);
    }
}