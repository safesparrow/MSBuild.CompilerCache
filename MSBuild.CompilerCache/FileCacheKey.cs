using System.Collections.Immutable;

namespace MSBuild.CompilerCache;

[Serializable]
public record struct FileCacheKey(string FullName, long Length, DateTime LastWriteTimeUtc);
