using System.Text.Json;
using Texter.Logic.Llm;
using Texter.Logic.Traceability;
using Texter.Model.Conceptual;
using Attribute = Texter.Model.Conceptual.Attribute;

namespace Texter.Logic.Extraction
{
    /// <summary>
    /// Asks the LLM for an EER model with evidence and confidence for each element,
    /// then resolves the evidence to positions in the text and classifies the confidence.
    /// </summary>
    public class LlmConceptualModelExtractor : IConceptualModelExtractor
    {
        private static readonly string SystemPrompt = EmbeddedResource.Read("eer-extraction.system.md");
        private static readonly string Schema = EmbeddedResource.Read("eer-extraction.schema.json");
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly ILlmClient _llm;
        private readonly ConfidenceClassifier _classifier;

        public LlmConceptualModelExtractor(ILlmClient llm, ConfidenceClassifier classifier)
        {
            _llm = llm;
            _classifier = classifier;
        }

        public async Task<ConceptualModel> ExtractAsync(string requirements, CancellationToken cancellationToken = default)
        {
            var request = new LlmJsonRequest(SystemPrompt, $"Requirements:\n\n{requirements}", "eer_model", Schema);
            var answer = await _llm.CompleteJsonAsync(request, cancellationToken);

            var raw = JsonSerializer.Deserialize<LlmEerModel>(answer, JsonOptions)
                ?? throw new LlmException("Could not parse the model answer.");

            return new Mapper(new EvidenceLocator(requirements), _classifier).Map(raw);
        }

        /// <summary>Maps the raw LLM answer to the domain model (ids, name resolution, spans, confidence levels).</summary>
        private sealed class Mapper(EvidenceLocator locator, ConfidenceClassifier classifier)
        {
            private readonly Dictionary<string, string> _entityIdsByName = new(StringComparer.OrdinalIgnoreCase);

            public ConceptualModel Map(LlmEerModel raw)
            {
                var model = new ConceptualModel();

                foreach (var (llmEntity, i) in raw.Entities.Select((e, i) => (e, i)))
                {
                    var entity = Fill(new Entity { Id = $"E{i + 1}", Name = llmEntity.Name, IsWeak = llmEntity.IsWeak }, llmEntity);
                    entity.Attributes = MapAttributes(entity.Id, llmEntity.Attributes);
                    _entityIdsByName.TryAdd(Key(entity.Name), entity.Id);
                    model.Entities.Add(entity);
                }

                foreach (var (llmRelationship, i) in raw.Relationships.Select((r, i) => (r, i)))
                {
                    var relationship = Fill(new Relationship
                    {
                        Id = $"R{i + 1}",
                        Name = llmRelationship.Name,
                        IsIdentifying = llmRelationship.IsIdentifying
                    }, llmRelationship);

                    relationship.Participants = llmRelationship.Participants
                        .Where(p => ResolveEntity(p.Entity) is not null)
                        .Select(p => new Participant
                        {
                            EntityId = ResolveEntity(p.Entity)!,
                            Cardinality = p.Cardinality,
                            IsTotal = p.IsTotal,
                            Role = p.Role
                        })
                        .ToList();
                    relationship.Attributes = MapAttributes(relationship.Id, llmRelationship.Attributes);

                    if (relationship.Participants.Count > 0)
                        model.Relationships.Add(relationship);
                }

                foreach (var (llmGeneralization, i) in raw.Generalizations.Select((g, i) => (g, i)))
                {
                    var superId = ResolveEntity(llmGeneralization.SuperEntity);
                    var subIds = llmGeneralization.SubEntities.Select(ResolveEntity).OfType<string>().ToList();
                    if (superId is null || subIds.Count == 0) continue;

                    model.Generalizations.Add(Fill(new Generalization
                    {
                        Id = $"G{i + 1}",
                        Name = llmGeneralization.IsDisjoint ? "d" : "o",
                        SuperEntityId = superId,
                        SubEntityIds = subIds,
                        IsDisjoint = llmGeneralization.IsDisjoint,
                        IsTotal = llmGeneralization.IsTotal
                    }, llmGeneralization));
                }

                return model;
            }

            private List<Attribute> MapAttributes(string ownerId, List<LlmAttribute> attributes) =>
                attributes.Select((a, i) => Fill(new Attribute
                {
                    Id = $"{ownerId}.A{i + 1}",
                    Name = a.Name,
                    IsKey = a.IsKey,
                    IsMultivalued = a.IsMultivalued,
                    IsDerived = a.IsDerived
                }, a)).ToList();

            private T Fill<T>(T element, LlmElement source) where T : ModelElement
            {
                element.Confidence = Math.Clamp(source.Confidence, 0, 1);
                element.Evidence = source.Evidence;
                element.Rationale = source.Rationale;
                element.Spans = locator.LocateAll(source.Evidence);
                element.ConfidenceLevel = classifier.Classify(element.Confidence, isTraceable: element.Spans.Count > 0);
                return element;
            }

            private string? ResolveEntity(string name) => _entityIdsByName.GetValueOrDefault(Key(name));

            private static string Key(string name) => new(name.Where(char.IsLetterOrDigit).ToArray());
        }
    }
}
