namespace MSBuild.CompilerCache.Trimming;

using Mono.Cecil;
using System;

internal class Trimming
{
    public void TrimDll(string dllPath, string outputPath)
    {
        var module = ModuleDefinition.ReadModule(dllPath);
        var methods = module.Types.SelectMany(t => t.Methods).ToArray();
        foreach (var method in methods)
        {
            TrimMethod(method);
        }

        module.Write(outputPath);
    }

    public void TrimMethod(MethodDefinition method)
    {
        method.Body.Instructions.Clear();
        method.Body.Variables.Clear();
    }

    public void RemapDllSymbolPaths(string dllPath, string outputPath)
    {
        var p = new ReaderParameters()
        {
            ReadSymbols = true
        };
        var module = ModuleDefinition.ReadModule(dllPath, p);

        var methods = module.Types.SelectMany(t => t.Methods).ToArray();
        foreach (var method in methods)
        {
            RemapMethodSymbolUrls(method, @"test2\", @"test\");
        }

        module.Write(outputPath);
    }

    public void RemapMethodSymbolUrls(MethodDefinition method, string oldBaseDir, string newBaseDir)
    {
        var points = method.DebugInformation.SequencePoints;
        if (points.Count > 0)
        {
            var point = points[0];
            point.Document.Url = SubstituteBaseDir(point.Document.Url, oldBaseDir, newBaseDir);
        }
    }

    private string SubstituteBaseDir(string path, string oldBaseDir, string newBaseDir)
    {
        return path.StartsWith(oldBaseDir, StringComparison.Ordinal)
            ? string.Concat(newBaseDir, path.AsSpan(oldBaseDir.Length))
            : path;
    }
}