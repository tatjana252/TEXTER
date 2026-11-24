using Microsoft.Extensions.Options;
using Texter.Erl;
using Texter.Logic.Diagram;
using Texter.Logic.Erld;
using Texter.Logic.Extraction;
using Texter.Logic.Llm;
using Texter.Logic.Traceability;
using Texter.Model.Conceptual;
using Texter.Model.GoJs;

namespace Texter.Tests
{
    public class ExtractorTests
    {
        private static readonly string SamplesDir = Path.Combine(AppContext.BaseDirectory, "Samples");

        private static async Task<ExtractionResult> ExtractSampleAsync()
        {
            var llm = new FakeLlmClient(Options.Create(new LlmOptions { FakeResponsesDir = Path.Combine(SamplesDir, "fake") }));
            var extractor = new LlmConceptualModelExtractor(llm, new ConfidenceClassifier(Options.Create(new ConfidenceOptions())));
            var text = await File.ReadAllTextAsync(Path.Combine(SamplesDir, "library-requirements.txt"));
            return await extractor.ExtractAsync(text);
        }

        [Fact]
        public async Task Sentences_are_numbered_with_positions()
        {
            var result = await ExtractSampleAsync();

            Assert.Equal(12, result.Sentences.Count);
            Assert.Equal("S10", result.Sentences[9].Id);
            Assert.Equal("An employee is either a librarian or a technician", result.Sentences[9].Text);
        }

        [Fact]
        public async Task Facts_are_read_from_sentences()
        {
            var facts = (await ExtractSampleAsync()).Model.Facts;

            Assert.Equal(19, facts.Count);
            var f11 = facts.Single(f => f.Id == "F11");
            Assert.Equal(new[] { "S6" }, f11.SentenceIds);
            Assert.Equal("a copy is identified by its copy number within the book", f11.Statement);
        }

        [Fact]
        public async Task Elements_cite_facts_that_lead_to_sentences()
        {
            var model = (await ExtractSampleAsync()).Model;

            var member = model.Entities.Single(e => e.Name == "Member");
            Assert.Equal(new[] { "F1", "F2" }, member.FactIds);
            Assert.Equal(new[] { "S1", "S2" }, member.SentenceIds);
            Assert.Equal(2, member.Spans.Count);
            Assert.Equal(ConfidenceCategory.Stated, member.Confidence);
            Assert.Equal(ConfidenceLevel.High, member.ConfidenceLevel);
        }

        [Fact]
        public async Task Weak_entity_cites_the_facts_of_its_decision()
        {
            var copy = (await ExtractSampleAsync()).Model.Entities.Single(e => e.Name == "Copy");

            Assert.Equal(EntityKind.Weak, copy.Kind);
            Assert.Equal(new[] { "F11" }, copy.WeakFactIds);
            Assert.Equal(new[] { "S5", "S6" }, copy.SentenceIds);
            Assert.Equal(ConfidenceLevel.Medium, copy.ConfidenceLevel);
        }

        [Fact]
        public async Task Element_without_facts_is_assumed_and_low()
        {
            var model = (await ExtractSampleAsync()).Model;

            var employeeId = model.Entities.Single(e => e.Name == "Employee").Attributes.Single();
            Assert.Empty(employeeId.FactIds);
            Assert.Empty(employeeId.Spans);
            Assert.Equal(ConfidenceCategory.Assumed, employeeId.Confidence);
            Assert.Equal(ConfidenceLevel.Low, employeeId.ConfidenceLevel);
        }

        [Fact]
        public async Task Relationship_with_assumed_bounds_is_capped_at_medium()
        {
            var manage = (await ExtractSampleAsync()).Model.Relationships.Single(r => r.Name == "Manages");

            Assert.Equal(ConfidenceCategory.Stated, manage.Confidence);
            Assert.True(manage.Mapping2.IsAssumed);
            Assert.Equal(ConfidenceLevel.Medium, manage.ConfidenceLevel);
        }

        [Fact]
        public async Task Mappings_reference_entities_and_bounds_are_formatted()
        {
            var model = (await ExtractSampleAsync()).Model;
            var ids = model.Entities.ToDictionary(e => e.Name, e => e.Id);

            var has = model.Relationships.Single(r => r.Name == "Has");
            Assert.Equal(ids["Book"], has.Mapping1.DomainEntityId);
            Assert.Equal("(0,M)", has.Mapping1.ToString());
            Assert.Equal("(1,1)", has.Mapping2.ToString());
        }

        [Fact]
        public async Task Unsupported_decisions_and_unknown_facts_are_flagged()
        {
            const string answer = """
                {
                  "facts": [ { "id": "F1", "sentenceIds": ["S1"], "statement": "an order has lines" } ],
                  "entities": [
                    { "evidence": ["F1"], "name": "Order", "displayName": null, "isWeak": false, "ownerEntity": "", "weakEvidence": [],
                      "attributes": [], "confidence": "stated", "rationale": "" },
                    { "evidence": ["F1"], "name": "Line", "displayName": null, "isWeak": true, "ownerEntity": "Order", "weakEvidence": [],
                      "attributes": [
                        { "evidence": ["F9"], "name": "price", "type": "money", "length": null, "isIdentifier": false,
                          "isMultivalued": false, "isOptional": false, "confidence": "stated", "rationale": "" } ],
                      "confidence": "stated", "rationale": "" }
                  ],
                  "relationships": [], "generalizations": [], "notes": []
                }
                """;
            var extractor = new LlmConceptualModelExtractor(new CannedLlm(answer), new ConfidenceClassifier(Options.Create(new ConfidenceOptions())));

            var result = await extractor.ExtractAsync("An order has lines.");
            var line = result.Model.Entities.Single(e => e.Name == "Line");

            Assert.Equal(ConfidenceLevel.Low, line.ConfidenceLevel); // weak, but no fact carries the decision
            var price = line.Attributes.Single();
            Assert.Equal(ConfidenceCategory.Assumed, price.Confidence); // cites only a fact that was never stated
            Assert.Contains(result.Notes, n => n.Contains("F9"));
            Assert.Contains(result.Notes, n => n.Contains("no identifying relationship"));
        }

