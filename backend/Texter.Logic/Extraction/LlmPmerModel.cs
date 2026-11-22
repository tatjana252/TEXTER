namespace Texter.Logic.Extraction
{
    // Raw shape of the LLM answer (mirrors Resources/pmer-extraction.schema.json).

    internal record LlmPmerModel(
        List<LlmFact> Facts,
        List<LlmEntity> Entities,
        List<LlmRelationship> Relationships,
        List<LlmGeneralization> Generalizations,
        List<string> Notes);

    internal record LlmFact(string Id, List<string> SentenceIds, string Statement);

    internal record LlmEntity(
        List<string> Evidence,
        string Name,
        string? DisplayName,
        bool IsWeak,
        string OwnerEntity,
        List<string> WeakEvidence,
        List<LlmAttribute> Attributes,
        string Confidence,
        string Rationale);

    internal record LlmAttribute(
        List<string> Evidence,
        string Name,
        string Type,
        int? Length,
        bool IsIdentifier,
        bool IsMultivalued,
        bool IsOptional,
        string Confidence,
        string Rationale);

    internal record LlmRelationship(
        List<string> Evidence,
        string Name,
        string? DisplayName,
        LlmMapping Mapping1,
        LlmMapping Mapping2,
        bool IsIdentifying,
        bool IsAggregation,
        List<string> AggregationEvidence,
        List<LlmAttribute> Attributes,
        string Confidence,
        string Rationale);

    internal record LlmMapping(string FromEntity, string ToEntity, string Role, int LowerBound, int UpperBound, bool BoundsStated);

    internal record LlmGeneralization(
        List<string> Evidence,
        string SuperEntity,
        List<string> SubEntities,
        bool IsDisjoint,
        bool IsTotal,
        string Confidence,
        string Rationale);
}
