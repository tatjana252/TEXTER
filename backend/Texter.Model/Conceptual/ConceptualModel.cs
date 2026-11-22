namespace Texter.Model.Conceptual
{
    /// <summary>PMER (EER) conceptual model extracted from requirements text.</summary>
    public class ConceptualModel
    {
        /// <summary>Facts read from the requirements; the elements cite them.</summary>
        public List<Fact> Facts { get; set; } = new();

        public List<Entity> Entities { get; set; } = new();
        public List<Relationship> Relationships { get; set; } = new();
        public List<Generalization> Generalizations { get; set; } = new();

        /// <summary>All elements, flattened (entities, their attributes, relationships, ...).</summary>
        public IEnumerable<ModelElement> AllElements()
        {
            foreach (var entity in Entities)
            {
                yield return entity;
                foreach (var attribute in entity.Attributes) yield return attribute;
            }
            foreach (var relationship in Relationships)
            {
                yield return relationship;
                foreach (var attribute in relationship.Attributes) yield return attribute;
            }
            foreach (var generalization in Generalizations) yield return generalization;
        }
    }
}
