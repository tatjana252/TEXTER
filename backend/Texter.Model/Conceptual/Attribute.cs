namespace Texter.Model.Conceptual
{
    public class Attribute : ModelElement
    {
        /// <summary>Part of the entity identifier (# on the diagram).</summary>
        public bool IsIdentifier { get; set; }

        /// <summary>ERLD built-in type: int, string, decimal, money, date, ...</summary>
        public string Type { get; set; } = "string";

        /// <summary>Length for string / char.</summary>
        public int? Length { get; set; }

        /// <summary>Several values per instance (string(20)[] in ERLD).</summary>
        public bool IsMultivalued { get; set; }

        public bool IsOptional { get; set; }
    }
}
