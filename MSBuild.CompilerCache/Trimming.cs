namespace MSBuild.CompilerCache.Trimming;

using Mono.Cecil;

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
}