        [Fact]
        public async Task Aggregation_takes_part_in_a_relationship()
        {
            // The relationship that uses the aggregation comes first in the answer: it must wait for the aggregation.
            const string answer = """
                {
                  "facts": [
                    { "id": "F1", "sentenceIds": ["S1"], "statement": "a vet examines pets" },
                    { "id": "F2", "sentenceIds": ["S2"], "statement": "for each examination the date is recorded" },
                    { "id": "F3", "sentenceIds": ["S3"], "statement": "each examination takes place at one exam spot" }
                  ],
                  "entities": [
                    { "evidence": ["F1"], "name": "Vet", "displayName": null, "isWeak": false, "ownerEntity": "", "weakEvidence": [], "attributes": [], "confidence": "stated", "rationale": "" },
                    { "evidence": ["F1"], "name": "Pet", "displayName": null, "isWeak": false, "ownerEntity": "", "weakEvidence": [], "attributes": [], "confidence": "stated", "rationale": "" },
                    { "evidence": ["F3"], "name": "ExamSpot", "displayName": null, "isWeak": false, "ownerEntity": "", "weakEvidence": [], "attributes": [], "confidence": "stated", "rationale": "" }
                  ],
                  "relationships": [
                    { "evidence": ["F3"], "name": "TakesPlaceAt", "displayName": null,
                      "mapping1": { "fromEntity": "Examination", "toEntity": "ExamSpot", "role": "spot", "lowerBound": 1, "upperBound": 1, "boundsStated": true },
                      "mapping2": { "fromEntity": "ExamSpot", "toEntity": "Examination", "role": "examinations", "lowerBound": 0, "upperBound": -1, "boundsStated": false },
                      "isIdentifying": false, "isAggregation": false, "aggregationEvidence": [], "attributes": [], "confidence": "inferred", "rationale": "the spot belongs to the pair (HR9)" },
                    { "evidence": ["F1"], "name": "Examination", "displayName": null,
                      "mapping1": { "fromEntity": "Vet", "toEntity": "Pet", "role": "pets", "lowerBound": 0, "upperBound": -1, "boundsStated": true },
                      "mapping2": { "fromEntity": "Pet", "toEntity": "Vet", "role": "vets", "lowerBound": 0, "upperBound": -1, "boundsStated": true },
                      "isIdentifying": false, "isAggregation": true, "aggregationEvidence": ["F2"],
                      "attributes": [ { "evidence": ["F2"], "name": "date", "type": "date", "length": null, "isIdentifier": false, "isMultivalued": false, "isOptional": false, "confidence": "stated", "rationale": "" } ],
                      "confidence": "inferred", "rationale": "date depends on the pair (HN5)" }
                  ],
                  "generalizations": [], "notes": []
                }
                """;
            var extractor = new LlmConceptualModelExtractor(new CannedLlm(answer), new ConfidenceClassifier(Options.Create(new ConfidenceOptions())));

            var result = await extractor.ExtractAsync("A vet examines pets. For each examination the date is recorded. Each examination takes place at one exam spot.");
            var model = result.Model;
            var examination = model.Relationships.Single(r => r.Name == "Examination");
            var takesPlaceAt = model.Relationships.Single(r => r.Name == "TakesPlaceAt");

            Assert.Equal(examination.Id, takesPlaceAt.Mapping1.DomainEntityId);
            Assert.DoesNotContain(result.Notes, n => n.Contains("unknown"));
            Assert.Contains(ConvertToDiagram.Convert(model).Links, l => l.From == takesPlaceAt.Id && l.To == examination.Id);

            var code = ErldGenerator.Generate(model, "Clinic");
            Assert.Empty(ErldSyntax.Validate(code));
            Assert.Contains("Examination -> ExamSpot [1..1] as spot;", code);
            Assert.True(code.IndexOf("aggregation Examination", StringComparison.Ordinal) < code.IndexOf("relationship TakesPlaceAt", StringComparison.Ordinal));
        }

        private sealed class CannedLlm(string answer) : ILlmClient
        {
            public Task<string> CompleteJsonAsync(LlmJsonRequest request, CancellationToken cancellationToken = default) => Task.FromResult(answer);
        }

        [Fact]
        public async Task Diagram_links_point_to_existing_nodes()
        {
            var diagram = ConvertToDiagram.Convert((await ExtractSampleAsync()).Model);
            var keys = diagram.Nodes.Select(n => n.Key).ToHashSet();

            Assert.All(diagram.Links, l => Assert.True(keys.Contains(l.From) && keys.Contains(l.To)));
            Assert.Contains(diagram.Nodes, n => n.Category == NodeCategory.Aggregation);
            // The identifying relationship Book -> Copy is an arrow, not a diamond.
            Assert.DoesNotContain(diagram.Nodes, n => n.Category == NodeCategory.WeakRelationship);
            var identifying = diagram.Links.Single(l => l.Category == LinkCategory.Identifying);
            Assert.Equal("(0,M)", identifying.Text);
            Assert.Equal("Has", identifying.Element!.Text);
            Assert.Contains(diagram.Links, l => l.Category == LinkCategory.Mapping && l.IsAssumed);
            Assert.Equal(new[] { "F14", "F15" }, diagram.Nodes.Single(n => n.Text == "Loan").DecisionFactIds);
        }
    }
}
