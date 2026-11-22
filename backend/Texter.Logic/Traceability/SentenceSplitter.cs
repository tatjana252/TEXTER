using System.Text.RegularExpressions;
using Texter.Model.Traceability;

namespace Texter.Logic.Traceability
{
    /// <summary>Splits requirements into sentences S1, S2, ... keeping their positions (basis of the attribution).</summary>
    public static partial class SentenceSplitter
    {
        [GeneratedRegex(@"[^.;!?\r\n]+")]
        private static partial Regex SentenceRegex();

        public static List<Sentence> Split(string text)
        {
            var sentences = new List<Sentence>();
            foreach (Match match in SentenceRegex().Matches(text))
            {
                var trimmed = match.Value.Trim();
                if (trimmed.Length == 0) continue;

                sentences.Add(new Sentence
                {
                    Id = $"S{sentences.Count + 1}",
                    Text = trimmed,
                    Start = match.Index + match.Value.IndexOf(trimmed, StringComparison.Ordinal),
                    Length = trimmed.Length
                });
            }
            return sentences;
        }
    }
}
