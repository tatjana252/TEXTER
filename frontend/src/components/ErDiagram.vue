<script setup>
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import go from 'gojs'
import { DerLayout, linkTemplates, nodeTemplates } from '../diagram/templates'

const props = defineProps({
  /** GoJS model data { nodes, links } from the backend */
  model: { type: Object, default: null }
})
const emit = defineEmits(['hover', 'select'])

const container = ref(null)
let diagram = null

onMounted(() => {
  diagram = new go.Diagram(container.value, {
    layout: new DerLayout(),
    initialAutoScale: go.AutoScale.Uniform,
    padding: 30,
    'undoManager.isEnabled': true,
    'animationManager.isEnabled': false,
    nodeTemplateMap: nodeTemplates({ onHover: (data) => emit('hover', data) }),
    linkTemplateMap: linkTemplates()
  })

  diagram.addDiagramListener('ChangedSelection', () => {
    emit('select', diagram.selection.first()?.data ?? null)
  })

  load(props.model)
})

onBeforeUnmount(() => {
  if (diagram) diagram.div = null
})

watch(() => props.model, load)

function load(model) {
  if (!diagram) return
  // GoJS mutates its data (e.g. __gohashid), so it gets its own copy, not Vue's reactive objects.
  const copy = JSON.parse(JSON.stringify(model ?? { nodes: [], links: [] }))
  diagram.model = new go.GraphLinksModel(copy.nodes, copy.links)
}

/** Re-runs the layout (e.g. after the user moved nodes around). */
function relayout() {
  diagram?.layoutDiagram(true)
  diagram?.zoomToFit()
}

defineExpose({ relayout })
</script>

<template>
  <div ref="container" class="er-diagram" />
</template>

<style scoped>
.er-diagram {
  width: 100%;
  height: 100%;
  background: #fbfbfc;
}
</style>
