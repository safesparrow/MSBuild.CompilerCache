using Newtonsoft.Json;

namespace Tests;

public static class Extensions
{
    public static string ToJson(this object x) => JsonConvert.SerializeObject(x);
}