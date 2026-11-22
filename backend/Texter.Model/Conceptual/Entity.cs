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
        public List<Attribute> Attributes { get; set; } = new();
    }
}
