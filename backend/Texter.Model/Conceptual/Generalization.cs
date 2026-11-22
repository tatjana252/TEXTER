namespace Texter.Model.Conceptual
{
    /// <summary>EER specialization/generalization (superclass with subclasses).</summary>
    public class Generalization : ModelElement
    {
        public string SuperEntityId { get; set; } = default!;
        public List<string> SubEntityIds { get; set; } = new();
        public bool IsDisjoint { get; set; }
        public bool IsTotal { get; set; }
    }
}
