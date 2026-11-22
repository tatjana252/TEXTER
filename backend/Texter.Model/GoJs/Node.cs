using System.Text.Json.Serialization;
using Texter.Model.Conceptual;

namespace Texter.Model.GoJs
{
    public class Node
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = default!;

        [JsonPropertyName("category")]
        public string Category { get; set; } = default!;

        [JsonPropertyName("text")]
        public string Text { get; set; } = default!;

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("confidenceLevel")]
        public ConfidenceLevel ConfidenceLevel { get; set; }

        [JsonPropertyName("spans")]
        public List<TextSpan> Spans { get; set; } = new();

        [JsonPropertyName("rationale")]
        public string Rationale { get; set; } = string.Empty;

        // Attribute flags
        [JsonPropertyName("isKey")]
        public bool IsKey { get; set; }

        [JsonPropertyName("isMultivalued")]
        public bool IsMultivalued { get; set; }

        [JsonPropertyName("isDerived")]
        public bool IsDerived { get; set; }

        // Generalization flags
        [JsonPropertyName("isDisjoint")]
        public bool IsDisjoint { get; set; }
    }
}
