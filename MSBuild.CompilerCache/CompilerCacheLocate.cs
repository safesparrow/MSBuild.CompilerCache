﻿#if false && RELEASE
#define OTEL
#endif

using System.Collections;
using System.Diagnostics;
using System.Runtime;
using System.Text;
using System.Text.Json.Serialization;
using JetBrains.Annotations;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
#if OTEL
using OpenTelemetry;
using OpenTelemetry.Trace;
#endif
using Task = Microsoft.Build.Utilities.Task;

namespace MSBuild.CompilerCache;

using IFileHashCache = ICacheBase<FileHashCacheKey, string>;

public record JitMetrics(double CompilationTimeMs, long MethodCount, long CompiledILBytes)
{
    public static JitMetrics CreateFromCurrentState() => new JitMetrics(JitInfo.GetCompilationTime().TotalMilliseconds, JitInfo.GetCompiledMethodCount(), JitInfo.GetCompiledILBytes());
     
    public JitMetrics Subtract(JitMetrics other) => new JitMetrics(CompilationTimeMs - other.CompilationTimeMs, MethodCount - other.MethodCount, CompiledILBytes - other.CompiledILBytes);

    public JitMetrics Add(JitMetrics otherJit) => new JitMetrics(CompilationTimeMs + otherJit.CompilationTimeMs, MethodCount + otherJit.MethodCount, CompiledILBytes + otherJit.CompiledILBytes);
}

public record GCStats(long AllocatedBytes)
{
    public static GCStats CreateFromCurrentState() => new(GC.GetTotalAllocatedBytes());
    public GCStats Subtract(GCStats other) => new GCStats(AllocatedBytes - other.AllocatedBytes);

    public GCStats Add(GCStats otherGc) => new(AllocatedBytes + otherGc.AllocatedBytes);
}

public record GenericMetrics(JitGcMetrics JitGc, StartEnd Times);

public class GenericMetricsCreator
{
    private readonly (DateTime, Stopwatch) _start = StartEnd.Init();
    private readonly JitGcMetrics _startJitGc = JitGcMetrics.FromCurrentState();
    
    public GenericMetrics Create() => new GenericMetrics(_startJitGc.DiffWithCurrentState(), StartEnd.CreateFromStart(_start));
}

public record StartEnd(DateTime Start, DateTime End, TimeSpan Duration)
{
    public static StartEnd CreateFromStart((DateTime start, Stopwatch sw) x) => new(x.start, DateTime.Now, x.sw.Elapsed);
    public static (DateTime, Stopwatch) Init() => (DateTime.Now, Stopwatch.StartNew());
}

public record LocateMetrics(GenericMetrics Generic, LocateOutcome Outcome);
public record PopulateMetrics(GenericMetrics Generic);

/// <summary>
/// Per-project metrics used for cache efficiency & efficiency analysis.
/// </summary>
public class CompilationMetrics
{
    public string ProjectFullPath { get; set; }
    public TimeSpan TotalTime => Locate.Generic.Times.Duration + (Populate?.Generic.Times.Duration ?? TimeSpan.Zero);
    public CacheIncStats CombinedRefCacheStats { get; set; } 
    public CacheIncStats RefCacheStats { get; set; } 
    public CacheIncStats CombinedFileHashCacheStats { get; set; } 
    public CacheIncStats FileHashCacheStats { get; set; }
    public LocateMetrics Locate { get; set; }
    public PopulateMetrics Populate { get; set; }
    public LocatorAndPopulator.Counters Counters { get; set; }
    public CPUProcessMetrics CPU { get; set; }
}

[JsonSerializable(typeof(CompilationMetrics))]
[JsonSourceGenerationOptions(WriteIndented = false)]
public partial class CompilationMetricsJsonContext : JsonSerializerContext;

public record CPUProcessMetrics(TimeSpan TotalCpuTime, TimeSpan UserCpuTime, TimeSpan PrivilegedCpuTime)
{
    public static CPUProcessMetrics CreateFromCurrentState()
    {
        var process = Process.GetCurrentProcess();
        return new CPUProcessMetrics(process.TotalProcessorTime, process.UserProcessorTime, process.PrivilegedProcessorTime);
    }

    public CPUProcessMetrics Subtract(CPUProcessMetrics other) => new CPUProcessMetrics(TotalCpuTime - other.TotalCpuTime, UserCpuTime - other.UserCpuTime, PrivilegedCpuTime - other.PrivilegedCpuTime);
    public CPUProcessMetrics Add(CPUProcessMetrics otherCpu) => new CPUProcessMetrics(TotalCpuTime + otherCpu.TotalCpuTime, UserCpuTime + otherCpu.UserCpuTime, PrivilegedCpuTime + otherCpu.PrivilegedCpuTime);
}

