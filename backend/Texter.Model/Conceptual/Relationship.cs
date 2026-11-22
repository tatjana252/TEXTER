namespace Texter.Model.Conceptual
{
    public class Relationship : ModelElement
    {
        /// <summary>Identifying relationship of a weak entity.</summary>
        public bool IsIdentifying { get; set; }
        public List<Participant> Participants { get; set; } = new();
        public List<Attribute> Attributes { get; set; } = new();
    }

    public class Participant
    {
        public string EntityId { get; set; } = default!;

        /// <summary>"1" or "N" (Chen notation).</summary>
        public string Cardinality { get; set; } = "N";

        /// <summary>Total (mandatory) participation is drawn as a double line.</summary>
        public bool IsTotal { get; set; }

        public string? Role { get; set; }
    }
}
