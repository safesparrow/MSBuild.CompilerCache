using System.Collections;
using Microsoft.Build.Utilities;

namespace MSBuild.CompilerCache;

using Microsoft.Build.Framework;
using JetBrains.Annotations;
// ReSharper disable once UnusedType.Global
/// <summary>
/// Hashes compilation inputs and checks if a cache entry with the given hash exists. 
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class CompilerCacheLocate : BaseTask
{
#pragma warning disable CS8618
    [UsedImplicitly(ImplicitUseKindFlags.Access)] [Output] public bool RunCompilation { get; private set; }
    [UsedImplicitly(ImplicitUseKindFlags.Access)] [Output] public bool PopulateCache { get; private set; }
    [UsedImplicitly(ImplicitUseKindFlags.Access)] [Output] public string Guid { get; private set; }
    internal LocateResult LocateResult { get; private set; }
#pragma warning restore CS8618
    
    public override bool Execute()
    {
        var guid = System.Guid.NewGuid();
        var locator = new Locator();
        var inputs = GatherInputs();
        var results = locator.Locate(inputs, Log);
        BuildEngine4.RegisterTaskObject(guid.ToString(), results, RegisteredTaskObjectLifetime.Build, false);
        Log.LogWarning($"Locate - registered result at Guid key {guid}");

        RunCompilation = results.RunCompilation;
        PopulateCache = results.PopulateCache;
        Guid = guid.ToString();
        LocateResult = results;
        
        return true;
    }

    [Required] public string ConfigPath { get; set; } = null!;
    [Required] public string ProjectFullPath { get; set; } = null!;
    [Required] public string AssemblyName { get; set; } = null!;
    [Required] public ITaskItem AllCompilerProperties { get; set; }

    protected BaseTaskInputs GatherInputs()
    {
        var typedAllCompilerProps = GetTypedAllCompilerProps(AllCompilerProperties);
        return new(
            ProjectFullPath: ProjectFullPath ??
                             throw new ArgumentException($"{nameof(ProjectFullPath)} cannot be null"),
            AssemblyName: AssemblyName,
            AllProps: typedAllCompilerProps,
            ConfigPath: ConfigPath ?? throw new ArgumentException($"{nameof(ConfigPath)} cannot be null")
        );
    }

    public static IDictionary<string, string> GetTypedAllCompilerProps(ITaskItem allTaskItem)
    {
        var _copy = allTaskItem.CloneCustomMetadata();
        return _copy as IDictionary<string, string> ?? throw new Exception(
            $"Expected the 'All' item's metadata to be IDictionary<string, string>, but was {_copy.GetType().FullName}");
    }

    public void SetInputs(BaseTaskInputs inputs)
    {
        ConfigPath = inputs.ConfigPath;
        ProjectFullPath = inputs.ProjectFullPath;
        AllCompilerProperties = new TaskItem("__nonexistent__", (IDictionary)inputs.AllProps);
    }
}