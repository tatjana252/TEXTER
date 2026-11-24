<script setup>
import { computed } from 'vue'
import { CATEGORY, colorsFor } from '../diagram/confidence'

const props = defineProps({
  element: { type: Object, default: null },
  /** Facts { id, statement, sentenceIds } of the model */
  facts: { type: Array, default: () => [] }
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
  if (e.category === 'mapping') return `Mapping ${e.bounds}${e.role ? ` · ${e.role}` : ''} · of ${e.relationshipName}`
  if (e.category === 'identifying') return `Identifying relationship ${e.relationshipName} · ${e.bounds}${e.role ? ` · ${e.role}` : ''}`
  if (e.category !== 'attribute') return KIND[e.category]
  return `${e.isIdentifier ? 'Identifier' : 'Attribute'} · ${e.type}`
})

const colors = computed(() => colorsFor(props.element?.confidenceLevel))

const DECISION = { weakEntity: 'Weak entity', aggregation: 'Aggregation' }

/** Cited facts, then the facts of the weak entity / aggregation decision. */
const citedFacts = computed(() => {
  const e = props.element
  if (!e) return []
  const byId = new Map(props.facts.map((f) => [f.id, f]))
  const decision = DECISION[e.relationshipCategory ?? e.category]
  const decisionIds = new Set(e.decisionFactIds ?? [])
  const ids = [...new Set([...e.factIds, ...decisionIds])].filter((id) => byId.has(id))
  return ids.map((id) => ({ ...byId.get(id), decision: decisionIds.has(id) ? decision : null }))
})

const decisionWithoutFacts = computed(() => {
  const e = props.element
  const decision = e && DECISION[e.relationshipCategory ?? e.category]
  return decision && !(e.decisionFactIds ?? []).length ? decision : null
})
</script>

<template>
  <div class="details">
    <template v-if="element">
      <div class="header">
        <span class="name">{{ element.text }}</span>
        <span class="kind">{{ kind }}</span>
        <span class="badge" :style="{ background: colors.fill, color: colors.text, borderColor: colors.stroke }">
          {{ colors.label }} · {{ CATEGORY[element.confidence] ?? element.confidence }}
        </span>
        <span v-for="id in element.sentenceIds" :key="id" class="sentence">{{ id }}</span>
      </div>
      <p v-if="element.category === 'mapping' || element.category === 'identifying'" :class="element.isAssumed ? 'warning' : 'rationale'">
        {{ element.isAssumed
          ? 'The bounds of this mapping are not stated in the text; they were assumed (shown with ?).'
          : 'The bounds of this mapping are stated in the text. The facts below are those of the relationship.' }}
      </p>
      <p v-if="element.rationale" class="rationale">{{ element.rationale }}</p>
      <ul v-if="citedFacts.length" class="facts">
        <li v-for="fact in citedFacts" :key="(fact.decision ?? '') + fact.id">
          <b>{{ fact.id }}</b>
          <span v-if="fact.decision" class="decision">{{ fact.decision }}</span>
          {{ fact.statement }}
          <span class="muted">({{ fact.sentenceIds.join(', ') }})</span>
        </li>
      </ul>
      <p v-if="!element.factIds.length" class="warning">No fact from the requirements supports it (assumed).</p>
      <p v-else-if="!element.spans.length" class="warning">Not attributed to any part of the requirements.</p>
      <p v-if="decisionWithoutFacts" class="warning">No fact supports the {{ decisionWithoutFacts.toLowerCase() }} decision.</p>
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
.facts {
  margin: 6px 0 0;
  padding-left: 18px;
  max-height: 110px;
  overflow: auto;
}
.facts li {
  margin: 2px 0;
}
.decision {
  font-size: 11px;
  font-weight: 600;
  color: #7a5c00;
  background: #fff6d5;
  border-radius: 4px;
  padding: 0 5px;
  margin-right: 2px;
}
.muted {
  color: var(--muted);
  font-size: 12px;
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
