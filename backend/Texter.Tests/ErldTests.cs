using Microsoft.Extensions.Options;
using Texter.Erl;
using Texter.Logic.Erld;
using Texter.Logic.Extraction;
using Texter.Logic.Llm;
using Texter.Logic.Traceability;
using Texter.Model.Conceptual;
using Xunit.Abstractions;

namespace Texter.Tests
{
    public class ErldTests(ITestOutputHelper output)
    {
        private static readonly string SamplesDir = Path.Combine(AppContext.BaseDirectory, "Samples");

        private static async Task<ConceptualModel> SampleModelAsync()
        {
            var llm = new FakeLlmClient(Options.Create(new LlmOptions { FakeResponsesDir = Path.Combine(SamplesDir, "fake") }));
            var extractor = new LlmConceptualModelExtractor(llm, new ConfidenceClassifier(Options.Create(new ConfidenceOptions())));
            var text = await File.ReadAllTextAsync(Path.Combine(SamplesDir, "library-requirements.txt"));
            return (await extractor.ExtractAsync(text)).Model;
        }

        [Fact]
        public void Reference_example_parses()
        {
            var errors = ErldSyntax.Validate(File.ReadAllText(Path.Combine(SamplesDir, "preduzece.erld")));
            Assert.Empty(errors);
        }

        [Fact]
        public void Syntax_errors_are_reported_with_position()
        {
            var errors = ErldSyntax.Validate("model M;\nentity A {\n  key id int;\n}");
            Assert.Contains(errors, e => e.Line == 3);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Generated_erld_parses(bool includeAttribution)
        {
            var code = ErldGenerator.Generate(await SampleModelAsync(), "Library", includeAttribution);
            output.WriteLine(code);

            Assert.Empty(ErldSyntax.Validate(code));
        }

        [Fact]
        public async Task Generated_erld_uses_pmer_constructs()
        {
            var code = ErldGenerator.Generate(await SampleModelAsync(), "Library");

            Assert.Contains("weak entity Copy {", code);
            Assert.Contains("partial key copyNumber : int;", code);
            Assert.Contains("identifying relationship Has {", code);
            Assert.Contains("Book -> Copy [0..*] as copies on delete cascade;", code);
            Assert.Contains("aggregation Loan as \"loan\":", code);
            Assert.Contains("@assumed Section -> Librarian [0..1] as manager;", code);
            Assert.Contains("@source(S10) @confidence(inferred)\nspecialization of Employee", code);
            Assert.Contains("specialization of Employee (disjoint, total) { Librarian, Technician }", code);
            Assert.Contains("@source(S2) @confidence(stated) key membershipNumber : string(10);", code);
        }

        [Fact]
        public void Keywords_and_spaces_are_quoted()
        {
            Assert.Equal("\"in\"", ErldGenerator.Name("in"));
            Assert.Equal("\"Radno mesto\"", ErldGenerator.Name("Radno mesto"));
            Assert.Equal("datum", ErldGenerator.Name("datum"));
            Assert.Equal("date", ErldGenerator.Name("date")); // soft keyword
        }
    }
}
