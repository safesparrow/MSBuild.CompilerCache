namespace Tests;

public static class Extensions
{
    public static string ToJson(this object x) => Newtonsoft.Json.JsonConvert.SerializeObject(x);
}