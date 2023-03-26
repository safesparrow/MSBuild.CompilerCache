using Mono.Cecil;
using Mono.Collections.Generic;
using System;

void TrimDll(string dllPath, string outputPath)
{
    var module = ModuleDefinition.ReadModule(dllPath);
    var methods = module.Types.SelectMany(t => t.Methods).ToArray();
    foreach (var method in methods)
    {
        TrimMethod(method);
    }

    module.Write(outputPath);
}

void TrimMethod(MethodDefinition method)
{
    method.Body.Instructions.Clear();
    method.Body.Variables.Clear();
}

void RemapDllSymbolPaths(string dllPath, string outputPath)
{
    var p = new ReaderParameters()
    {
        ReadSymbols = true
    };
    var module = ModuleDefinition.ReadModule(dllPath, p);

    var methods = module.Types.SelectMany(t => t.Methods).ToArray();
    foreach (var method in methods)
    {
        RemapMethodSymboLUrls(method, @"test2\", @"test\");
    }

    module.Write(outputPath);
}

void RemapMethodSymboLUrls(MethodDefinition method, string oldBaseDir, string newBaseDir)
{
    var points = method.DebugInformation.SequencePoints;
    if (points.Count > 0)
    {
        var point = points[0];
        point.Document.Url = SubstituteBaseDir(point.Document.Url, oldBaseDir, newBaseDir);
    }
}

string SubstituteBaseDir(string path, string oldBaseDir, string newBaseDir)
{
    return path.StartsWith(oldBaseDir, StringComparison.Ordinal)
        ? string.Concat(newBaseDir, path.AsSpan(oldBaseDir.Length))
        : path;
}

var file = @"C:\projekty\zip\zip\test\bin\Debug\net7.0\test.dll";
// TrimDll(file, file + ".mapped.dll");
// RemapDllSymbolPaths(file, file + ".mapped.dll");