// Test-only adapter: production uses Unity JsonUtility. Fields-only JSON for cloud logic tests.
public static class JsonUtility
{
    private static System.Text.Json.JsonSerializerOptions Options(bool pretty = false)
    {
        var resolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(t => { for(int i=t.Properties.Count-1;i>=0;i--) if(t.Properties[i].AttributeProvider is System.Reflection.PropertyInfo) t.Properties.RemoveAt(i); });
        return new System.Text.Json.JsonSerializerOptions { IncludeFields = true, WriteIndented = pretty, TypeInfoResolver = resolver };
    }
    public static string ToJson(object o, bool prettyPrint = false) => System.Text.Json.JsonSerializer.Serialize(o, Options(prettyPrint));
    public static T FromJson<T>(string s) => System.Text.Json.JsonSerializer.Deserialize<T>(s, Options());
}
