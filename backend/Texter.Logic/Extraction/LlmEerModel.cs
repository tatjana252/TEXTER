namespace Texter.Logic.Extraction
{
    // Raw shape of the LLM answer (mirrors Resources/eer-extraction.schema.json).

    internal class LlmEerModel
    {
        public List<LlmEntity> Entities { get; set; } = new();
        public List<LlmRelationship> Relationships { get; set; } = new();
        public List<LlmGeneralization> Generalizations { get; set; } = new();
    }

    internal abstract class LlmElement
    {
        public List<string> Evidence { get; set; } = new();
        public double Confidence { get; set; }
        public string Rationale { get; set; } = string.Empty;
    }

    internal class LlmEntity : LlmElement
    {
        public string Name { get; set; } = default!;
        public bool IsWeak { get; set; }
        public List<LlmAttribute> Attributes { get; set; } = new();
    }

    internal class LlmAttribute : LlmElement
    {
        public string Name { get; set; } = default!;
        public bool IsKey { get; set; }
        public bool IsMultivalued { get; set; }
        public bool IsDerived { get; set; }
    }

    internal class LlmRelationship : LlmElement
    {
        public string Name { get; set; } = default!;
        public bool IsIdentifying { get; set; }
        public List<LlmParticipant> Participants { get; set; } = new();
        public List<LlmAttribute> Attributes { get; set; } = new();
    }

    internal class LlmParticipant
    {
        public string Entity { get; set; } = default!;
        public string Cardinality { get; set; } = "N";
        public bool IsTotal { get; set; }
        public string? Role { get; set; }
    }

    internal class LlmGeneralization : LlmElement
    {
        public string SuperEntity { get; set; } = default!;
        public List<string> SubEntities { get; set; } = new();
        public bool IsDisjoint { get; set; }
        public bool IsTotal { get; set; }
    }
}
