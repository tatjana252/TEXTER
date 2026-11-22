using System.Reflection;
using System.Text.Json;
using Texter.Logic.Llm;
using Texter.Logic.Traceability;
using Texter.Model.Conceptual;
using Texter.Model.Traceability;
using Attribute = Texter.Model.Conceptual.Attribute;

namespace Texter.Logic.Extraction
{
    /// <summary>
    /// TEXTER step 1 in a single LLM call: the requirements, split into sentences S1, S2, ..., go to the LLM,
    /// which returns the PMER model with an attribution for every element (sentences, verbatim evidence,
    /// confidence, rationale). The evidence is then located in the text and the confidence classified.
    /// </summary>
    public class LlmConceptualModelExtractor : IConceptualModelExtractor
    {
        private static readonly string SystemPrompt = Resource("pmer-extraction.system.md");
        private static readonly string Schema = Resource("pmer-extraction.schema.json");
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly ILlmClient _llm;
        private readonly ConfidenceClassifier _classifier;

        public LlmConceptualModelExtractor(ILlmClient llm, ConfidenceClassifier classifier)
        {
            _llm = llm;
            _classifier = classifier;
        }

        public async Task<ExtractionResult> ExtractAsync(string requirements, CancellationToken cancellationToken = default)
        {
            var sentences = SentenceSplitter.Split(requirements);
            var userPrompt = "Requirements: " + JsonSerializer.Serialize(sentences.Select(s => new { s.Id, s.Text }), Json);

            var answer = await _llm.CompleteJsonAsync(new LlmJsonRequest(SystemPrompt, userPrompt, "pmer_model", Schema), cancellationToken);
            var raw = JsonSerializer.Deserialize<LlmPmerModel>(answer, Json)
                ?? throw new LlmException("Could not parse the model answer.");

            var mapper = new Mapper(sentences, new EvidenceLocator(requirements), _classifier);
            var model = mapper.Map(raw);
            return new ExtractionResult(model, sentences, raw.Notes.Concat(mapper.Problems).ToList());
        }

        private static string Resource(string fileName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + fileName, StringComparison.Ordinal));
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            return reader.ReadToEnd();
        }

        /// <summary>Raw answer -> conceptual model: ids (E1, E1.A1, R1, G1), entity references, attribution.</summary>
        private sealed class Mapper(List<Sentence> sentences, EvidenceLocator locator, ConfidenceClassifier classifier)
        {
            private readonly Dictionary<string, Sentence> _sentences = sentences.ToDictionary(s => s.Id);
            private readonly Dictionary<string, string> _entityIds = new(StringComparer.OrdinalIgnoreCase);

            public List<string> Problems { get; } = new();

            public ConceptualModel Map(LlmPmerModel raw)
            {
                var model = new ConceptualModel();

                foreach (var (e, i) in raw.Entities.Select((e, i) => (e, i)))
                {
                    var entity = Attribute(new Entity
                    {
                        Id = $"E{i + 1}",
                        Name = e.Name,
                        Kind = e.Kind == "weak" ? EntityKind.Weak : EntityKind.Normal
                    }, e.Attribution);
                    entity.Attributes = Attributes(entity.Id, e.Attributes);

                    if (_entityIds.TryAdd(Key(e.Name), entity.Id)) model.Entities.Add(entity);
                    else Problems.Add($"Duplicate entity '{e.Name}' was ignored.");
                }

                foreach (var r in raw.Relationships)
                {
                    var mapping1 = Mapping(r.Mapping1);
                    var mapping2 = Mapping(r.Mapping2);
                    if (mapping1 is null || mapping2 is null)
                    {
                        Problems.Add($"Relationship '{r.Name}' refers to an unknown entity and was ignored.");
                        continue;
                    }

                    var relationship = new Relationship
                    {
                        Id = $"R{model.Relationships.Count + 1}",
                        Name = r.Name,
                        IsWeak = r.IsWeak,
                        Mapping1 = mapping1,
                        Mapping2 = mapping2
                    };
                    relationship.Attributes = Attributes(relationship.Id, r.Attributes);
                    model.Relationships.Add(Attribute(relationship, r.Attribution, hasAssumedBounds: mapping1.IsAssumed || mapping2.IsAssumed));
                }

                foreach (var g in raw.Generalizations)
                {
                    var superId = EntityId(g.SuperEntity);
                    var subIds = g.SubEntities.Select(EntityId).OfType<string>().ToList();
                    if (superId is null || subIds.Count == 0)
                    {
                        Problems.Add($"Generalization of '{g.SuperEntity}' refers to unknown entities and was ignored.");
                        continue;
                    }

                    model.Generalizations.Add(Attribute(new Generalization
                    {
                        Id = $"G{model.Generalizations.Count + 1}",
                        Name = "IS-A",
                        SuperEntityId = superId,
                        SubEntityIds = subIds
                    }, g.Attribution));
                }

                return model;
            }

            private List<Attribute> Attributes(string ownerId, List<LlmAttribute> attributes) =>
                attributes.Select((a, i) => Attribute(new Attribute
                {
                    Id = $"{ownerId}.A{i + 1}",
                    Name = a.Name,
                    Type = a.Type,
                    IsIdentifier = a.IsIdentifier
                }, a.Attribution)).ToList();

            private Mapping? Mapping(LlmMapping m)
            {
                var domain = EntityId(m.Domain);
                var coDomain = EntityId(m.CoDomain);
                if (domain is null || coDomain is null) return null;

                return new Mapping
                {
                    Name = m.Name,
                    DomainEntityId = domain,
                    CoDomainEntityId = coDomain,
                    LowerBound = m.LowerBound,
                    UpperBound = m.UpperBound < 0 ? Model.Conceptual.Mapping.Many : m.UpperBound,
                    IsAssumed = !m.BoundsStated
                };
            }

            /// <summary>
            /// Attribution: cited sentences, evidence located in the text (whole sentences when an excerpt cannot be found),
            /// confidence capped when nothing is traceable or the bounds were assumed.
            /// </summary>
            private T Attribute<T>(T element, LlmAttribution attribution, bool hasAssumedBounds = false) where T : ModelElement
            {
                element.SentenceIds = attribution.SentenceIds.Where(_sentences.ContainsKey).Distinct().ToList();
                element.Evidence = attribution.Evidence;
                element.Rationale = attribution.Rationale;
                element.Confidence = Math.Clamp(attribution.Confidence, 0, 1);

                element.Spans = locator.LocateAll(attribution.Evidence);
                if (element.Spans.Count == 0)
                    element.Spans = element.SentenceIds.Select(id => new TextSpan(_sentences[id].Start, _sentences[id].Length)).ToList();

                element.ConfidenceLevel = classifier.Classify(element.Confidence, element.Spans.Count > 0, hasAssumedBounds);
                return element;
            }

            private string? EntityId(string name) => _entityIds.GetValueOrDefault(Key(name));

            private static string Key(string name) => new(name.Where(char.IsLetterOrDigit).ToArray());
        }
    }
}
