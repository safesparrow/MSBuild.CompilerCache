# What is this?
`MSBuild.CompilerCache` is a package that provides machine-wide or distributed caching of C# and F# project compilation.

It extends the `CoreCompile` targets from the .NET SDK with caching steps and uses custom MSBuild tasks that perform the actual caching.

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
    <PackageReference Include="MSBuild.CompilerCache" Version="0.0.2" />
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
- Currently only selected .NET SDKs are supported. 
- Some of the less common compilation inputs are currently ignored. This is easy to fix, but can currently lead to incorrectly reusing cached results when one of those inputs changes.
- The cache mechanism is currently not safe for running multiple builds in parallel. If the same project is being built in two workspaces at the same time, there might be some undefined behaviour.

# Supported SDKs
Below is the list of all supported .NET SDKs:
- 6.0.300
- 7.0.202

If your project is not using one of those versions, caching will be disabled.
Note that the SDK you use must match one of the supported ones precisely.
This is because the `CoreCompile` targets are created by first copy-pasting the original targets from the SDK and then adding caching logic - so a new SDK requires a new copy.

# Reporting issues
The project is in a very early stage. You are free to try the tool, but it is expected to cause _some_ issues in some cases.
If you think you found an issue that isn't covered in the "Outstanding issues" list above, please raise it on GitHub.

# Contributing
You are most welcome to contribute to the project here on GitHub.

Please raise an issue if you would like to:
- share an idea
- report a bug
- ask a question
- make implementation changes
