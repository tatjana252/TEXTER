namespace Texter.Model.Conceptual
{
    /// <summary>IS-A: generalization of subtypes into a supertype.</summary>
    public class Generalization : ModelElement
    {
        public string SuperEntityId { get; set; } = default!;
        public List<string> SubEntityIds { get; set; } = new();

        /// <summary>An instance belongs to at most one subtype.</summary>
        public bool IsDisjoint { get; set; }

        /// <summary>Every instance belongs to some subtype.</summary>
        public bool IsTotal { get; set; }
    }
}
