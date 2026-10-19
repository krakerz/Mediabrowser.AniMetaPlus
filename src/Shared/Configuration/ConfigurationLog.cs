using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AniMetaPlus.Configuration;

/// <summary>
/// The settings as one line for the log, so that a problem can be matched against them without
/// a screenshot of the settings page. Client names, keys and anything else that identifies the
/// owner to a source are left out, shown only as set or not.
/// </summary>
public static partial class ConfigurationLog
{
    private static readonly JsonSerializerOptions _options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };

    /// <summary>
    /// Writes the settings as JSON with the sensitive values replaced.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>The settings, for the log.</returns>
    public static string Describe(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var node = JsonSerializer.SerializeToNode(configuration, _options);
        Redact(node);

        var text = new StringBuilder();
        Write(node, text, top: true);

        return text.ToString();
    }

    // Written as name=value rather than JSON: Jellyfin quotes a logged string and escapes every
    // quote inside it, which buries JSON in backslashes.
    private static void Write(JsonNode? node, StringBuilder text, bool top = false)
    {
        switch (node)
        {
            case JsonObject obj:
                if (!top)
                {
                    text.Append('{');
                }

                var first = true;
                foreach (var (name, value) in obj)
                {
                    text.Append(first ? string.Empty : ", ").Append(name).Append('=');
                    Write(value, text);
                    first = false;
                }

                if (!top)
                {
                    text.Append('}');
                }

                break;

            case JsonArray array:
                text.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    text.Append(i == 0 ? string.Empty : ", ");
                    Write(array[i], text);
                }

                text.Append(']');
                break;

            case JsonValue value:
                text.Append(value.TryGetValue<string>(out var s) ? s : value.ToJsonString());
                break;

            default:
                text.Append("null");
                break;
        }
    }

    /// <summary>
    /// Whether a setting holds something that identifies the owner: a client name or id, a key,
    /// a token, a secret or a password. A client version is not one.
    /// </summary>
    /// <param name="name">The setting's name.</param>
    /// <returns><c>true</c> when its value is left out of the log.</returns>
    internal static bool IsSensitive(string name)
        => SensitiveRegex().IsMatch(name) && !name.EndsWith("Version", StringComparison.OrdinalIgnoreCase);

    private static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(pair => pair.Key).ToList())
                {
                    var value = obj[name];
                    if (IsSensitive(name) && value is JsonValue)
                    {
                        obj[name] = string.IsNullOrWhiteSpace(value.ToString()) ? "(not set)" : "(set)";
                    }
                    else
                    {
                        Redact(value);
                    }
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    Redact(item);
                }

                break;
        }
    }

    [GeneratedRegex("client|key|token|secret|password", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveRegex();
}
