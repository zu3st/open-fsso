using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace OpenFsso.Configuration;

public static partial class YamlConfiguration
{
    private const int MaximumBytes = 64 * 1024;
    private const int MaximumNesting = 256;

    public static T Load<T>(string path, ConfigurationSchema schema, JsonTypeInfo<T> type) where T : class
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
        return Parse(reader.ReadToEnd(), schema, type);
    }

    public static T Parse<T>(string text, ConfigurationSchema schema, JsonTypeInfo<T> type) where T : class
    {
        if (text.Length > MaximumBytes)
        {
            throw new InvalidDataException("Configuration must not exceed 64 KiB.");
        }

        try
        {
            using var document = JsonDocument.Parse(ToJson(text), new JsonDocumentOptions { MaxDepth = MaximumNesting + 1 });
            SchemaValidation.Validate(document.RootElement, schema);
            return document.RootElement.Deserialize(type) ?? throw new InvalidDataException("Configuration is empty.");
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

    private sealed class Frame(bool mapping)
    {
        public bool Mapping { get; } = mapping;
        public bool ExpectKey { get; set; } = mapping;
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
    }

    // Converts the single YAML document to JSON from the parser's event stream,
    // which needs no reflection and no recursion.
    private static ReadOnlyMemory<byte> ToJson(string text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var frames = new Stack<Frame>();
        int documents = 0;
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = MaximumNesting + 1 }))
        {
            void Value()
            {
                if (frames.TryPeek(out var frame) && frame.Mapping)
                {
                    if (frame.ExpectKey)
                    {
                        throw new InvalidDataException("Invalid YAML configuration. Mapping keys must be plain text.");
                    }

                    frame.ExpectKey = true;
                }
            }

            void Open(bool mapping)
            {
                if (frames.Count == MaximumNesting)
                {
                    throw new InvalidDataException("Invalid YAML configuration. It is nested too deeply.");
                }

                frames.Push(new Frame(mapping));
            }

            var parser = new Parser(new StringReader(text));
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case DocumentStart:
                        if (++documents > 1)
                        {
                            throw new InvalidDataException("Invalid YAML configuration. Use a single YAML document.");
                        }

                        break;
                    case AnchorAlias:
                    case NodeEvent { Anchor.IsEmpty: false }:
                    case NodeEvent { Tag.IsEmpty: false }:
                        throw new InvalidDataException("Invalid YAML configuration. Anchors, aliases and tags are not supported.");
                    case MappingStart:
                        Value();
                        writer.WriteStartObject();
                        Open(mapping: true);
                        break;
                    case SequenceStart:
                        Value();
                        writer.WriteStartArray();
                        Open(mapping: false);
                        break;
                    case MappingEnd:
                        frames.Pop();
                        writer.WriteEndObject();
                        break;
                    case SequenceEnd:
                        frames.Pop();
                        writer.WriteEndArray();
                        break;
                    case Scalar scalar when frames.TryPeek(out var frame) && frame.Mapping && frame.ExpectKey:
                        if (!frame.Keys.Add(scalar.Value))
                        {
                            throw new InvalidDataException("Invalid YAML configuration. A field appears more than once.");
                        }

                        writer.WritePropertyName(scalar.Value);
                        frame.ExpectKey = false;
                        break;
                    case Scalar scalar:
                        Value();
                        Write(writer, scalar);
                        break;
                }
            }
        }

        if (documents == 0)
        {
            throw new InvalidDataException("Configuration is empty.");
        }

        return buffer.WrittenMemory;
    }

    // Plain scalars follow the YAML 1.2 core schema; quoted and block scalars are text.
    private static void Write(Utf8JsonWriter writer, Scalar scalar)
    {
        string value = scalar.Value;
        if (scalar.Style != ScalarStyle.Plain)
        {
            writer.WriteStringValue(value);
        }
        else if (value is "" or "~" or "null" or "Null" or "NULL")
        {
            writer.WriteNullValue();
        }
        else if (value is "true" or "True" or "TRUE" or "false" or "False" or "FALSE")
        {
            writer.WriteBooleanValue(value[0] is 't' or 'T');
        }
        else if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
        {
            writer.WriteNumberValue(integer);
        }
        else if (Float().IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex Float();
}
