namespace Texter.Model.Conceptual
{
    public enum EntityKind
    {
        Normal,
        /// <summary>Identification and existence depend on its owner entity.</summary>
        Weak
    }

    public class Entity : ModelElement
    {
        public EntityKind Kind { get; set; }

        /// <summary>Facts showing that a weak entity has no identity outside its owner and is told apart within it.</summary>
        public List<string> WeakFactIds { get; set; } = new();

        /// <summary>Name as written in the requirements, e.g. "Radno mesto".</summary>
        public string? DisplayName { get; set; }
        public List<Attribute> Attributes { get; set; } = new();
    }
}
