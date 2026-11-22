<script setup>
import { computed } from 'vue'
import { CONFIDENCE, CONFIDENCE_ORDER } from '../diagram/confidence'

const props = defineProps({
  nodes: { type: Array, default: () => [] }
})

const items = computed(() =>
  CONFIDENCE_ORDER.map((level) => ({
    level,
    ...CONFIDENCE[level],
    count: props.nodes.filter((n) => n.confidenceLevel === level).length
  }))
)
</script>

<template>
  <div class="legend">
    <span v-for="item in items" :key="item.level" class="item">
      <span class="swatch" :style="{ background: item.fill, borderColor: item.stroke }" />
      {{ item.label }} <b>{{ item.count }}</b>
    </span>
  </div>
</template>

<style scoped>
.legend {
  display: flex;
  gap: 14px;
  font-size: 13px;
  color: var(--muted);
}
.item {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}
.swatch {
  width: 14px;
  height: 14px;
  border: 2px solid;
  border-radius: 3px;
}
b {
  color: var(--text);
}
</style>
