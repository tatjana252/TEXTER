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

            var weakIds = model.Entities.Where(e => e.Kind == EntityKind.Weak).Select(e => e.Id).ToHashSet();
            foreach (var relationship in model.Relationships)
            {
                // Identifying relationship: an arrow owner -> weak entity, labelled with the owner's mapping.
                var ownerMapping = new[] { relationship.Mapping1, relationship.Mapping2 }
                    .SingleOrDefault(m => weakIds.Contains(m.CoDomainEntityId) && !weakIds.Contains(m.DomainEntityId));
                if (relationship.IsWeak && !relationship.IsAggregation && ownerMapping is not null)
                {
                    result.Links.Add(new Link
                    {
                        From = ownerMapping.DomainEntityId,
                        To = ownerMapping.CoDomainEntityId,
                        Category = LinkCategory.Identifying,
                        Text = ownerMapping.ToString(),
                        Role = ownerMapping.Role,
                        IsAssumed = ownerMapping.IsAssumed,
                        Element = NodeFor(relationship, NodeCategory.WeakRelationship)
                    });
                    continue;
                }

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
                // PMER specialization: (min, max) subtypes per supertype instance - total => 1, disjoint => 1.
                var node = NodeFor(generalization, NodeCategory.Generalization);
                node.Type = $"({(generalization.IsTotal ? "1" : "0")}, {(generalization.IsDisjoint ? "1" : "M")})";
                result.Nodes.Add(node);
                result.Links.Add(new Link { From = generalization.SuperEntityId, To = generalization.Id, Category = LinkCategory.Generalization });
                foreach (var subId in generalization.SubEntityIds)
                    result.Links.Add(new Link { From = generalization.Id, To = subId, Category = LinkCategory.Specialization });
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
            FactIds = element.FactIds,
            DecisionFactIds = element switch
            {
                Entity e => e.WeakFactIds,
                Relationship r => r.AggregationFactIds,
                _ => new List<string>()
            },
            SentenceIds = element.SentenceIds,
            Spans = element.Spans,
            Confidence = element.Confidence,
            ConfidenceLevel = element.ConfidenceLevel,
            Rationale = element.Rationale
        };
    }
}
