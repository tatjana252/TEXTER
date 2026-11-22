<script setup>
import { computed, ref } from 'vue'

const props = defineProps({
  modelValue: { type: String, required: true },
  /** [{ line, column, message }] */
  errors: { type: Array, default: () => [] }
})
const emit = defineEmits(['update:modelValue', 'line'])

const LINE_HEIGHT = 21
const textarea = ref(null)
const gutter = ref(null)
const lineCount = computed(() => props.modelValue.split('\n').length)
const errorLines = computed(() => new Set(props.errors.map((e) => e.line)))

function syncScroll() {
  gutter.value.scrollTop = textarea.value.scrollTop
}

function lineAt(offset) {
  return props.modelValue.slice(0, offset).split('\n').length
}

function onCaret() {
  emit('line', lineAt(textarea.value.selectionStart))
}

function onHover(event) {
  const style = getComputedStyle(textarea.value)
  const y = event.offsetY - parseFloat(style.paddingTop) + textarea.value.scrollTop
  emit('line', Math.min(lineCount.value, Math.max(1, Math.floor(y / LINE_HEIGHT) + 1)))
}

/** Moves the caret to the start of a line (e.g. clicked error). */
function goToLine(line) {
  const lines = props.modelValue.split('\n')
  const offset = lines.slice(0, line - 1).reduce((sum, l) => sum + l.length + 1, 0)
  textarea.value.focus()
  textarea.value.setSelectionRange(offset, offset + (lines[line - 1]?.length ?? 0))
  textarea.value.scrollTop = Math.max(0, (line - 5) * LINE_HEIGHT)
  emit('line', line)
}

defineExpose({ goToLine })
</script>

<template>
  <div class="editor">
    <div ref="gutter" class="gutter">
      <div v-for="n in lineCount" :key="n" :class="{ error: errorLines.has(n) }">{{ n }}</div>
    </div>
    <textarea
      ref="textarea"
      :value="modelValue"
      spellcheck="false"
      wrap="off"
      @input="$emit('update:modelValue', $event.target.value)"
      @scroll="syncScroll"
      @click="onCaret"
      @keyup="onCaret"
      @mousemove="onHover"
      @mouseleave="onCaret"
    />
  </div>
</template>

<style scoped>
.editor {
  display: flex;
  height: 100%;
  border: 1px solid var(--border);
  border-radius: 8px;
  overflow: hidden;
  background: white;
  font: 13px/21px ui-monospace, 'Cascadia Code', Consolas, monospace;
}
.gutter {
  padding: 12px 8px;
  text-align: right;
  color: #9aa1aa;
  background: #f6f7f9;
  border-right: 1px solid var(--border);
  overflow: hidden;
  user-select: none;
  min-width: 44px;
}
.gutter .error {
  color: white;
  background: #d63c3c;
  border-radius: 3px;
}
textarea {
  flex: 1;
  padding: 12px;
  border: none;
  outline: none;
  resize: none;
  font: inherit;
  color: var(--text);
  white-space: pre;
  tab-size: 4;
}
</style>
