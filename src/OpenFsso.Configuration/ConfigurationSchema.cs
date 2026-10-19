using System.Net;
using System.Text.Json;
using Json.Schema;

namespace OpenFsso.Configuration;

public enum ConfigurationSchema
{
    Client,
    Receiver
}

internal static class SchemaValidation
{
    private static readonly Lazy<IReadOnlyDictionary<ConfigurationSchema, JsonSchema>> Schemas = new(() =>
    {
        FormatRegistry.Global.Register(new PredicateFormat("cidr", value => value.ValueKind != JsonValueKind.String || IPNetwork.TryParse(value.GetString(), out _)));
        return Enum.GetValues<ConfigurationSchema>().ToDictionary(schema => schema, schema =>
        {
            using var stream = typeof(SchemaValidation).Assembly.GetManifestResourceStream($"OpenFsso.Configuration.{schema.ToString().ToLowerInvariant()}.schema.json")!;
            using var document = JsonDocument.Parse(stream);
            return JsonSchema.Build(document.RootElement.Clone(), new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new() });
        });
    });

    public static void Validate(JsonElement instance, ConfigurationSchema schema)
    {
        var result = Schemas.Value[schema].Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical, RequireFormatValidation = true });
        if (result.IsValid)
        {
            return;
        }

        IEnumerable<(string Path, string Message)> Errors(EvaluationResults node)
        {
            if (node.IsValid)
            {
                yield break;
            }

            if (node.Details != null)
            {
                foreach (var child in node.Details)
                {
                    foreach (var error in Errors(child))
                    {
                        yield return error;
                    }
                }
            }

            if (node.Errors != null)
            {
                foreach (var error in node.Errors.Values)
                {
                    yield return (node.InstanceLocation.ToString(), error);
                }
            }
        }

        var errors = Errors(result).Distinct()
            .OrderByDescending(error => error.Path.Count(character => character == '/'))
            .Take(8)
            .Select(error => $"{(error.Path.Length == 0 ? "/" : error.Path)}: {error.Message}");
        throw new InvalidDataException($"Invalid {schema.ToString().ToLowerInvariant()} configuration:\n" + string.Join('\n', errors));
    }
}
