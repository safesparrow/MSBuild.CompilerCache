using System.Diagnostics;

namespace Tests;

internal static class Utils
{
    public static void RunProcess(string name, string args, DirectoryInfo workingDir)
    {
        var pi = new ProcessStartInfo(name, args)
        {
            WorkingDirectory = workingDir.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };
        
        Console.WriteLine($"RunProcess '{name} {args}' in {workingDir.FullName}");
        var p = Process.Start(pi);
        
        var outputTask = Task.Run(() => p.StandardOutput.ReadToEnd());
        var errorTask = Task.Run(() => p.StandardError.ReadToEnd());
        p.WaitForExit();
        
        Console.WriteLine($"outputTask:{outputTask.Result}");
        Console.WriteLine($"------------------------------------------");
        Console.WriteLine($"errorTask:{errorTask.Result}");
        Console.WriteLine($"------------------------------------------");
        if (p.ExitCode != 0)
        {
            throw new Exception($"Running process failed with non-zero exit code.");
        }
    }
}