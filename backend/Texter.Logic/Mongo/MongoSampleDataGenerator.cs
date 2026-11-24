using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using Texter.Erl.Mongo;
using Texter.Logic.Llm;

namespace Texter.Logic.Mongo
{
    /// <summary>
    /// Sample data for MongoDB: the LLM fills the collections, guided by the requirements. Its answer is constrained by
    /// a JSON schema derived from the collections' $jsonSchema validators (embedded arrays and references included).
    /// </summary>
    public class MongoSampleDataGenerator(ILlmClient llm)
    {
        private static readonly string SystemPrompt = Resource("mongo-sample-data.system.md");

        public async Task<Dictionary<string, List<JsonElement>>> GenerateAsync(string requirements, MongoSchemaGenerator.MongoSchemaResult schema, CancellationToken ct = default)
        {
            var user = $"Requirements:\n\n{requirements}\n\nMongoDB schema (mongosh):\n\n{schema.Script}";
            var answer = await llm.CompleteJsonAsync(new LlmJsonRequest(SystemPrompt, user, "mongo_sample_data", Schema(schema.Map)), ct);
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, List<JsonElement>>>(answer) ?? throw new LlmException("Empty sample data.");
            }
            catch (JsonException e)
            {
                throw new LlmException("Could not parse the sample data: " + e.Message);
            }
        }

        /// <summary>{ "Collection": [ document, ... ], ... }, every field required (optional ones may be null).</summary>
        public static string Schema(MongoMap map) => new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(map.Collections.Select(c => (JsonNode)c.Name).ToArray()),
            ["properties"] = new JsonObject(map.Collections.Select(c => new KeyValuePair<string, JsonNode?>(c.Name, new JsonObject
            {
                ["type"] = "array",
                ["items"] = Convert(c.Schema, nullable: false)
            })))
        }.ToJsonString();

        /// <summary>A $jsonSchema node -> a strict JSON schema node (OpenAI structured outputs).</summary>
        private static JsonObject Convert(BsonDocument schema, bool nullable)
        {
            if (schema.TryGetValue("enum", out var values))
            {
                var items = values.AsBsonArray.Select(v => JsonValue(v)).ToList();
                if (nullable) items.Add(null);
                return new JsonObject { ["enum"] = new JsonArray(items.ToArray()) };
            }

            var bsonType = schema.TryGetValue("bsonType", out var t) ? (t.IsBsonArray ? t.AsBsonArray[0].AsString : t.AsString) : "string";
            JsonObject node;
            switch (bsonType)
            {
                case "object":
                    var required = schema.TryGetValue("required", out var r) ? r.AsBsonArray.Select(x => x.AsString).ToHashSet() : new HashSet<string>();
                    var properties = schema.TryGetValue("properties", out var p) ? p.AsBsonDocument : new BsonDocument();
                    node = new JsonObject
                    {
                        ["type"] = nullable ? new JsonArray("object", "null") : "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray(properties.Names.Select(n => (JsonNode)n).ToArray()),
                        ["properties"] = new JsonObject(properties.Select(e => new KeyValuePair<string, JsonNode?>(e.Name,
                            Convert(e.Value.AsBsonDocument, nullable: !required.Contains(e.Name)))))
                    };
                    break;
                case "array":
                    node = new JsonObject
                    {
                        ["type"] = nullable ? new JsonArray("array", "null") : "array",
                        ["items"] = schema.TryGetValue("items", out var i) ? Convert(i.AsBsonDocument, nullable: false) : new JsonObject { ["type"] = "string" }
                    };
                    break;
                default:
                    var type = bsonType switch
                    {
                        "int" or "long" => "integer",
                        "number" or "double" or "decimal" => "number",
                        "bool" => "boolean",
                        _ => "string"
                    };
                    node = new JsonObject { ["type"] = nullable ? new JsonArray(type, "null") : type };
                    var hints = new List<string>();
                    if (bsonType == "date") hints.Add("date YYYY-MM-DD");
                    if (schema.TryGetValue("maxLength", out var max)) hints.Add($"at most {max} characters");
                    if (schema.TryGetValue("minimum", out var min)) hints.Add($"{(schema.TryGetValue("exclusiveMinimum", out var ex) && ex.AsBoolean ? ">" : ">=")} {min}");
                    if (schema.TryGetValue("maximum", out var mx)) hints.Add($"{(schema.TryGetValue("exclusiveMaximum", out var ex2) && ex2.AsBoolean ? "<" : "<=")} {mx}");
                    if (schema.TryGetValue("pattern", out var pattern)) hints.Add($"matches {pattern}");
                    if (hints.Count > 0) node["description"] = string.Join("; ", hints);
                    break;
            }
            if (schema.TryGetValue("description", out var description))
                node["description"] = node["description"] is { } d ? $"{description}; {d}" : description.AsString;
            return node;
        }

        private static JsonNode? JsonValue(BsonValue v) => v switch
        {
            BsonString s => s.Value,
            BsonInt32 i => i.Value,
            BsonInt64 l => l.Value,
            BsonDouble d => d.Value,
            BsonBoolean b => b.Value,
            _ => null
        };

        private static string Resource(string fileName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + fileName, StringComparison.Ordinal));
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            return reader.ReadToEnd();
        }
    }
}
