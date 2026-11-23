using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Texter.Erl.Ddl;
using Texter.Logic.Llm;

namespace Texter.Logic.Sandbox
{
    /// <summary>
    /// Sample data for step 4: the LLM fills the tables of the DDL, guided by the requirements. The answer is
    /// constrained by a JSON schema built from the actual tables and columns, so names and types cannot drift.
    /// </summary>
    public class SampleDataGenerator(ILlmClient llm)
    {
        private static readonly string SystemPrompt = Resource("sample-data.system.md");

        public async Task<Dictionary<string, List<Dictionary<string, JsonElement>>>> GenerateAsync(
            string requirements, string ddl, RelationalMap map, CancellationToken ct = default)
        {
            var user = $"Requirements:\n\n{requirements}\n\nDDL (SQL Server):\n\n{ddl}";
            var answer = await llm.CompleteJsonAsync(new LlmJsonRequest(SystemPrompt, user, "sample_data", Schema(map)), ct);
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, List<Dictionary<string, JsonElement>>>>(answer)
                    ?? throw new LlmException("Empty sample data.");
            }
            catch (JsonException e)
            {
                throw new LlmException("Could not parse the sample data: " + e.Message);
            }
        }

        /// <summary>{ "Table": [ { "column": value, ... } ], ... } with every column required (nullable ones may be null).</summary>
        public static string Schema(RelationalMap map)
        {
            var tables = new JsonObject();
            foreach (var table in map.Tables)
            {
                var columns = new JsonObject();
                foreach (var c in table.Columns.Where(c => !c.Computed))
                {
                    var type = JsonType(c.Type);
                    columns[c.Name] = new JsonObject
                    {
                        ["type"] = c.Nullable ? new JsonArray(type, "null") : type,
                        ["description"] = c.Type
                    };
                }
                tables[table.Name] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray(table.Columns.Where(c => !c.Computed).Select(c => (JsonNode)c.Name).ToArray()),
                        ["properties"] = columns
                    }
                };
            }
            return new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray(map.Tables.Select(t => (JsonNode)t.Name).ToArray()),
                ["properties"] = tables
            }.ToJsonString();
        }

        private static string JsonType(string sqlType)
        {
            var t = sqlType.ToLowerInvariant();
            if (t is "tinyint" or "smallint" or "int" or "bigint") return "integer";
            if (t.StartsWith("decimal") || t is "money" or "float") return "number";
            if (t == "bit") return "boolean";
            return "string";
        }

        private static string Resource(string fileName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + fileName, StringComparison.Ordinal));
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            return reader.ReadToEnd();
        }
    }
}
