namespace Texter.Model.Conceptual
{
    /// <summary>
    /// PMER relationship R(E1, E2), described by two mappings: E1 => E2 (DG, GG) and E2 => E1 (DG, GG).
    /// A relationship that has its own attributes is an aggregation.
    /// </summary>
    public class Relationship : ModelElement
    {
        /// <summary>Name as written in the requirements, e.g. "radi na".</summary>
        public string? DisplayName { get; set; }

        public Mapping Mapping1 { get; set; } = new();
        public Mapping Mapping2 { get; set; } = new();

        /// <summary>Weak entity relationship (owner - weak entity).</summary>
        public bool IsWeak { get; set; }

        public List<Attribute> Attributes { get; set; } = new();

        public bool IsAggregation => Attributes.Count > 0;
    }

    /// <summary>Mapping Domain => CoDomain with lower (DG) and upper (GG) bound.</summary>
    public class Mapping
    {
        /// <summary>Navigation name from the domain to the co-domain, e.g. radnik.radnoMesto, radnoMesto.radnici.</summary>
        public string? Role { get; set; }
        public string DomainEntityId { get; set; } = default!;
        public string CoDomainEntityId { get; set; } = default!;
        public int LowerBound { get; set; }

        /// <summary>-1 means M (many).</summary>
        public int UpperBound { get; set; } = Many;

        /// <summary>No quantifier/modality cue in the text supports the bounds; they were assumed.</summary>
        public bool IsAssumed { get; set; }

        public const int Many = -1;

        public override string ToString() => $"({LowerBound},{(UpperBound == Many ? "M" : UpperBound.ToString())})";
    }
}
