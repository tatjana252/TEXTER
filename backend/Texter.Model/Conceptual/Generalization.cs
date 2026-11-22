namespace Texter.Model.Conceptual
{
    /// <summary>IS-A: generalization of subtypes into a supertype.</summary>
    public class Generalization : ModelElement
    {
        public string SuperEntityId { get; set; } = default!;
        public List<string> SubEntityIds { get; set; } = new();
    }
}
