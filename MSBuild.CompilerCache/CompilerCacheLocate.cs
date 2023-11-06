using System.Collections;
using System.Diagnostics;
using JetBrains.Annotations;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Task = Microsoft.Build.Utilities.Task;

namespace MSBuild.CompilerCache;

using IFileHashCache = ICacheBase<FileHashCacheKey, string>;

// ReSharper disable once UnusedType.Global
/// <summary>
/// Task that calculates compilation inputs and uses cached outputs if they exist. 
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class CompilerCacheLocate : Task
{
#pragma warning disable CS8618
    // Inputs
    [UsedImplicitly(ImplicitUseKindFlags.Assign)] [Required] public string ConfigPath { get; set; } = null!;
    [UsedImplicitly(ImplicitUseKindFlags.Assign)] [Required] public string ProjectFullPath { get; set; } = null!;
    [UsedImplicitly(ImplicitUseKindFlags.Assign)] [Required] public string AssemblyName { get; set; } = null!;
    [UsedImplicitly(ImplicitUseKindFlags.Assign)] [Required] public ITaskItem AllCompilerProperties { get; set; }
    
    // Outputs
    [UsedImplicitly(ImplicitUseKindFlags.Access)] [Output] public bool RunCompilation { get; private set; }
    [UsedImplicitly(ImplicitUseKindFlags.Access)] [Output] public bool PopulateCache { get; private set; }
    [UsedImplicitly(ImplicitUseKindFlags.Access)] [Output] public string Guid { get; private set; }
    /// <summary> Accessed in tests </summary>
    internal LocateResult LocateResult { get; private set; }
#pragma warning restore CS8618

    private record InMemoryCaches(InMemoryRefCache InMemoryRefCache, IFileHashCache FileHashCache);

    private readonly object _refCacheLock = new object();
    
    private InMemoryCaches GetInMemoryCaches()
    {
        var key = "CompilerCache_InMemoryRefCache";
        lock (_refCacheLock)
        {
            if (BuildEngine9.GetRegisteredTaskObject(key, RegisteredTaskObjectLifetime.Build) is InMemoryCaches cached)
            {
                return cached;
            }

            var fresh = new InMemoryCaches(new InMemoryRefCache(), new DictionaryBasedCache<FileHashCacheKey, string>());
            BuildEngine9.RegisterTaskObject(key, fresh, RegisteredTaskObjectLifetime.Build, false);
            return fresh;
        }
    }
    
    public override bool Execute()
    {
        var sw = Stopwatch.StartNew();
        var guid = System.Guid.NewGuid();
        var (inMemoryRefCache, fileHashCache) = GetInMemoryCaches();
        var locator = new LocatorAndPopulator(inMemoryRefCache, fileHashCache);
        var inputs = GatherInputs();
        void LogTime(string name) => Log.LogMessage($"[{sw.ElapsedMilliseconds}ms] {name}");
        var results = locator.Locate(inputs, Log, LogTime);
        if (results.PopulateCache)
        {
            Guid = guid.ToString();
            BuildEngine4.RegisterTaskObject(guid.ToString(), locator, RegisteredTaskObjectLifetime.Build, false);
            Log.LogMessage($"Locate - registered {nameof(LocatorAndPopulator)} object at Guid key {guid}");
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
    
    /// <summary> Used in tests </summary>
    internal void SetInputs(LocateInputs inputs)
    {
        ConfigPath = inputs.ConfigPath;
        ProjectFullPath = inputs.ProjectFullPath;
        AllCompilerProperties = new TaskItem("__nonexistent__", (IDictionary)inputs.AllProps);
    }
}