public class MetricsCollector
{
    private GenericMetricsCreator _locateStart;
    private LocateMetrics _locate;
    private GenericMetricsCreator _populateStart;
    private PopulateMetrics _populate;
    private LocatorAndPopulator.Counters _counters;
    private LocateResult _locateResult;
    private CPUProcessMetrics _cpuStart;

    public CacheIncStats RefCacheStats { get; set; } = null;
    public CacheIncStats CombinedRefCacheStats { get; set; } = null;
    public CacheIncStats FileHashCacheStats { get; set; } = null;
    public CacheIncStats CombinedFileHashCacheStats { get; set; } = null;

    public void StartLocateTask()
    {
        _locateStart = new GenericMetricsCreator();
        _cpuStart = CPUProcessMetrics.CreateFromCurrentState();
    }

    public void EndLocateTask(LocateResult locateResult, LocatorAndPopulator.Counters counters)
    {
        _locate = new LocateMetrics(_locateStart.Create(), locateResult.Outcome);
        _locateResult = locateResult;
        _counters = counters;
    }

    public void StartPopulateTask()
    {
        _populateStart = new GenericMetricsCreator();
    }

    public void EndPopulateTask()
    {
        _populate = new PopulateMetrics(_populateStart.Create());
    }

    public void Collect()
    {
        var m = new CompilationMetrics
        {
            ProjectFullPath = _locateResult.Inputs?.ProjectFullPath,
            RefCacheStats = RefCacheStats,
            FileHashCacheStats = FileHashCacheStats,
            Counters = _counters,
            CombinedRefCacheStats = CombinedRefCacheStats,
            CombinedFileHashCacheStats = CombinedFileHashCacheStats,
            Locate = _locate,
            Populate = _populate,
            CPU = CPUProcessMetrics.CreateFromCurrentState().Subtract(_cpuStart)
        };
        var bytes = JsonSerializerExt.SerializeToUtf8Bytes(m, null, CompilationMetricsJsonContext.Default.CompilationMetrics);
        FileHashCache.IOActionWithRetries(() =>
        {
            var path = $"c:/projekty/MSBuild.CompilerCache/metrics.jsonl";
            using var fs = File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            fs.Write(bytes);
            fs.Write(Encoding.ASCII.GetBytes(Environment.NewLine));
            return 0;
        });
    }
}

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

    private InMemoryCaches GetInMemoryCaches()
    {
        using var activity = Tracing.StartWithMetrics("GetInMemoryCaches");
        var key = "CompilerCache_InMemoryRefCache";
        if (BuildEngine9.GetRegisteredTaskObject(key, RegisteredTaskObjectLifetime.Build) is InMemoryCaches cached)
        {
            return cached;
        }
        using var activity2 = Tracing.Source.StartActivity("GetInMemoryCachesInner");
        var fresh = new InMemoryCaches(new InMemoryRefCache(), new DictionaryBasedCache<FileHashCacheKey, string>());
        BuildEngine9.RegisterTaskObject(key, fresh, RegisteredTaskObjectLifetime.Build, false);
        return fresh;
    }

    internal static IDisposable? SetupOtelIfEnabled()
    {
        #if OTEL
        return Sdk.CreateTracerProviderBuilder()
                .AddSource(Tracing.ServiceName)
                .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317"))
                .AddConsoleExporter()
                .Build();
        #else
            return null;
        #endif
    }
    
    public override bool Execute()
    {
        var collector = new MetricsCollector();
        collector.StartLocateTask();
#if OTEL
        using var otel = SetupOtelIfEnabled();
#endif
        using var activity = Tracing.StartWithMetrics("CompilerCacheLocate");
        var guid = System.Guid.NewGuid();
        activity?.SetTag("guid", guid);
        activity?.SetTag("assemblyName", AssemblyName);
        var sw = Stopwatch.StartNew();
        var (inMemoryRefCache, fileHashCache) = GetInMemoryCaches();
        var locator = new LocatorAndPopulator(inMemoryRefCache, fileHashCache, collector);
        
        var inputs = GatherInputs();
        void LogTime(string name) => Log.LogMessage($"[{sw.ElapsedMilliseconds}ms] {name}");
        var results = locator.Locate(inputs, Log, LogTime);
        if (results.PopulateCache)
        {
            Guid = guid.ToString();
            BuildEngine4.RegisterTaskObject(guid.ToString(), locator, RegisteredTaskObjectLifetime.Build, false);
            Log.LogMessage($"Locate - registered {nameof(LocatorAndPopulator)} object at Guid key {guid}");
        }
        else
        {
            locator.Dispose();
        }
        RunCompilation = results.RunCompilation;
        PopulateCache = results.PopulateCache;
        LocateResult = results;
        
        return true;
    }

    protected LocateInputs GatherInputs()
    {
        using var activity = Tracing.Source.StartActivity("GatherInputs");
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