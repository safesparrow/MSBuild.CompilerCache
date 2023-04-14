using Mono.Cecil.Cil;

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

    private bool ReallyHasSymbols(ModuleDefinition module)
    {
        try
        {
            module.ReadSymbols();
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    public void RemapDllSymbolPaths(string dllPath, string outputPath, string oldBaseDir, string newBaseDir)
    {
        var module = ModuleDefinition.ReadModule(dllPath);
        var modulesCount = module.Assembly.Modules.Count;
        if (modulesCount > 1)
        {
            throw new NotImplementedException(
                $"{dllPath} is part of a multi-module assembly - only single-module assemblies are supported.");
        }

        if (ReallyHasSymbols(module))
        {
            module.ReadSymbols();
            //module.SymbolReader.GetWriterProvider().
            var typeMethods = module.Types.SelectMany(t => t.Methods);
            typeMethods = module.EntryPoint != null ? typeMethods.Append(module.EntryPoint) : typeMethods;
            var methods = typeMethods.ToArray();
            // foreach (var method in methods)
            // {
            //     RemapMethodSymbolUrls(method, oldBaseDir, newBaseDir);
            // }
            
            var p = new WriterParameters
            {
                WriteSymbols = true,
                DeterministicMvid = true
            };
            module.Write(outputPath, p);
        }
        else
        {
            File.Copy(dllPath, outputPath);
        }
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