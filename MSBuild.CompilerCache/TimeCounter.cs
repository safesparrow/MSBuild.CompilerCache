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
        }
    }

    public TimeSpan Total()
    {
        lock(_lock)
        {
            return TimeSpan.FromMilliseconds(_totalMilliseconds);
        }
    }
}