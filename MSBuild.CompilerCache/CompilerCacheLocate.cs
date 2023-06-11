using System.Collections;
using System.Diagnostics;
using Microsoft.Build.Utilities;

namespace MSBuild.CompilerCache;

using Microsoft.Build.Framework;
using JetBrains.Annotations;

// ReSharper disable once UnusedType.Global
/// <summary>
/// Task that calculates compilation inputs and uses cached outputs if they exist. 
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class CompilerCacheLocate : Microsoft.Build.Utilities.Task
{
#pragma warning disable CS8618
    [UsedImplicitly(ImplicitUseKindFlags.Assign)] [Required] public string ConfigPath { get; set; } = null!;
    [UsedImplicitly(ImplicitUseKindFlags.Assign)] [Required] public string ProjectFullPath { get; set; } = null!;
    [UsedImplicitly(ImplicitUseKindFlags.Assign)] [Required] public string AssemblyName { get; set; } = null!;
    [UsedImplicitly(ImplicitUseKindFlags.Assign)] [Required] public ITaskItem AllCompilerProperties { get; set; }
    
    [UsedImplicitly(ImplicitUseKindFlags.Access)] [Output] public bool RunCompilation { get; private set; }
    [UsedImplicitly(ImplicitUseKindFlags.Access)] [Output] public bool PopulateCache { get; private set; }
    [UsedImplicitly(ImplicitUseKindFlags.Access)] [Output] public string Guid { get; private set; }
    /// <summary> Accessed in tests </summary>
    internal LocateResult LocateResult { get; private set; }
#pragma warning restore CS8618

    private InMemoryRefCache GetInMemoryRefCache()
    {
        var key = "CompilerCache_InMemoryRefCache";
        if (BuildEngine9.GetRegisteredTaskObject(key, RegisteredTaskObjectLifetime.Build) is InMemoryRefCache cached)
        {
            return cached;
        }
        else
        {
            var fresh = new InMemoryRefCache();
            BuildEngine9.RegisterTaskObject(key, fresh, RegisteredTaskObjectLifetime.Build, false);
            return fresh;
        }
    }
    
    public override bool Execute()
    {
        var sw = Stopwatch.StartNew();
        var guid = System.Guid.NewGuid();
        var inMemoryRefCache = GetInMemoryRefCache();
        var locator = new LocatorAndPopulator(inMemoryRefCache);
        var inputs = GatherInputs();
        void LogTime(string name) => Log.LogWarning($"[{sw.ElapsedMilliseconds}ms] {name}");
        var results = locator.Locate(inputs, Log, LogTime);
        if (results.PopulateCache)
        {
            Guid = guid.ToString();
            BuildEngine4.RegisterTaskObject(guid.ToString(), locator, RegisteredTaskObjectLifetime.Build, false);
            Log.LogWarning($"Locate - registered {nameof(LocatorAndPopulator)} object at Guid key {guid}");
        }
        RunCompilation = results.RunCompilation;
        PopulateCache = results.PopulateCache;
        LocateResult = results;
        
        return true;
    }

    protected LocateInputs GatherInputs()
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

    private static IDictionary<string, string> GetTypedAllCompilerProps(ITaskItem allTaskItem)
    {
        var _copy = allTaskItem.CloneCustomMetadata();
        return _copy as IDictionary<string, string> ?? throw new Exception(
            $"Expected the 'All' item's metadata to be IDictionary<string, string>, but was {_copy.GetType().FullName}");
    }

    public void SetInputs(LocateInputs inputs)
    {
        ConfigPath = inputs.ConfigPath;
        ProjectFullPath = inputs.ProjectFullPath;
        AllCompilerProperties = new TaskItem("__nonexistent__", (IDictionary)inputs.AllProps);
    }
}