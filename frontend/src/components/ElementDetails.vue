<script setup>
import { computed } from 'vue'
import { colorsFor } from '../diagram/confidence'

const props = defineProps({
  element: { type: Object, default: null }
})

const KIND = {
  entity: 'Entity',
  weakEntity: 'Weak entity',
  relationship: 'Relationship',
  weakRelationship: 'Weak entity relationship',
  aggregation: 'Aggregation',
  attribute: 'Attribute',
  generalization: 'Generalization'
}

const kind = computed(() => {
  const e = props.element
  if (!e) return ''
  if (e.category !== 'attribute') return KIND[e.category]
  return `${e.isIdentifier ? 'Identifier' : 'Attribute'} · ${e.type}`
})

const colors = computed(() => colorsFor(props.element?.confidenceLevel))
</script>

<template>
  <div class="details">
    <template v-if="element">
      <div class="header">
        <span class="name">{{ element.text }}</span>
        <span class="kind">{{ kind }}</span>
        <span class="badge" :style="{ background: colors.fill, color: colors.text, borderColor: colors.stroke }">
          {{ colors.label }} · {{ Math.round(element.confidence * 100) }}%
        </span>
        <span v-for="id in element.sentenceIds" :key="id" class="sentence">{{ id }}</span>
      </div>
      <p class="rationale">{{ element.rationale }}</p>
      <p v-if="!element.spans.length" class="warning">Not attributed to any part of the requirements.</p>
    </template>
    <p v-else class="hint">Hover over a diagram element to see the text it is attributed to.</p>
  </div>
</template>

<style scoped>
.details {
  min-height: 64px;
  font-size: 14px;
}
.header {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.name {
  font-weight: 600;
  font-size: 15px;
}
.kind {
  color: var(--muted);
}
.badge {
  border: 1px solid;
  border-radius: 999px;
  padding: 1px 10px;
  font-size: 12px;
  font-weight: 600;
}
.sentence {
  font-size: 12px;
  font-weight: 600;
  color: var(--accent);
  background: #edf0fc;
  border-radius: 4px;
  padding: 1px 6px;
}
.rationale {
  margin: 6px 0 0;
}
.warning {
  margin: 4px 0 0;
  color: #8f1f1f;
}
.hint {
  margin: 0;
  color: var(--muted);
}
</style>
