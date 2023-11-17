namespace MSBuild.CompilerCache;

public record JitGcMetrics(JitMetrics Jit, GCStats Gc)
{
    public static JitGcMetrics FromCurrentState() => new JitGcMetrics(JitMetrics.CreateFromCurrentState(), GCStats.CreateFromCurrentState());
    public JitGcMetrics Subtract(JitGcMetrics other) => new JitGcMetrics(Jit.Subtract(other.Jit), Gc.Subtract(other.Gc));
    public JitGcMetrics Add(JitGcMetrics other) => new JitGcMetrics(Jit.Add(other.Jit), Gc.Add(other.Gc));
    public JitGcMetrics DiffWithCurrentState() => Subtract(FromCurrentState());
}