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
    /// TEXTER step 1 in a single LLM call (fact-mediated): the requirements, split into sentences S1, S2, ..., go to
    /// the LLM, which first writes down the facts the text states (F1, F2, ..., each read from sentences) and then builds
    /// the PMER model from those facts only. Every element cites its facts and says whether it is stated, inferred or
    /// assumed; weak entities and aggregations also cite the facts behind that decision. The cited facts lead back to
    /// the sentences, which are highlighted in the text.
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
            var userPrompt = "Verbal description:\n\n" + string.Join("\n", sentences.Select(s => $"[{s.Id}] {s.Text}"));

            var answer = await _llm.CompleteJsonAsync(new LlmJsonRequest(SystemPrompt, userPrompt, "pmer_model", Schema), cancellationToken);
            var raw = JsonSerializer.Deserialize<LlmPmerModel>(answer, Json)
                ?? throw new LlmException("Could not parse the model answer.");

            var mapper = new Mapper(sentences, _classifier);
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

        /// <summary>Raw answer -> conceptual model: facts, ids (E1, E1.A1, R1, G1), entity references, attribution.</summary>
        private sealed class Mapper(List<Sentence> sentences, ConfidenceClassifier classifier)
        {
            private readonly Dictionary<string, Sentence> _sentences = sentences.ToDictionary(s => s.Id);
            private readonly List<string> _sentenceOrder = sentences.Select(s => s.Id).ToList();
            private readonly Dictionary<string, Fact> _facts = new();
            private readonly Dictionary<string, string> _entityIds = new(StringComparer.OrdinalIgnoreCase);
            private readonly SortedSet<string> _unknownFacts = new();

            public List<string> Problems { get; } = new();

            public ConceptualModel Map(LlmPmerModel raw)
            {
                var model = new ConceptualModel();

                foreach (var f in raw.Facts)
                {
                    var fact = new Fact
                    {
                        Id = f.Id,
                        Statement = f.Statement,
                        SentenceIds = f.SentenceIds.Where(_sentences.ContainsKey).Distinct().ToList()
                    };
                    if (_facts.TryAdd(fact.Id, fact)) model.Facts.Add(fact);
                    else Problems.Add($"Duplicate fact '{f.Id}' was ignored.");
                }

                var owners = new Dictionary<string, string>();
                foreach (var (e, i) in raw.Entities.Select((e, i) => (e, i)))
                {
                    var weakFacts = e.IsWeak ? Facts(e.WeakEvidence) : new List<string>();
                    var entity = Attribute(new Entity
                    {
                        Id = $"E{i + 1}",
                        Name = e.Name,
                        DisplayName = e.DisplayName,
                        Kind = e.IsWeak ? EntityKind.Weak : EntityKind.Normal,
                        WeakFactIds = weakFacts
                    }, e.Evidence, e.Confidence, e.Rationale, decisionFacts: weakFacts, hasUnsupportedDecision: e.IsWeak && weakFacts.Count == 0);
                    entity.Attributes = Attributes(entity.Id, e.Attributes);

                    if (!_entityIds.TryAdd(Key(e.Name), entity.Id))
                    {
                        Problems.Add($"Duplicate entity '{e.Name}' was ignored.");
                        continue;
                    }
                    model.Entities.Add(entity);
                    if (e.IsWeak) owners[entity.Id] = e.OwnerEntity;
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

                    var isAggregation = r.IsAggregation || r.Attributes.Count > 0;
                    var aggregationFacts = isAggregation ? Facts(r.AggregationEvidence) : new List<string>();
                    var relationship = new Relationship
                    {
                        Id = $"R{model.Relationships.Count + 1}",
                        Name = r.Name,
                        DisplayName = r.DisplayName,
                        IsWeak = r.IsIdentifying,
                        IsAggregation = isAggregation,
                        AggregationFactIds = aggregationFacts,
                        Mapping1 = mapping1,
                        Mapping2 = mapping2
                    };
                    relationship.Attributes = Attributes(relationship.Id, r.Attributes);
                    model.Relationships.Add(Attribute(relationship, r.Evidence, r.Confidence, r.Rationale,
                        decisionFacts: aggregationFacts,
                        hasAssumedBounds: mapping1.IsAssumed || mapping2.IsAssumed,
                        hasUnsupportedDecision: r.IsAggregation && aggregationFacts.Count == 0));
                }

                foreach (var g in raw.Generalizations)
                {
                    var superId = EntityId(g.SuperEntity);
                    var subIds = g.SubEntities.Select(EntityId).OfType<string>().Where(id => id != superId).Distinct().ToList();
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
                        SubEntityIds = subIds,
                        IsDisjoint = g.IsDisjoint,
                        IsTotal = g.IsTotal
                    }, g.Evidence, g.Confidence, g.Rationale));
                }

                CheckWeakEntities(model, owners);
                if (_unknownFacts.Count > 0)
                    Problems.Add($"Cited facts that were never stated were ignored: {string.Join(", ", _unknownFacts)}.");
                return model;
            }

            /// <summary>Every weak entity needs an identifying relationship with the owner it names.</summary>
            private void CheckWeakEntities(ConceptualModel model, Dictionary<string, string> owners)
            {
                foreach (var entity in model.Entities.Where(e => e.Kind == EntityKind.Weak))
                {
                    var ownerId = EntityId(owners[entity.Id]);
                    var identified = model.Relationships.Any(r =>
                        r.IsWeak && Connects(r, entity.Id) && (ownerId is null || Connects(r, ownerId)));
                    if (!identified)
                        Problems.Add($"Weak entity '{entity.Name}' has no identifying relationship with its owner '{owners[entity.Id]}'.");
                }
            }

            private static bool Connects(Relationship r, string entityId) =>
                r.Mapping1.DomainEntityId == entityId || r.Mapping2.DomainEntityId == entityId;

            private List<Attribute> Attributes(string ownerId, List<LlmAttribute> attributes) =>
                attributes.Select((a, i) => Attribute(new Attribute
                {
                    Id = $"{ownerId}.A{i + 1}",
                    Name = a.Name,
                    Type = a.Type,
                    Length = a.Length,
                    IsIdentifier = a.IsIdentifier,
                    IsMultivalued = a.IsMultivalued,
                    IsOptional = a.IsOptional
                }, a.Evidence, a.Confidence, a.Rationale)).ToList();

            private Mapping? Mapping(LlmMapping m)
            {
                var domain = EntityId(m.FromEntity);
                var coDomain = EntityId(m.ToEntity);
                if (domain is null || coDomain is null) return null;

                return new Mapping
                {
                    Role = m.Role,
                    DomainEntityId = domain,
                    CoDomainEntityId = coDomain,
                    LowerBound = m.LowerBound,
                    UpperBound = m.UpperBound < 0 ? Model.Conceptual.Mapping.Many : m.UpperBound,
                    IsAssumed = !m.BoundsStated
                };
            }

            /// <summary>
            /// Attribution: the cited facts, the sentences they (and the facts of the element's decision) are read from,
            /// and the display level. An element that cites no fact is assumed, whatever the LLM said.
            /// </summary>
            private T Attribute<T>(T element, List<string> evidence, string confidence, string rationale,
                List<string>? decisionFacts = null, bool hasAssumedBounds = false, bool hasUnsupportedDecision = false) where T : ModelElement
            {
                element.FactIds = Facts(evidence);
                element.Confidence = element.FactIds.Count == 0 ? ConfidenceCategory.Assumed : Category(confidence);
                element.Rationale = rationale;

                element.SentenceIds = element.FactIds.Concat(decisionFacts ?? new List<string>())
                    .SelectMany(id => _facts[id].SentenceIds)
                    .Distinct()
                    .OrderBy(_sentenceOrder.IndexOf)
                    .ToList();
                element.Spans = element.SentenceIds.Select(id => new TextSpan(_sentences[id].Start, _sentences[id].Length)).ToList();

                element.ConfidenceLevel = classifier.Classify(element.Confidence, element.Spans.Count > 0, hasAssumedBounds, hasUnsupportedDecision);
                return element;
            }

            /// <summary>Known fact ids, in citation order; unknown ones are reported once.</summary>
            private List<string> Facts(IEnumerable<string> ids)
            {
                var known = new List<string>();
                foreach (var id in ids.Distinct())
                {
                    if (_facts.ContainsKey(id)) known.Add(id);
                    else _unknownFacts.Add(id);
                }
                return known;
            }

            private static ConfidenceCategory Category(string confidence) => confidence.ToLowerInvariant() switch
            {
                "stated" => ConfidenceCategory.Stated,
                "inferred" => ConfidenceCategory.Inferred,
                _ => ConfidenceCategory.Assumed
            };

            private string? EntityId(string name) => _entityIds.GetValueOrDefault(Key(name));

            private static string Key(string name) => new(name.Where(char.IsLetterOrDigit).ToArray());
        }
    }
}
