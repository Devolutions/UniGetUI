using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using YamlDotNet.RepresentationModel;

namespace UniGetUI.Core.Data;

public static class SerializationHelpers
{
    private static readonly JsonSerializerOptions NodeJsonOptions = new()
    {
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static Task<string> YAML_to_JSON(string YAML) => Task.Run(() => yaml_to_json(YAML));

    private static string yaml_to_json(string YAML)
    {
        using var reader = new StringReader(YAML);
        var stream = new YamlStream();
        stream.Load(reader);

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is null)
            return "{\"message\":\"deserialized YAML object was null\"}";

        return ConvertYamlNode(stream.Documents[0].RootNode)?.ToJsonString(NodeJsonOptions)
            ?? "null";
    }

    public static Task<string> XML_to_JSON(string XML) => Task.Run(() => xml_to_json(XML));

    private static string xml_to_json(string XML)
    {
        var doc = new XmlDocument();
        doc.LoadXml(XML);
        if (doc.DocumentElement is null)
            return "{\"message\":\"XmlDocument.DocumentElement was null\"}";

        return ConvertXmlNode(doc.DocumentElement)?.ToJsonString(NodeJsonOptions)
            ?? "null";
    }

    private static JsonNode? ConvertXmlNode(XmlNode node)
    {
        if (node.ChildNodes.Count == 1 && node.FirstChild is XmlText singleText)
        {
            return JsonValue.Create(singleText.Value);
        }

        var jsonObject = new JsonObject();
        if (node.Attributes?.Count > 0)
        {
            foreach (XmlAttribute attr in node.Attributes)
            {
                jsonObject[$"@{attr.Name}"] = attr.Value;
            }
        }

        var children = new Dictionary<string, List<JsonNode?>>();
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child is XmlElement childElement)
            {
                var value = ConvertXmlNode(childElement);
                if (!children.ContainsKey(childElement.Name))
                    children[childElement.Name] = new List<JsonNode?>();
                children[childElement.Name].Add(value);
            }
        }

        if (children.Count == 1 && jsonObject.Count == 0)
        {
            var firstKey = children.Keys.First();
            return children[firstKey].Count == 1
                ? children[firstKey][0]
                : new JsonArray(children[firstKey].ToArray());
        }

        foreach (var kv in children)
        {
            jsonObject[kv.Key] = kv.Value.Count == 1 ? kv.Value[0] : new JsonArray(kv.Value.ToArray());
        }

        return jsonObject;
    }

    private static JsonNode? ConvertYamlNode(YamlNode node)
    {
        return node switch
        {
            YamlScalarNode scalar => ConvertYamlScalar(scalar),
            YamlSequenceNode sequence => ConvertYamlSequence(sequence),
            YamlMappingNode mapping => ConvertYamlMapping(mapping),
            _ => JsonValue.Create(node.ToString()),
        };
    }

    private static JsonNode? ConvertYamlScalar(YamlScalarNode scalar)
    {
        if (scalar.Value is null)
            return null;

        if (bool.TryParse(scalar.Value, out bool boolValue))
            return JsonValue.Create(boolValue);

        if (long.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
            return JsonValue.Create(longValue);

        if (double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue))
            return JsonValue.Create(doubleValue);

        return JsonValue.Create(scalar.Value);
    }

    private static JsonArray ConvertYamlSequence(YamlSequenceNode sequence)
    {
        var array = new JsonArray();
        foreach (YamlNode child in sequence.Children)
            array.Add(ConvertYamlNode(child));
        return array;
    }

    private static JsonObject ConvertYamlMapping(YamlMappingNode mapping)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in mapping.Children)
            obj[ConvertYamlKey(key)] = ConvertYamlNode(value);
        return obj;
    }

    private static string ConvertYamlKey(YamlNode key)
    {
        return key is YamlScalarNode scalar
            ? scalar.Value ?? ""
            : key.ToString();
    }

    public static JsonSerializerOptions DefaultOptions = new()
    {
        AllowTrailingCommas = true,
        WriteIndented = true,
    };
}

public static class JsonNodeExtensions
{
    /// <summary>
    /// Safely gets a child node by key, returning null if the key does not exist.
    /// </summary>
    public static T GetVal<T>(this JsonNode node)
    {
        if (typeof(T) == typeof(double) && node.GetValueKind() is JsonValueKind.String)
            return (T)(object)double.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture);

        if (typeof(T) == typeof(int) && node.GetValueKind() is JsonValueKind.String)
            return (T)(object)int.Parse(node.GetValue<string>());

        if (typeof(T) == typeof(bool) && node.GetValueKind() is JsonValueKind.String)
            return (T)(object)bool.Parse(node.GetValue<string>());

        if (typeof(T) == typeof(string) && node.GetValueKind() is JsonValueKind.Object)
        {
            return (T)(object)"";
        }

        return node.GetValue<T>();
    }

    /// <summary>
    /// The same as JsonNode.AsArray, but can convert objects whose keys are integers to arrays
    /// </summary>
    /// <param name="node"></param>
    /// <returns></returns>
    public static JsonArray AsArray2(this JsonNode node)
    {
        if (node is JsonValue val)
        {
            JsonArray result = new();
            result.Add(val.DeepClone());
            return result;
        }
        else if (node is JsonObject obj && !obj.Any())
        {
            return new JsonArray();
        }

        return node.AsArray();
    }
}
