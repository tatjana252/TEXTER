namespace Texter.Model.Conceptual
{
    public class Attribute : ModelElement
    {
        /// <summary>Part of the entity identifier (# on the diagram).</summary>
        public bool IsIdentifier { get; set; }

        /// <summary>Predefined or semantic domain, e.g. String, Int, Date.</summary>
        public string Type { get; set; } = "String";
    }
}
