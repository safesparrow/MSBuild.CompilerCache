namespace MSBuild.CompilerCache;

public record struct FileCacheKey(string FullName, long Length, DateTime LastWriteTimeUtc);