using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;

namespace LyWaf.Utils;

public static class CommonUtil
{
    public static T DeepCopy<T>(T obj) where T : class
    {
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(obj))!;
    }

    public static Stream ObjectToStream<T>(string key, T obj) {
        var dict = new Dictionary<string, T>
        {
            { key, obj }
        };
        var ret = JsonSerializer.Serialize(dict);
        return new MemoryStream(Encoding.UTF8.GetBytes(ret));
    }

    public static JObject SafeJson(string val)
    {
        try
        {
            return JObject.Parse(val);
        }
        catch (Exception)
        {
            return [];
        }
    }

    // 检查数组包含关系
    public static bool ContainsAllArrayElements(JArray source, JArray target)
    {
        foreach (JToken targetItem in target)
        {
            bool found = false;
            foreach (JToken sourceItem in source)
            {
                if (JToken.DeepEquals(sourceItem, targetItem))
                {
                    found = true;
                    break;
                }
            }
            if (!found) return false;
        }
        return true;
    }

    public static bool ContainsAllProperties(JObject source, JObject target)
    {
        foreach (JProperty targetProp in target.Properties())
        {
            // 检查源JSON是否包含目标属性
            if (!source.ContainsKey(targetProp.Name))
                return false;

            // 如果目标属性值是对象，递归检查
            if (targetProp.Value.Type == JTokenType.Object)
            {
                if (source[targetProp.Name]!.Type != JTokenType.Object)
                    return false;

                if (!ContainsAllProperties(
                    (JObject)source[targetProp.Name]!,
                    (JObject)targetProp.Value))
                    return false;
            }
            // 如果目标属性值是数组，检查数组包含关系
            else if (targetProp.Value.Type == JTokenType.Array)
            {
                if (source[targetProp.Name]!.Type != JTokenType.Array)
                    return false;

                if (!ContainsAllArrayElements(
                    (JArray)source[targetProp.Name]!,
                    (JArray)targetProp.Value))
                    return false;
            }
            // 简单值比较
            else if (!JToken.DeepEquals(source[targetProp.Name], targetProp.Value))
            {
                return false;
            }
        }

        return true;
    }

    const long K_BYTES = 1024;
    const long M_BYTES = 1024 * 1024;
    const long G_BYTES = 1024 * 1024 * 1024;
    const long T_BYTES = 1024L * 1024 * 1024 * 1024;

    public static string LengthToBytesSize(long length) {
        if(length < K_BYTES) {
            return $"{length}B";
        } else if(length < M_BYTES ) {
            return $"{length * 1.0 / K_BYTES:F2}KB";
        } else if(length < G_BYTES ) {
            return $"{length * 1.0 / M_BYTES:F2}MB";
        } else if(length < T_BYTES ) {
            return $"{length * 1.0 / G_BYTES:F2}GB";
        } else {
            return $"{length * 1.0 / T_BYTES:F2}TB";
        }
    }
}