using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace OpenFsso.Configuration;

public static class YamlConfiguration
{
    private const int MaximumBytes = 64 * 1024;
    private const int MaximumNesting = 256;

    public static T Load<T>(string path, ConfigurationSchema schema) where T : class
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetExtension(path).ToLowerInvariant() is not (".yaml" or ".yml"))
        {
            throw new InvalidDataException("Use an absolute YAML configuration path ending in .yaml or .yml.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes)
        {
            throw new InvalidDataException("Configuration must not exceed 64 KiB.");
        }

        using var reader = new StreamReader(stream);
        return Parse<T>(reader.ReadToEnd(), schema);
    }

    public static T Parse<T>(string text, ConfigurationSchema schema) where T : class
    {
        if (text.Length > MaximumBytes)
        {
            throw new InvalidDataException("Configuration must not exceed 64 KiB.");
        }

        try
        {
            var data = new DeserializerBuilder()
                .WithDuplicateKeyChecking()
                .WithAttemptingUnquotedStringTypeDeserialization()
                .WithMaximumRecursion(MaximumNesting)
                .Build()
                .Deserialize<object>(text) ?? throw new InvalidDataException("Configuration is empty.");
            var instance = JsonSerializer.SerializeToElement(data, new JsonSerializerOptions { MaxDepth = MaximumNesting });
            SchemaValidation.Validate(instance, schema);

            return instance.Deserialize<T>(new JsonSerializerOptions
            {
                MaxDepth = MaximumNesting,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                RespectNullableAnnotations = true,
                RespectRequiredConstructorParameters = true
            }) ?? throw new InvalidDataException("Configuration is empty.");
        }
        catch (YamlException error)
        {
            // Parser exceptions can contain the source line, including an API key.
            throw new InvalidDataException($"Invalid YAML configuration at line {error.Start.Line}, column {error.Start.Column}. Check indentation, field names, required values and types.");
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("Invalid YAML configuration. Check field names, required values and types.");
        }
    }
}
