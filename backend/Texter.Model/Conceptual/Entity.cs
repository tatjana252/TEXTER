namespace Texter.Model.Conceptual
{
    public class Entity : ModelElement
    {
        public bool IsWeak { get; set; }
        public List<Attribute> Attributes { get; set; } = new();
    }
}
