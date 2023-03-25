using System.Runtime.Serialization.Formatters.Binary;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

[Serializable]
public record Extract(string Name, DateTime Time, long Length);

// ReSharper disable once UnusedType.Global
public class SimpleTask : Task
{
    public string Compile { get; set; }
    public string References { get; set; }
    public string MyProperty { get; set; }

    public string CacheDir { get; set; } = "c:/projekty/zip/zip/zip/cache/";

    public override bool Execute()
    {
        var items = (Compile?.Split(";") ?? Array.Empty<string>())
            .Union(References?.Split(";") ?? Array.Empty<string>()).ToArray();
        var baseDir = @"c:\projekty\zip\zip\zip\";
        var extracts = items.AsParallel().Select(file =>
        {
            var fileInfo = new FileInfo(file);
            return new Extract(fileInfo.Name, fileInfo.CreationTime, fileInfo.Length);
        }).ToArray();

        using var ms = new MemoryStream();
#pragma warning disable SYSLIB0011
        var binaryFormatter = new BinaryFormatter();
        binaryFormatter.Serialize(ms, extracts);
        ms.Position = 0;
#pragma warning restore SYSLIB0011
        var hash = System.Security.Cryptography.SHA256.Create().ComputeHash(ms);
        var hashString = Convert.ToHexString(hash);
        var dir = Path.Combine(CacheDir, hashString);
        if (!Directory.Exists(dir))
        {
            Log.LogMessage(MessageImportance.High, $"{dir} does not exist");
            Directory.CreateDirectory(dir); 
        }
        else
        {
            Log.LogMessage(MessageImportance.High, $"{dir} exists");
        }
        
        return true;
    }
}