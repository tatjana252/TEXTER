using Texter.Logic.Traceability;

namespace Texter.Tests
{
    public class EvidenceLocatorTests
    {
        private const string Text = "The library keeps track of its members.\nEach member has a unique   membership number, a name and one or more phone numbers.";

        [Fact]
        public void Exact_quote_is_found()
        {
            var span = new EvidenceLocator(Text).Locate("a name");

            Assert.NotNull(span);
            Assert.Equal("a name", Text.Substring(span.Start, span.Length));
        }

        [Fact]
        public void Quote_with_different_case_and_whitespace_maps_back_to_original_text()
        {
            var span = new EvidenceLocator(Text).Locate("A Unique Membership Number");

            Assert.NotNull(span);
            Assert.Equal("a unique   membership number", Text.Substring(span.Start, span.Length));
        }

        [Fact]
        public void Paraphrased_quote_falls_back_to_best_sentence()
        {
            var span = new EvidenceLocator(Text).Locate("library keeps track of members");

            Assert.NotNull(span);
            Assert.Equal("The library keeps track of its members.", Text.Substring(span.Start, span.Length));
        }

        [Fact]
        public void Unrelated_quote_is_not_found()
        {
            Assert.Null(new EvidenceLocator(Text).Locate("employees are paid monthly"));
        }
    }
}
