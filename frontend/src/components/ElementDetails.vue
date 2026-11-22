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
  identifyingRelationship: 'Identifying relationship',
  attribute: 'Attribute',
  generalization: 'Specialization'
}

const kind = computed(() => {
  const e = props.element
  if (!e) return ''
  const flags = [e.isKey && 'key', e.isMultivalued && 'multivalued', e.isDerived && 'derived'].filter(Boolean)
  return KIND[e.category] + (flags.length ? ` (${flags.join(', ')})` : '')
})

const colors = computed(() => colorsFor(props.element?.confidenceLevel))
</script>

<template>
  <div class="details">
    <template v-if="element">
      <div class="header">
        <span class="name">{{ element.category === 'generalization' ? (element.isDisjoint ? 'Disjoint' : 'Overlapping') : element.text }}</span>
        <span class="kind">{{ kind }}</span>
        <span class="badge" :style="{ background: colors.fill, color: colors.text, borderColor: colors.stroke }">
          {{ colors.label }} · {{ Math.round(element.confidence * 100) }}%
        </span>
      </div>
      <p class="rationale">{{ element.rationale }}</p>
      <p v-if="!element.spans.length" class="warning">No supporting text found in the requirements.</p>
    </template>
    <p v-else class="hint">Hover over a diagram element to see the text it comes from.</p>
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
  gap: 10px;
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
