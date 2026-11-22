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

        private static async Task<ConceptualModel> ExtractSampleAsync()
        {
            var llm = new FakeLlmClient(Options.Create(new LlmOptions { FakeResponsePath = Path.Combine(SamplesDir, "library-response.json") }));
            var extractor = new LlmConceptualModelExtractor(llm, new ConfidenceClassifier(Options.Create(new ConfidenceOptions())));
            var text = await File.ReadAllTextAsync(Path.Combine(SamplesDir, "library-requirements.txt"));
            return await extractor.ExtractAsync(text);
        }

        [Fact]
        public async Task Elements_get_spans_and_confidence_levels()
        {
            var model = await ExtractSampleAsync();

            var member = model.Entities.Single(e => e.Name == "Member");
            Assert.Equal(ConfidenceLevel.High, member.ConfidenceLevel);
            Assert.NotEmpty(member.Spans);

            var author = model.Entities.Single(e => e.Name == "Book").Attributes.Single(a => a.Name == "author");
            Assert.Equal(ConfidenceLevel.Medium, author.ConfidenceLevel);
        }

        [Fact]
        public async Task Element_without_evidence_in_text_is_low()
        {
            var model = await ExtractSampleAsync();

            var employeeId = model.Entities.Single(e => e.Name == "Employee").Attributes.Single();
            Assert.Empty(employeeId.Spans);
            Assert.Equal(ConfidenceLevel.Low, employeeId.ConfidenceLevel);
        }

        [Fact]
        public async Task Participants_and_generalizations_are_resolved_to_entity_ids()
        {
            var model = await ExtractSampleAsync();
            var ids = model.Entities.ToDictionary(e => e.Name, e => e.Id);

            var borrows = model.Relationships.Single(r => r.Name == "borrows");
            Assert.Equal(new[] { ids["Member"], ids["Copy"] }, borrows.Participants.Select(p => p.EntityId));

            var generalization = model.Generalizations.Single();
            Assert.Equal(ids["Employee"], generalization.SuperEntityId);
            Assert.Equal(2, generalization.SubEntityIds.Count);
        }

        [Fact]
        public async Task Diagram_links_point_to_existing_nodes()
        {
            var diagram = ConvertToDiagram.Convert(await ExtractSampleAsync());
            var keys = diagram.Nodes.Select(n => n.Key).ToHashSet();

            Assert.All(diagram.Links, l => Assert.True(keys.Contains(l.From) && keys.Contains(l.To)));
            Assert.Contains(diagram.Nodes, n => n.Category == NodeCategory.WeakEntity);
            Assert.Contains(diagram.Links, l => l.Category == LinkCategory.Participation && l.IsTotal);
        }
    }
}
