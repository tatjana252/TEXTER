namespace Texter.Logic.Extraction
{
    // Raw shape of the LLM answer (mirrors Resources/pmer-extraction.schema.json).

    internal record LlmPmerModel(
        List<LlmEntity> Entities,
        List<LlmRelationship> Relationships,
        List<LlmGeneralization> Generalizations,
        List<string> Notes);

    internal record LlmEntity(string Name, string Kind, List<LlmAttribute> Attributes, LlmAttribution Attribution);

    internal record LlmAttribute(string Name, string Type, bool IsIdentifier, LlmAttribution Attribution);

    internal record LlmRelationship(
        string Name,
        LlmMapping Mapping1,
        LlmMapping Mapping2,
        bool IsWeak,
        List<LlmAttribute> Attributes,
        LlmAttribution Attribution);

    internal record LlmMapping(string? Name, string Domain, string CoDomain, int LowerBound, int UpperBound, bool BoundsStated);

    internal record LlmGeneralization(string SuperEntity, List<string> SubEntities, LlmAttribution Attribution);

    internal record LlmAttribution(List<string> SentenceIds, List<string> Evidence, double Confidence, string Rationale);
}
