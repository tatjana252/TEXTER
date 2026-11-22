namespace Texter.Model.Conceptual
{
    /// <summary>How directly the cited facts support a model element (TEXTER self-assessment).</summary>
    public enum ConfidenceCategory
    {
        /// <summary>A cited fact names the element; it was only written in modeling form.</summary>
        Stated,

        /// <summary>Reached through a modeling decision: facts combined, a pattern recognised, a representation chosen.</summary>
        Inferred,

        /// <summary>No fact supports it; added by modeling convention.</summary>
        Assumed
    }
}
