using System.Text.Json.Serialization;

namespace Texter.Model.GoJs
{
    /// <summary>GoJS GraphLinksModel data.</summary>
    public class DiagramModel
    {
        [JsonPropertyName("nodes")]
        public List<Node> Nodes { get; set; } = new();

        [JsonPropertyName("links")]
        public List<Link> Links { get; set; } = new();
    }
}
