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

        // Attribution
        [JsonPropertyName("sentenceIds")]
        public List<string> SentenceIds { get; set; } = new();

        [JsonPropertyName("spans")]
        public List<TextSpan> Spans { get; set; } = new();

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("confidenceLevel")]
        public ConfidenceLevel ConfidenceLevel { get; set; }

        [JsonPropertyName("rationale")]
        public string Rationale { get; set; } = string.Empty;

        // Attribute
        [JsonPropertyName("isIdentifier")]
        public bool IsIdentifier { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }
    }
}
