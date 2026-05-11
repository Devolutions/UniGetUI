using System.Text.Json;
using System.Text.Json.Nodes;
using UniGetUI.Core.Data;

namespace UniGetUI.PackageEngine.Serializable;

public abstract class SerializableComponent<T>
    where T : class
{
    /// <summary>
    /// Creates a deep copy of the object
    /// </summary>
    /// <returns>A memory-independend copy of this</returns>
    public abstract T Copy();

    /// <summary>
    /// Loads data for this object from a JsonNode object
    /// </summary>
    /// <param name="data">The JSON from which to load the data</param>
    public abstract void LoadFromJson(JsonNode data);

    /// <summary>
    /// Serializes this object into a JsonNode object
    /// </summary>
    /// <returns>A pretty-formatted JSON string representing the current data</returns>
    public string AsJsonString()
    {
        return AsJsonNode().ToJsonString(SerializationHelpers.DefaultOptions);
    }

    /// <summary>
    /// Serializes this object into a JsonNode object
    /// </summary>
    /// <returns>A pretty-formatted JSON string representing the current data</returns>
    public abstract JsonObject AsJsonNode();

    /// <summary>
    /// Creates an instance of this object with the default data
    /// </summary>
    public SerializableComponent() { }

    /// <summary>
    /// Creates an instance of this object, and loads the data from the given JsonNode object
    /// </summary>
    /// <param name="data">The JSON from which to load the data</param>
    public SerializableComponent(JsonNode data)
    {
        LoadFromJson(data);
    }
}
