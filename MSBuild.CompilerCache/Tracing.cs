using System.Diagnostics;

namespace MSBuild.CompilerCache;

internal static class Tracing
{
    public  const string ServiceName = "MSBuild.CompilerCache";
    public static readonly ActivitySource Source =  new ActivitySource(ServiceName);

    public static Activity? Start(string name) => Source.StartActivity(name);
    
    public static ActivityWithMetrics? StartWithMetrics(string name)
    {
        var activity = Source.StartActivity(name);
        if (activity != null)
        {
            return new ActivityWithMetrics(activity);
        }
        else
        {
            return null;
        }
    }
}

public sealed class ActivityWithMetrics : IDisposable
{
    private readonly JitMetrics _jitStart;
    private readonly GCStats _gcStart;
    public Activity Activity { get; }

    public ActivityWithMetrics(Activity activity)
    {
        Activity = activity;
        _jitStart = JitMetrics.CreateFromCurrentState();
        _gcStart = GCStats.CreateFromCurrentState();
    }

    public void Dispose()
    {
        var jitEnd = JitMetrics.CreateFromCurrentState();
        var jit = jitEnd.Subtract(_jitStart);
        var gcEnd = GCStats.CreateFromCurrentState();
        Activity.SetTag("jit.compilationTime", jit.CompilationTimeMs);
        Activity.SetTag("jit.methodCount", jit.MethodCount);
        Activity.SetTag("jit.compiledILBytes", jit.CompiledILBytes);
        Activity.SetTag("gc.allocated", gcEnd.AllocatedBytes - _gcStart.AllocatedBytes);
        Activity.Dispose();
    }
    
    public void SetTag(string key, object value)
    {
        Activity.SetTag(key, value);
    }
}