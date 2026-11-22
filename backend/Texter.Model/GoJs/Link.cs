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

        /// <summary>Mapping bounds, e.g. "(0,M)", shown next to the relationship.</summary>
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        /// <summary>Mapping name, e.g. "borrows".</summary>
        [JsonPropertyName("mappingName")]
        public string? MappingName { get; set; }

        /// <summary>Bounds were assumed (no cue in the text).</summary>
        [JsonPropertyName("isAssumed")]
        public bool IsAssumed { get; set; }
    }
}
