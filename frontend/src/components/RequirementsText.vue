<script setup>
import { computed, nextTick, ref, watch } from 'vue'
import { colorsFor } from '../diagram/confidence'

const props = defineProps({
  modelValue: { type: String, required: true },
  /** false: plain textarea, true: read-only view with highlighted evidence */
  readonly: { type: Boolean, default: false },
  /** Spans { start, length } to highlight */
  spans: { type: Array, default: () => [] },
  confidenceLevel: { type: String, default: null },
  /** Sentences { id, start, length } shown as S1, S2, ... labels */
  sentences: { type: Array, default: () => [] },
  /** Sentence ids the active element is attributed to */
  activeSentenceIds: { type: Array, default: () => [] }
})
defineEmits(['update:modelValue'])

const viewer = ref(null)

/** Splits the text into segments: sentence labels, plain and highlighted text (overlapping spans are merged). */
const segments = computed(() => {
  const text = props.modelValue
  const ranges = [...props.spans]
    .map((s) => [s.start, s.start + s.length])
    .sort((a, b) => a[0] - b[0])
    .reduce((merged, range) => {
      const last = merged.at(-1)
      if (last && range[0] <= last[1]) last[1] = Math.max(last[1], range[1])
      else merged.push(range)
      return merged
    }, [])

  const labels = new Map(props.sentences.map((s) => [s.start, s.id]))
  const cuts = [...new Set([0, text.length, ...ranges.flat(), ...labels.keys()])].sort((a, b) => a - b)

  const result = []
  for (let i = 0; i < cuts.length - 1; i++) {
    const [start, end] = [cuts[i], cuts[i + 1]]
    if (labels.has(start)) result.push({ label: labels.get(start) })
    const mark = ranges.some(([s, e]) => start >= s && end <= e)
    result.push({ text: text.slice(start, end), mark })
  }
  return result
})

const markStyle = computed(() => {
  const colors = colorsFor(props.confidenceLevel)
  return { background: colors.fill, boxShadow: `inset 0 -2px 0 ${colors.stroke}` }
})

// Bring the first highlighted fragment into view.
watch(
  () => props.spans,
  async () => {
    await nextTick()
    viewer.value?.querySelector('mark')?.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
  }
)
</script>

<template>
  <textarea
    v-if="!readonly"
    class="requirements"
    :value="modelValue"
    placeholder="Enter the requirements (verbal description of the domain)..."
    @input="$emit('update:modelValue', $event.target.value)"
  />
  <div v-else ref="viewer" class="requirements viewer">
    <template v-for="(segment, i) in segments" :key="i">
      <sup v-if="segment.label" :class="{ active: activeSentenceIds.includes(segment.label) }">{{ segment.label }}</sup>
      <mark v-else-if="segment.mark" :style="markStyle">{{ segment.text }}</mark>
      <template v-else>{{ segment.text }}</template>
    </template>
  </div>
</template>

<style scoped>
.requirements {
  width: 100%;
  height: 100%;
  padding: 16px;
  border: 1px solid var(--border);
  border-radius: 8px;
  font: 15px/1.7 'Segoe UI', system-ui, sans-serif;
  color: var(--text);
  background: white;
  resize: none;
  overflow: auto;
}
.viewer {
  white-space: pre-wrap;
}
sup {
  font-size: 10px;
  font-weight: 600;
  color: #9aa1aa;
  margin-right: 3px;
  user-select: none;
}
sup.active {
  color: white;
  background: var(--accent);
  border-radius: 3px;
  padding: 0 3px;
}
mark {
  color: inherit;
  border-radius: 3px;
  padding: 1px 0;
}
</style>
