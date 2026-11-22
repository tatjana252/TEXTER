import go from 'gojs'
import { colorsFor } from './confidence'

// PMER DER templates. Category names must match Texter.Model/GoJs/NodeCategory.cs and LinkCategory.cs.

const FONT = '600 13px "Segoe UI", system-ui, sans-serif'
const SMALL = '12px "Segoe UI", system-ui, sans-serif'
const LINE = '#5c6370'
const ASSUMED = '#d63c3c'

const hoverEvents = (events) => ({
  cursor: 'pointer',
  mouseEnter: (e, obj) => events.onHover(obj.data),
  mouseLeave: () => events.onHover(null)
})

const confidenceFill = (shape) =>
  shape
    .bind('fill', 'confidenceLevel', (l) => colorsFor(l).fill)
    .bind('stroke', 'confidenceLevel', (l) => colorsFor(l).stroke)

/**
 * Shape with an optional inner (double) border, colored by confidence.
 * @param isDouble draws the inner border (weak entity, weak relationship)
 */
function shapeNode(figure, isDouble, { minSize, margin = 4, events }) {
  return new go.Node('Auto', hoverEvents(events)).add(
    confidenceFill(new go.Shape(figure, { strokeWidth: 2, minSize })),
    new go.Panel('Auto', { margin }).add(
      new go.Shape(figure, { fill: null, strokeWidth: 1.5 })
        .bind('stroke', '', (d) => (isDouble ? colorsFor(d.confidenceLevel).stroke : null)),
      new go.TextBlock({ margin: new go.Margin(6, 10), font: FONT, textAlign: 'center', stroke: '#222' }).bind('text')
    )
  )
}

/** Aggregation: relationship (diamond) inside an entity rectangle - mixed entity-relationship type. */
function aggregationNode(events) {
  return new go.Node('Auto', hoverEvents(events)).add(
    confidenceFill(new go.Shape('Rectangle', { strokeWidth: 2 })),
    new go.Panel('Auto', { margin: 4 }).add(
      new go.Shape('Diamond', { fill: 'white', strokeWidth: 1.5, minSize: new go.Size(110, 56) })
        .bind('stroke', 'confidenceLevel', (l) => colorsFor(l).stroke),
      new go.TextBlock({ margin: new go.Margin(6, 10), font: FONT, stroke: '#222' }).bind('text')
    )
  )
}

/** Attribute oval; identifier attributes are marked with # (and underlined). */
function attributeNode(events) {
  return new go.Node('Auto', hoverEvents(events)).add(
    confidenceFill(new go.Shape('Ellipse', { strokeWidth: 1.5, minSize: new go.Size(64, 30) })),
    new go.TextBlock({ margin: new go.Margin(4, 10), font: SMALL, stroke: '#222' })
      .bind('text', '', (d) => (d.isIdentifier ? `#${d.text}` : d.text))
      .bind('isUnderline', 'isIdentifier')
  )
}

/** IS-A (generalization / specialization). */
function generalizationNode(events) {
  return new go.Node('Auto', hoverEvents(events)).add(
    confidenceFill(new go.Shape('Triangle', { strokeWidth: 2, minSize: new go.Size(56, 40) })),
    new go.TextBlock('IS-A', { margin: new go.Margin(14, 4, 2, 4), font: 'bold 11px "Segoe UI", system-ui, sans-serif' })
  )
}

export function nodeTemplates(events) {
  const entity = (weak) => shapeNode('Rectangle', weak, { minSize: new go.Size(110, 44), events })
  const relationship = (weak) => shapeNode('Diamond', weak, { minSize: new go.Size(120, 64), margin: 7, events })

  return new go.Map([
    ['entity', entity(false)],
    ['weakEntity', entity(true)],
    ['relationship', relationship(false)],
    ['weakRelationship', relationship(true)],
    ['aggregation', aggregationNode(events)],
    ['attribute', attributeNode(events)],
    ['generalization', generalizationNode(events)]
  ])
}

/**
 * A mapping link has no attribution of its own: it is shown as its relationship's attribution,
 * plus the direction (domain => co-domain), bounds and role of this mapping.
 */
function mappingDetails(link) {
  const model = link.diagram.model
  const relationship = model.findNodeDataForKey(link.data.from)
  const inverse = model.linkDataArray.find((l) => l.from === link.data.from && l.category === 'mapping' && l !== link.data)
  const domain = model.findNodeDataForKey(link.data.to)?.text
  const coDomain = model.findNodeDataForKey(inverse?.to)?.text
  return {
    ...relationship,
    key: `${relationship.key}:${link.data.to}`,
    category: 'mapping',
    relationshipCategory: relationship.category,
    relationshipName: relationship.text,
    text: `${domain} ⇒ ${coDomain}`,
    bounds: link.data.text,
    role: link.data.role,
    isAssumed: link.data.isAssumed,
    // Assumed bounds are drawn red on the diagram, so the details use the same color.
    confidenceLevel: link.data.isAssumed ? 'Low' : relationship.confidenceLevel
  }
}

export function linkTemplates(events) {
  // Relationship -> entity; the label is the mapping entity => other entity, written next to the diamond.
  const mapping = new go.Link({
    selectable: false,
    layerName: 'Background',
    cursor: 'pointer',
    mouseEnter: (e, link) => events.onHover(mappingDetails(link)),
    mouseLeave: () => events.onHover(null)
  }).add(
    new go.Shape({ stroke: LINE, strokeWidth: 1.5 }),
    // Wide transparent stroke: easier to hit than the 1.5px line.
    new go.Shape({ stroke: 'transparent', strokeWidth: 10 }),
    new go.Panel('Vertical', { segmentIndex: 0, segmentFraction: 0.35, segmentOffset: new go.Point(0, -14), background: 'transparent' }).add(
      new go.TextBlock({ font: 'bold 13px "Segoe UI", system-ui, sans-serif' })
        .bind('text', '', (d) => (d.isAssumed ? `${d.text}?` : d.text))
        .bind('stroke', 'isAssumed', (a) => (a ? ASSUMED : '#333')),
      new go.TextBlock({ font: 'italic 11px "Segoe UI", system-ui, sans-serif', stroke: '#6a737d' })
        .bind('text', 'role')
        .bind('visible', 'role', (n) => !!n)
    )
  )

  const plain = (width) =>
    new go.Link({ selectable: false, layerName: 'Background' }).add(new go.Shape({ stroke: LINE, strokeWidth: width }))

  return new go.Map([
    ['mapping', mapping],
    ['attribute', plain(1.2)],
    ['generalization', plain(1.5)]
  ])
}

/** Force-directed layout with short springs for attributes, so they cluster around their owner. */
export class DerLayout extends go.ForceDirectedLayout {
  constructor() {
    super({ maxIterations: 800, defaultElectricalCharge: 120 })
  }

  springLength(edge) {
    return edge.link?.category === 'attribute' ? 20 : 70
  }
}
