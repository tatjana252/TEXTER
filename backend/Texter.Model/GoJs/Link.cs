using System.Text.Json.Serialization;

namespace Texter.Model.GoJs
{
    public class Link
    {
        [JsonPropertyName("from")]
        public string From { get; set; } = default!;

        [JsonPropertyName("to")]
        public string To { get; set; } = default!;

        [JsonPropertyName("category")]
        public string Category { get; set; } = default!;

        /// <summary>Cardinality label ("1" / "N") for participation links.</summary>
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        /// <summary>Total participation / total specialization, drawn as a double line.</summary>
        [JsonPropertyName("isTotal")]
        public bool IsTotal { get; set; }
    }
}
