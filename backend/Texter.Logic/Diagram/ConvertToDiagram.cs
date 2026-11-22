using Texter.Model.Conceptual;
using Texter.Model.GoJs;
using Attribute = Texter.Model.Conceptual.Attribute;

namespace Texter.Logic.Diagram
{
    /// <summary>Conceptual model -> GoJS model in Chen notation.</summary>
    public static class ConvertToDiagram
    {
        public static DiagramModel Convert(ConceptualModel model)
        {
            var result = new DiagramModel();

            foreach (var entity in model.Entities)
            {
                result.Nodes.Add(NodeFor(entity, entity.IsWeak ? NodeCategory.WeakEntity : NodeCategory.Entity));
                AddAttributes(result, entity.Id, entity.Attributes);
            }

            foreach (var relationship in model.Relationships)
            {
                result.Nodes.Add(NodeFor(relationship,
                    relationship.IsIdentifying ? NodeCategory.IdentifyingRelationship : NodeCategory.Relationship));

                foreach (var participant in relationship.Participants)
                {
                    result.Links.Add(new Link
                    {
                        From = relationship.Id,
                        To = participant.EntityId,
                        Category = LinkCategory.Participation,
                        Text = string.IsNullOrWhiteSpace(participant.Role)
                            ? participant.Cardinality
                            : $"{participant.Cardinality} ({participant.Role})",
                        IsTotal = participant.IsTotal
                    });
                }
                AddAttributes(result, relationship.Id, relationship.Attributes);
            }

            foreach (var generalization in model.Generalizations)
            {
                var node = NodeFor(generalization, NodeCategory.Generalization);
                node.IsDisjoint = generalization.IsDisjoint;
                result.Nodes.Add(node);

                result.Links.Add(new Link
                {
                    From = generalization.SuperEntityId,
                    To = generalization.Id,
                    Category = LinkCategory.Generalization,
                    IsTotal = generalization.IsTotal
                });
                foreach (var subId in generalization.SubEntityIds)
                {
                    result.Links.Add(new Link { From = generalization.Id, To = subId, Category = LinkCategory.Generalization });
                }
            }

            return result;
        }

        private static void AddAttributes(DiagramModel diagram, string ownerId, IEnumerable<Attribute> attributes)
        {
            foreach (var attribute in attributes)
            {
                var node = NodeFor(attribute, NodeCategory.Attribute);
                node.IsKey = attribute.IsKey;
                node.IsMultivalued = attribute.IsMultivalued;
                node.IsDerived = attribute.IsDerived;
                diagram.Nodes.Add(node);
                diagram.Links.Add(new Link { From = ownerId, To = attribute.Id, Category = LinkCategory.Attribute });
            }
        }

        private static Node NodeFor(ModelElement element, string category) => new()
        {
            Key = element.Id,
            Category = category,
            Text = element.Name,
            Confidence = element.Confidence,
            ConfidenceLevel = element.ConfidenceLevel,
            Spans = element.Spans,
            Rationale = element.Rationale
        };
    }
}
