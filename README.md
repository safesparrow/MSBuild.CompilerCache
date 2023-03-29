# What is this?
MSBuild.CompilerCache is a package that provides caching of C# and F# project compilation.

It extends the `CoreCompile` targets from the SDK with caching steps and uses custom MSBuild tasks that perform the actual caching.

# How does it work?
1. Before `Csc` or `Fsc` task is invoked, we calculate a hash of all the relevant compilation inputs.
2. If a cache entry exists with that hash, we simply copy the files and skip compilation.
3. If the file does not exist, we run compilation and then populate the cache.

# How can I use it?

To use the cache, add the following to your project file (or `Directory.Build.props` in your directory structure):
```xml
<PropertyGroup>
    <CompilationCacheBaseDir>c:/machine-wide/compilation-cache/</CompilationCacheBaseDir>
</PropertyGroup>

<ItemGroup>
    <PackageReference Include="MSBuild.CompilerCache" Version="0.0.1" />
</ItemGroup>
```
The above code does two things:
1. Adds the `MSBuild.CompilerCache` package, which provides custom Task definitions and ships .targets files.
2. Sets the `CompilationCacheBaseDir` used as a root directory for cache entries. This needs to be an accessible filesystem directory.

# Local cache vs remote cache
The location set in `CompilationCacheBaseDir` can be either a local path or a network share - the caching logic behaves exactly the same.

This means that you could share the cache between multiple people.
If you do, note that in many scenarios this is not secure enough, as it means that users can inject malicious dll files into a shared place, to be used by other users.

# Outstanding issues
- Currently only .NET SDK 7.0.202 is supported. Note that the SDK you use must match that precisely. This is because the `CoreCompile` target is extended by copy-pasting the original target from the SDK, so a new SDK requires a new copy.
- Some of the less common compilation inputs are currently ignored. This is easy to fix, but can currently lead to incorrectly reusing cached results when one of those input changes.
- The cache mechanism is currently not safe for running multiple builds in parallel. If the same project is being built in two workspaces at the same time, there might be some undefined behaviour. This is again easy to fix.