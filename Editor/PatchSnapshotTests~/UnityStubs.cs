using System.Text.Json;
namespace UnityEngine
{
    public static class JsonUtility
    {
        public static string ToJson(object value, bool pretty = false) => JsonSerializer.Serialize(value, value.GetType(),
            new JsonSerializerOptions { IncludeFields = true, WriteIndented = pretty });
    }
}
