# Generating reference assemblies for caching
## Compilation vs referenced assemblies
One of the main inputs to the compilation process is a set of referenced assemblies.
These assemblies come from `ProjectReferences`, `PackageReferences` or the framework.

Results of the compilation depend only on the parts of the assemblies accessible by the assembly being built.
In most cases this is limited to the public API of those assemblies.

This means that changes to a referenced assembly that are not accessible do not affect the results,
and compilation results can be reused from previous runs.

## .NET reference assemblies
.NET already provides tooling for generating and using reference assemblies.
See [Reference Assemblies](https://learn.microsoft.com/en-us/dotnet/standard/assembly/reference-assemblies) for detailed documentation.

When the mechanism is enabled:
1. Dependencies generate reference assemblies on top of regular assemblies.
2. Dependants use reference assemblies as inputs for compilation instead of full assemblies
3. Regular assemblies are shipped to the output directory for use at runtime.

## Why custom generation of reference assemblies could be useful
The following reasons outline why custom generation of a similar type of reference assemblies might be useful for caching,
despite the standard .NET mechanism already existing:

1. Reference assembly support in F# has only been added in .NET7. When using previous SDKs:
   1. F# compilation does not use reference assemblies provided by other projects, causing needless recompilation.
   2. F# compilation does not produce reference assemblies, forcing needless recompilation of dependant projects.
2. Most NuGet packages do not ship reference assemblies, therefore most package updates force recompilation. This tendency is driven by lack of native support for reference assemblies in NuGet.
3. If an assembly defines any `InternalsVisibleTo` attribute, its standard reference assembly will include internal method definitions. However, compilation of assemblies not on the `InternalsVisibleTo` list is not affected by those internal elements.

## Using JetBrains.Refasmer to generate reference assemblies
https://github.com/JetBrains/Refasmer/ can be used to generate reference assemblies.

It can be used as a .NET global tool, or a library.

However, it is not clear whether using Refasmer is safe, ie. that the resulting assembly contains everything that affects compilation of dependent projects.
For an example, see [this issue](https://github.com/JetBrains/Refasmer/issues/18).