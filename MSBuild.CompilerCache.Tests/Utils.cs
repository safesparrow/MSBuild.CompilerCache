using System.Diagnostics;

namespace Tests;

internal static class Utils
{
    public static string[] RunProcess(string name, string args, DirectoryInfo workingDir)
    {
        var pi = new ProcessStartInfo(name, args)
        {
            WorkingDirectory = workingDir.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        Console.WriteLine($"Running '{name} {args}' in {workingDir.FullName}");
        var p = new Process();
        p.StartInfo = pi;
        var output = new List<string>();
        p.OutputDataReceived += (sender, eventArgs) =>
        {
            Console.WriteLine($"[{name} - OUT] {eventArgs.Data}");
            output.Add(eventArgs.Data!);
        };
        p.ErrorDataReceived += (sender, eventArgs) =>
        {
            Console.WriteLine($"[{name} - ERR] {eventArgs.Data}");
            output.Add(eventArgs.Data!);
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new Exception($"Running process failed with non-zero exit code.");
        }

        return output.ToArray();
    }
}