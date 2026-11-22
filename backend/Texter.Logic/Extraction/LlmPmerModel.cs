namespace Texter.Logic.Extraction
{
    // Raw shape of the LLM answer (mirrors Resources/pmer-extraction.schema.json).

    internal record LlmPmerModel(
        List<LlmEntity> Entities,
        List<LlmRelationship> Relationships,
        List<LlmGeneralization> Generalizations,
        List<string> Notes);

    internal record LlmEntity(string Name, string? DisplayName, string Kind, List<LlmAttribute> Attributes, LlmAttribution Attribution);

    internal record LlmAttribute(string Name, string Type, int? Length, bool IsIdentifier, bool IsMultivalued, bool IsOptional, LlmAttribution Attribution);

    internal record LlmRelationship(
        string Name,
        string? DisplayName,
        LlmMapping Mapping1,
        LlmMapping Mapping2,
        bool IsWeak,
        List<LlmAttribute> Attributes,
        LlmAttribution Attribution);

    internal record LlmMapping(string Role, string Domain, string CoDomain, int LowerBound, int UpperBound, bool BoundsStated);

    internal record LlmGeneralization(string SuperEntity, List<string> SubEntities, bool IsDisjoint, bool IsTotal, LlmAttribution Attribution);

    internal record LlmAttribution(List<string> SentenceIds, List<string> Evidence, double Confidence, string Rationale);
}
