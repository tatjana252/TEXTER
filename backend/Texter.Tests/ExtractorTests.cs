using Microsoft.Extensions.Options;
using Texter.Logic.Diagram;
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
        public async Task Elements_are_attributed_to_text()
        {
            var model = (await ExtractSampleAsync()).Model;

            var member = model.Entities.Single(e => e.Name == "Member");
            Assert.Equal(new[] { "S1", "S2" }, member.SentenceIds);
            Assert.NotEmpty(member.Spans);
            Assert.Equal(ConfidenceLevel.High, member.ConfidenceLevel);
        }

        [Fact]
        public async Task Element_without_supporting_text_is_low()
        {
            var model = (await ExtractSampleAsync()).Model;

            var employeeId = model.Entities.Single(e => e.Name == "Employee").Attributes.Single();
            Assert.Empty(employeeId.Spans);
            Assert.Equal(ConfidenceLevel.Low, employeeId.ConfidenceLevel);
        }

        [Fact]
        public async Task Relationship_with_assumed_bounds_is_capped_at_medium()
        {
            var manage = (await ExtractSampleAsync()).Model.Relationships.Single(r => r.Name == "manage");

            Assert.Equal(0.85, manage.Confidence);
            Assert.True(manage.Mapping2.IsAssumed);
            Assert.Equal(ConfidenceLevel.Medium, manage.ConfidenceLevel);
        }

        [Fact]
        public async Task Mappings_reference_entities_and_bounds_are_formatted()
        {
            var model = (await ExtractSampleAsync()).Model;
            var ids = model.Entities.ToDictionary(e => e.Name, e => e.Id);

            var has = model.Relationships.Single(r => r.Name == "has");
            Assert.Equal(ids["Book"], has.Mapping1.DomainEntityId);
            Assert.Equal("(0,M)", has.Mapping1.ToString());
            Assert.Equal("(1,1)", has.Mapping2.ToString());
        }

        [Fact]
        public async Task Diagram_links_point_to_existing_nodes()
        {
            var diagram = ConvertToDiagram.Convert((await ExtractSampleAsync()).Model);
            var keys = diagram.Nodes.Select(n => n.Key).ToHashSet();

            Assert.All(diagram.Links, l => Assert.True(keys.Contains(l.From) && keys.Contains(l.To)));
            Assert.Contains(diagram.Nodes, n => n.Category == NodeCategory.Aggregation);
            Assert.Contains(diagram.Nodes, n => n.Category == NodeCategory.WeakRelationship);
            Assert.Contains(diagram.Links, l => l.Category == LinkCategory.Mapping && l.IsAssumed);
        }
    }
}
