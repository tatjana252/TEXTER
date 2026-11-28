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
function shapeNode(figure, isDouble, { minSize, margin = 4, font = FONT, textMargin = new go.Margin(6, 10), events }) {
  const text = new go.TextBlock({ margin: textMargin, font, textAlign: 'center', stroke: '#222' }).bind('text')
  const outer = confidenceFill(new go.Shape(figure, { strokeWidth: 2, minSize }))
  // Only a double border nests a second shape: a diamond around a diamond would double its size.
  if (!isDouble) return new go.Node('Auto', hoverEvents(events)).add(outer, text)
  return new go.Node('Auto', hoverEvents(events)).add(
    outer,
    new go.Panel('Auto', { margin }).add(
      new go.Shape(figure, { fill: null, strokeWidth: 1.5 }).bind('stroke', 'confidenceLevel', (l) => colorsFor(l).stroke),
      text
    )
  )
}

/** Relationship diamonds: small, so that the entities stay the main shapes of the diagram. */
const DIAMOND_FONT = '600 11px "Segoe UI", system-ui, sans-serif'
const DIAMOND_TEXT_MARGIN = new go.Margin(1, 2)

/** Aggregation: relationship (diamond) inside an entity rectangle - mixed entity-relationship type. */
function aggregationNode(events) {
  return new go.Node('Auto', hoverEvents(events)).add(
    confidenceFill(new go.Shape('Rectangle', { strokeWidth: 2 })),
    new go.Panel('Auto', { margin: 3 }).add(
      new go.Shape('Diamond', { fill: 'white', strokeWidth: 1.5, minSize: new go.Size(70, 36) })
        .bind('stroke', 'confidenceLevel', (l) => colorsFor(l).stroke),
      new go.TextBlock({ margin: DIAMOND_TEXT_MARGIN, font: DIAMOND_FONT, stroke: '#222' }).bind('text')
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

/**
 * Specialization (PMER): a small diamond with S, its cardinality (min, max) on the left.
 * The node is located by the diamond, and links connect to it.
 */
function generalizationNode(events) {
  return new go.Node('Horizontal', { ...hoverEvents(events), locationObjectName: 'DIAMOND', locationSpot: go.Spot.Center }).add(
    new go.TextBlock({ margin: new go.Margin(0, 6, 0, 0), font: SMALL, stroke: '#333' }).bind('text', 'type'),
    new go.Panel('Auto', { name: 'DIAMOND', portId: '' }).add(
      confidenceFill(new go.Shape('Diamond', { strokeWidth: 2, width: 34, height: 34 })),
      new go.TextBlock('S', { font: 'bold 12px "Segoe UI", system-ui, sans-serif', stroke: '#222' })
    )
  )
}

export function nodeTemplates(events) {
  const entity = (weak) => shapeNode('Rectangle', weak, { minSize: new go.Size(110, 44), events })
  const relationship = (weak) =>
    shapeNode('Diamond', weak, { minSize: new go.Size(70, 36), margin: 4, font: DIAMOND_FONT, textMargin: DIAMOND_TEXT_MARGIN, events })

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

/** Identifying arrow owner -> weak entity: the relationship's attribution, plus the owner's mapping. */
function identifyingDetails(link) {
  const model = link.diagram.model
  const element = link.data.element
  const owner = model.findNodeDataForKey(link.data.from)?.text
  const weak = model.findNodeDataForKey(link.data.to)?.text
  return {
    ...element,
    key: `${element.key}:arrow`,
    category: 'identifying',
    relationshipName: element.text,
    text: `${owner} ⇒ ${weak}`,
    bounds: link.data.text,
    role: link.data.role,
    isAssumed: link.data.isAssumed,
    confidenceLevel: link.data.isAssumed ? 'Low' : element.confidenceLevel
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
    // Both shapes are the link path (isPanelMain); without it the second one is drawn as a black label.
    new go.Shape({ isPanelMain: true, stroke: LINE, strokeWidth: 1.5 }),
    // Wide transparent stroke: easier to hit than the 1.5px line.
    new go.Shape({ isPanelMain: true, stroke: 'transparent', strokeWidth: 10 }),
    new go.Panel('Vertical', { segmentIndex: 0, segmentFraction: 0.35, segmentOffset: new go.Point(0, -14), background: 'transparent' }).add(
      new go.TextBlock({ font: 'bold 13px "Segoe UI", system-ui, sans-serif' })
        .bind('text', '', (d) => (d.isAssumed ? `${d.text}?` : d.text))
        .bind('stroke', 'isAssumed', (a) => (a ? ASSUMED : '#333')),
      new go.TextBlock({ font: 'italic 11px "Segoe UI", system-ui, sans-serif', stroke: '#6a737d' })
        .bind('text', 'role')
        .bind('visible', 'role', (n) => !!n)
    )
  )

  // Identifying relationship (PMER): an arrow from the owner to the weak entity, the owner's bounds at its start.
  const identifying = new go.Link({
    selectable: false,
    layerName: 'Background',
    routing: go.Routing.Orthogonal,
    corner: 0,
    fromSpot: go.Spot.Bottom,
    toSpot: go.Spot.Top,
    cursor: 'pointer',
    mouseEnter: (e, link) => events.onHover(identifyingDetails(link)),
    mouseLeave: () => events.onHover(null)
  }).add(
    new go.Shape({ isPanelMain: true, stroke: LINE, strokeWidth: 1.5 }),
    new go.Shape({ isPanelMain: true, stroke: 'transparent', strokeWidth: 10 }),
    new go.Shape({ toArrow: 'Triangle', scale: 1.1, stroke: LINE, fill: LINE }),
    // The owner's bounds beside the arrow, as in PMER.
    new go.Panel('Vertical', { segmentIndex: NaN, segmentFraction: 0.5, alignmentFocus: go.Spot.Left, segmentOffset: new go.Point(-6, -10), segmentOrientation: go.Orientation.None, background: 'transparent' }).add(
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

  // Supertype -> S -> subtypes: orthogonal, top-down; the lines to the subtypes end in arrows.
  const topDown = (arrow) =>
    new go.Link({
      selectable: false,
      layerName: 'Background',
      routing: go.Routing.Orthogonal,
      corner: 0,
      fromSpot: go.Spot.Bottom,
      toSpot: go.Spot.Top
    }).add(
      new go.Shape({ stroke: LINE, strokeWidth: 1.5 }),
      ...(arrow ? [new go.Shape({ toArrow: 'Triangle', scale: 1.1, stroke: LINE, fill: LINE })] : [])
    )

  return new go.Map([
    ['mapping', mapping],
    ['identifying', identifying],
    ['attribute', plain(1.2)],
    ['generalization', topDown(false)],
    ['specialization', topDown(true)]
  ])
}

const SPECIALIZATION_GAP = 55
const SUBTYPE_GAP = 40

/** Moves a node and the attributes it owns. */
function moveWithAttributes(node, x, y) {
  const dx = x - node.actualBounds.x
  const dy = y - node.actualBounds.y
  node.moveTo(node.position.x + dx, node.position.y + dy)
  node.findLinksOutOf().each((l) => {
    if (l.category === 'attribute' && l.toNode) l.toNode.moveTo(l.toNode.position.x + dx, l.toNode.position.y + dy)
  })
}

const WEAK_GAP = 80

/** Each weak entity under its owner, so the identifying arrow points down. */
function arrangeWeakEntities(diagram) {
  diagram.links.each((l) => {
    if (l.category !== 'identifying' || !l.fromNode || !l.toNode) return
    const owner = l.fromNode.actualBounds
    const weak = l.toNode.actualBounds
    moveWithAttributes(l.toNode, owner.centerX - weak.width / 2, owner.bottom + WEAK_GAP)
  })
}

/**
 * Draws each specialization top-down, as in PMER: the S diamond under its supertype,
 * the subtypes side by side under the diamond. A moved subtype takes its attributes with it.
 */
function arrangeSpecializations(diagram) {
  diagram.nodes.each((s) => {
    if (s.category !== 'generalization') return
    const into = s.findLinksInto().first()
    const parent = into?.fromNode
    if (!parent) return
    const subs = []
    s.findLinksOutOf().each((l) => l.toNode && subs.push(l.toNode))

    const top = parent.actualBounds
    const cx = top.centerX
    const diamondY = top.bottom + SPECIALIZATION_GAP
    s.moveTo(s.position.x + (cx - s.location.x), s.position.y + (diamondY - s.location.y))

    const width = subs.reduce((w, n) => w + n.actualBounds.width, 0) + SUBTYPE_GAP * (subs.length - 1)
    let x = cx - width / 2
    const y = diamondY + 17 + SPECIALIZATION_GAP
    for (const sub of subs) {
      moveWithAttributes(sub, x, y)
      x += sub.actualBounds.width + SUBTYPE_GAP
    }
  })
}

/** Force-directed layout with short springs for attributes, so they cluster around their owner. */
export class DerLayout extends go.ForceDirectedLayout {
  constructor() {
    super({ maxIterations: 800, defaultElectricalCharge: 120 })
  }

  springLength(edge) {
    return edge.link?.category === 'attribute' ? 20 : 70
  }

  commitLayout() {
    super.commitLayout()
    arrangeWeakEntities(this.diagram)
    arrangeSpecializations(this.diagram)
  }
}
