import go from 'gojs'
import { colorsFor } from './confidence'

// Chen-notation templates. Category names must match Texter.Model/GoJs/NodeCategory.cs and LinkCategory.cs.

const FONT = '600 13px "Segoe UI", system-ui, sans-serif'
const LINE = '#5c6370'

/**
 * Node with an optional inner (double) border, colored by confidence.
 * @param figure GoJS figure name
 * @param isDouble (data) => bool, draws the inner border (weak entity, identifying relationship, multivalued attribute)
 */
function shapeNode(figure, isDouble, { minSize, margin = 4, font = FONT, events }) {
  return new go.Node('Auto', {
    cursor: 'pointer',
    mouseEnter: (e, node) => events.onHover(node.data),
    mouseLeave: () => events.onHover(null)
  }).add(
    new go.Shape(figure, { strokeWidth: 2, minSize })
      .bind('fill', 'confidenceLevel', (l) => colorsFor(l).fill)
      .bind('stroke', 'confidenceLevel', (l) => colorsFor(l).stroke)
      .bind('strokeDashArray', 'isDerived', (d) => (d ? [5, 3] : null)),
    new go.Panel('Auto', { margin }).add(
      new go.Shape(figure, { fill: null, strokeWidth: 1.5 })
        .bind('stroke', '', (d) => (isDouble(d) ? colorsFor(d.confidenceLevel).stroke : null)),
      new go.TextBlock({ margin: new go.Margin(6, 10), font, textAlign: 'center', stroke: '#222' })
        .bind('text')
        .bind('isUnderline', 'isKey')
    )
  )
}

export function nodeTemplates(events) {
  const entity = () =>
    shapeNode('Rectangle', (d) => d.category === 'weakEntity', { minSize: new go.Size(110, 44), events })
  const relationship = () =>
    shapeNode('Diamond', (d) => d.category === 'identifyingRelationship', {
      minSize: new go.Size(120, 64), margin: 7, events
    })
  const attribute = shapeNode('Ellipse', (d) => d.isMultivalued, {
    minSize: new go.Size(70, 34), margin: 3, font: '12px "Segoe UI", system-ui, sans-serif', events
  })
  const generalization = shapeNode('Circle', () => false, { minSize: new go.Size(30, 30), margin: 0, events })

  return new go.Map([
    ['entity', entity()],
    ['weakEntity', entity()],
    ['relationship', relationship()],
    ['identifyingRelationship', relationship()],
    ['attribute', attribute],
    ['generalization', generalization]
  ])
}

/** Line that becomes a double line when isTotal (total participation / total specialization). */
function totalAwareLink() {
  return new go.Link({ selectable: false, layerName: 'Background' }).add(
    new go.Shape({ isPanelMain: true, stroke: LINE }).bind('strokeWidth', 'isTotal', (t) => (t ? 5 : 1.5)),
    new go.Shape({ isPanelMain: true, stroke: 'white', strokeWidth: 2 }).bind('visible', 'isTotal')
  )
}

export function linkTemplates() {
  const participation = totalAwareLink().add(
    new go.TextBlock({
      segmentIndex: -1,
      segmentOffset: new go.Point(-22, -12),
      font: 'bold 13px "Segoe UI", system-ui, sans-serif',
      stroke: '#333'
    }).bind('text')
  )

  const attribute = new go.Link({ selectable: false, layerName: 'Background' }).add(
    new go.Shape({ stroke: LINE, strokeWidth: 1.2 })
  )

  return new go.Map([
    ['participation', participation],
    ['generalization', totalAwareLink()],
    ['attribute', attribute]
  ])
}

/** Force-directed layout with short springs for attributes, so they cluster around their owner. */
export class ChenLayout extends go.ForceDirectedLayout {
  constructor() {
    super({ maxIterations: 800, defaultElectricalCharge: 120 })
  }

  springLength(edge) {
    return edge.link?.category === 'attribute' ? 20 : 60
  }
}
