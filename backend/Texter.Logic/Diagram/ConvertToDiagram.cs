using Texter.Model.Conceptual;
using Texter.Model.GoJs;
using Attribute = Texter.Model.Conceptual.Attribute;

namespace Texter.Logic.Diagram
{
    /// <summary>Conceptual model -> GoJS model in PMER DER notation.</summary>
    public static class ConvertToDiagram
    {
        public static DiagramModel Convert(ConceptualModel model)
        {
            var result = new DiagramModel();

            foreach (var entity in model.Entities)
            {
                result.Nodes.Add(NodeFor(entity, entity.Kind == EntityKind.Weak ? NodeCategory.WeakEntity : NodeCategory.Entity));
                AddAttributes(result, entity.Id, entity.Attributes);
            }

            foreach (var relationship in model.Relationships)
            {
                var category = relationship.IsAggregation ? NodeCategory.Aggregation
                    : relationship.IsWeak ? NodeCategory.WeakRelationship
                    : NodeCategory.Relationship;
                result.Nodes.Add(NodeFor(relationship, category));

                // The bounds of mapping E => X are written on the line between the relationship and E.
                foreach (var mapping in new[] { relationship.Mapping1, relationship.Mapping2 })
                {
                    result.Links.Add(new Link
                    {
                        From = relationship.Id,
                        To = mapping.DomainEntityId,
                        Category = LinkCategory.Mapping,
                        Text = mapping.ToString(),
                        Role = mapping.Role,
                        IsAssumed = mapping.IsAssumed
                    });
                }
                AddAttributes(result, relationship.Id, relationship.Attributes);
            }

            foreach (var generalization in model.Generalizations)
            {
                result.Nodes.Add(NodeFor(generalization, NodeCategory.Generalization));
                result.Links.Add(new Link { From = generalization.SuperEntityId, To = generalization.Id, Category = LinkCategory.Generalization });
                foreach (var subId in generalization.SubEntityIds)
                    result.Links.Add(new Link { From = generalization.Id, To = subId, Category = LinkCategory.Generalization });
            }

            return result;
        }

        private static void AddAttributes(DiagramModel diagram, string ownerId, IEnumerable<Attribute> attributes)
        {
            foreach (var attribute in attributes)
            {
                var node = NodeFor(attribute, NodeCategory.Attribute);
                node.IsIdentifier = attribute.IsIdentifier;
                node.Type = attribute.Length is { } length ? $"{attribute.Type}({length})" : attribute.Type;
                if (attribute.IsMultivalued) node.Type += "[]";
                diagram.Nodes.Add(node);
                diagram.Links.Add(new Link { From = ownerId, To = attribute.Id, Category = LinkCategory.Attribute });
            }
        }

        private static Node NodeFor(ModelElement element, string category) => new()
        {
            Key = element.Id,
            Category = category,
            Text = element.Name,
            SentenceIds = element.SentenceIds,
            Spans = element.Spans,
            Confidence = element.Confidence,
            ConfidenceLevel = element.ConfidenceLevel,
            Rationale = element.Rationale
        };
    }
}
