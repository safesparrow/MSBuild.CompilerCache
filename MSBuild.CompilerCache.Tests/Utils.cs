using System.Diagnostics;

namespace Tests;

internal static class Utils
{
    public static void RunProcess(string name, string args, DirectoryInfo workingDir)
    {
        var pi = new ProcessStartInfo(name, args)
        {
            WorkingDirectory = workingDir.FullName,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        Console.WriteLine($"'{name} {args}' in {workingDir.FullName}");
        var p = Process.Start(pi);
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            //throw new Exception($"Running process failed with non-zero exit code.");
        }
    }
}