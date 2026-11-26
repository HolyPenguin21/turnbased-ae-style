// Test-only adapter: production uses Unity JsonUtility. This models its inline
// CampaignOperation/null-string representation, not the complete native serializer.
public static class JsonUtility
{
    private static System.Text.Json.JsonSerializerOptions Options(bool pretty = false)
    {
        var resolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(t =>
        {
            for (int i = t.Properties.Count - 1; i >= 0; i--)
            {
                var property = t.Properties[i];
                if (property.AttributeProvider is System.Reflection.PropertyInfo) { t.Properties.RemoveAt(i); continue; }
                if (System.Environment.GetEnvironmentVariable("CAMPAIGN_JSON_INLINE_NULLS") == "0" || property.Get == null) continue;
                var get = property.Get;
                if (property.PropertyType == typeof(string)) property.Get = o => get(o) ?? "";
                else if (property.PropertyType == typeof(Game.Campaign.CampaignOperation))
                    property.Get = o => get(o) ?? new Game.Campaign.CampaignOperation();
            }
        });
        return new System.Text.Json.JsonSerializerOptions { IncludeFields = true, WriteIndented = pretty, TypeInfoResolver = resolver };
    }
    public static string ToJson(object o, bool prettyPrint = false) => System.Text.Json.JsonSerializer.Serialize(o, Options(prettyPrint));
    public static T FromJson<T>(string s) => System.Text.Json.JsonSerializer.Deserialize<T>(s, Options());
}
