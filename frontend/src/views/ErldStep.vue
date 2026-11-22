<script setup>
import { computed, onMounted, ref } from 'vue'
import RequirementsText from '../components/RequirementsText.vue'
import ErldEditor from '../components/ErldEditor.vue'
import { levelFor } from '../diagram/confidence'
import { texterApi } from '../services/texterApi'

const props = defineProps({
  text: { type: String, required: true },
  /** Result of step 1: { model, sentences, ... } */
  conceptual: { type: Object, required: true }
})
/** { code, errors } */
const erld = defineModel('erld', { type: Object, default: null })

const modelName = ref('Texter')
const includeAttribution = ref(true)
const loading = ref(false)
const error = ref('')
const currentLine = ref(0)
const editor = ref(null)
const validated = ref(false)

onMounted(() => {
  if (!erld.value) generate()
})

async function generate() {
  loading.value = true
  error.value = ''
  try {
    erld.value = await texterApi.generateErld(props.conceptual.model, modelName.value, includeAttribution.value)
    validated.value = true
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

async function validate() {
  try {
    erld.value = await texterApi.validateErld(erld.value.code)
    validated.value = true
  } catch (e) {
    error.value = e.message
  }
}

function onEdit(code) {
  erld.value = { ...erld.value, code }
  validated.value = false
}

/**
 * Attribution of the current line: its own annotations or, for a declaration,
 * the annotation line right above it.
 */
const attribution = computed(() => {
  const lines = erld.value?.code.split('\n') ?? []
  const parse = (line) => ({
    sentenceIds: line?.match(/@source\(([^)]*)\)/)?.[1].split(',').map((s) => s.trim()) ?? [],
    confidence: Number(line?.match(/@confidence\(([^)]*)\)/)?.[1] ?? NaN)
  })
  let found = parse(lines[currentLine.value - 1])
  if (!found.sentenceIds.length && Number.isNaN(found.confidence)) found = parse(lines[currentLine.value - 2])
  return found
})

const spans = computed(() =>
  props.conceptual.sentences
    .filter((s) => attribution.value.sentenceIds.includes(s.id))
    .map((s) => ({ start: s.start, length: s.length }))
)
const level = computed(() => (Number.isNaN(attribution.value.confidence) ? 'High' : levelFor(attribution.value.confidence)))
</script>

<template>
  <main class="two-panels">
    <section class="panel">
      <div class="toolbar">
        <h2>Requirements</h2>
      </div>
      <RequirementsText
        :model-value="text"
        readonly
        :spans="spans"
        :confidence-level="level"
        :sentences="conceptual.sentences"
        :active-sentence-ids="attribution.sentenceIds"
      />
      <p class="hint">Hover over or move the cursor to an ERLD line with <code>@source</code> to see the text it comes from.</p>
    </section>

    <section class="panel">
      <div class="toolbar">
        <h2>ERLD</h2>
        <input v-model="modelName" class="model-name" title="Model name" />
        <label class="check"><input v-model="includeAttribution" type="checkbox" /> attribution</label>
        <span class="spacer" />
        <button :disabled="loading" @click="generate">{{ loading ? 'Generating…' : 'Regenerate' }}</button>
        <button class="primary" :disabled="!erld" @click="validate">Validate</button>
      </div>
      <div class="editor-host">
        <ErldEditor
          v-if="erld"
          ref="editor"
          :model-value="erld.code"
          :errors="erld.errors"
          @update:model-value="onEdit"
          @line="currentLine = $event"
        />
      </div>
      <div v-if="erld" class="status">
        <template v-if="!validated"><span class="muted">Changed – validate to check the syntax.</span></template>
        <template v-else-if="erld.errors.length === 0"><span class="ok">✓ Valid ERLD</span></template>
        <ul v-else class="errors">
          <li v-for="(e, i) in erld.errors" :key="i" @click="editor?.goToLine(e.line)">
            <b>{{ e.line }}:{{ e.column }}</b> {{ e.message }}
          </li>
        </ul>
      </div>
      <p v-if="error" class="error">{{ error }}</p>
    </section>
  </main>
</template>

<style scoped>
.editor-host {
  flex: 1;
  min-height: 0;
}
.model-name {
  width: 140px;
  padding: 5px 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
  font: inherit;
  font-size: 14px;
}
.check {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 13px;
  color: var(--muted);
}
.status {
  font-size: 13px;
  min-height: 20px;
}
.ok {
  color: #1e6b2e;
  font-weight: 600;
}
.muted,
.hint {
  color: var(--muted);
}
.hint {
  margin: 0;
  font-size: 13px;
}
.errors {
  margin: 0;
  padding-left: 18px;
  color: #b02a2a;
  max-height: 90px;
  overflow: auto;
}
.errors li {
  cursor: pointer;
}
</style>
