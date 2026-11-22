namespace Texter.Model.Conceptual
{
    /// <summary>
    /// Display level of a model element (green / yellow / red in the UI): its confidence category,
    /// capped when the element cannot be traced to the text, its bounds were assumed or a decision has no supporting fact.
    /// </summary>
    public enum ConfidenceLevel
    {
        Low,
        Medium,
        High
    }
}
