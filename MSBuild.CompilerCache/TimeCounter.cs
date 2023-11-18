namespace MSBuild.CompilerCache;

public class TimeCounter
{
    private double _totalMilliseconds = 0;
    private readonly object _lock = new object();
    
    public void Add(TimeSpan ts)
    {
        lock (_lock)
        {
            _totalMilliseconds += ts.TotalMilliseconds;
            Count++;
        }
    }

    public TimeSpan Total
    {
        get
        {
            lock (_lock)
            {
                return TimeSpan.FromMilliseconds(_totalMilliseconds);
            }
        }
    }
    
    public int Count { get; private set; } = 0;
}