namespace Texter.Model.Conceptual
{
    /// <summary>Character range in the original requirements text.</summary>
    public record TextSpan(int Start, int Length)
    {
        public int End => Start + Length;
    }
}
