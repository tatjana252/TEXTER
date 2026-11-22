namespace Texter.Model.Conceptual
{
    public class Attribute : ModelElement
    {
        public bool IsKey { get; set; }
        public bool IsMultivalued { get; set; }
        public bool IsDerived { get; set; }
    }
}
