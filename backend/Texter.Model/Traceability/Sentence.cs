using System.Text.Json.Serialization;

namespace Texter.Model.Traceability
{
    /// <summary>Requirements sentence with its ID (S1, S2, ...) and position in the original text.</summary>
    public class Sentence
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = default!;

        [JsonPropertyName("text")]
        public string Text { get; set; } = default!;

        [JsonPropertyName("start")]
        public int Start { get; set; }

        [JsonPropertyName("length")]
        public int Length { get; set; }
    }
}
