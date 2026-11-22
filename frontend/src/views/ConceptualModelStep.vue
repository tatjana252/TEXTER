<script setup>
import { computed, ref } from 'vue'
import RequirementsText from '../components/RequirementsText.vue'
import ErDiagram from '../components/ErDiagram.vue'
import ConfidenceLegend from '../components/ConfidenceLegend.vue'
import ElementDetails from '../components/ElementDetails.vue'
import { texterApi } from '../services/texterApi'
import { LIBRARY_SAMPLE } from '../samples'

const text = defineModel('text', { type: String, required: true })
/** { model, diagram, sentences, notes } */
const result = defineModel('result', { type: Object, default: null })

const loading = ref(false)
const error = ref('')
const hovered = ref(null)
const selected = ref(null)
const diagramRef = ref(null)

const isViewing = computed(() => result.value !== null)
const active = computed(() => hovered.value ?? selected.value)

async function generate() {
  loading.value = true
  error.value = ''
  try {
    result.value = await texterApi.extractConceptualModel(text.value)
    hovered.value = selected.value = null
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

function edit() {
  result.value = null
  hovered.value = selected.value = null
}
</script>

<template>
  <main class="two-panels">
    <section class="panel">
      <div class="toolbar">
        <h2>Requirements</h2>
        <span class="spacer" />
        <template v-if="!isViewing">
          <button class="link" @click="text = LIBRARY_SAMPLE">Load example</button>
          <button class="primary" :disabled="loading || text.trim().length < 10" @click="generate">
            {{ loading ? 'Generating…' : 'Generate diagram' }}
          </button>
        </template>
        <button v-else @click="edit">Edit text</button>
      </div>
      <RequirementsText
        v-model="text"
        :readonly="isViewing"
        :spans="active?.spans ?? []"
        :confidence-level="active?.confidenceLevel"
        :sentences="result?.sentences ?? []"
        :active-sentence-ids="active?.sentenceIds ?? []"
      />
      <details v-if="result?.notes.length" class="notes">
        <summary>Notes from the analysis ({{ result.notes.length }})</summary>
        <ul>
          <li v-for="(note, i) in result.notes" :key="i">{{ note }}</li>
        </ul>
      </details>
      <p v-if="error" class="error">{{ error }}</p>
    </section>

    <section class="panel">
      <div class="toolbar">
        <h2>PMER diagram</h2>
        <span class="spacer" />
        <ConfidenceLegend v-if="result" :nodes="result.diagram.nodes" />
        <button v-if="result" @click="diagramRef?.relayout()">Re-layout</button>
      </div>
      <div class="diagram-host">
        <ErDiagram ref="diagramRef" :model="result?.diagram" @hover="hovered = $event" @select="selected = $event" />
        <div v-if="loading" class="overlay">The LLM is analysing the text…</div>
        <div v-else-if="!result" class="overlay muted">Enter requirements and generate the diagram.</div>
      </div>
      <ElementDetails v-if="result" :element="active" />
    </section>
  </main>
</template>

<style scoped>
.diagram-host {
  position: relative;
  flex: 1;
  min-height: 0;
  border: 1px solid var(--border);
  border-radius: 8px;
  overflow: hidden;
}
.overlay {
  position: absolute;
  inset: 0;
  display: grid;
  place-items: center;
  background: rgba(251, 251, 252, 0.85);
  font-size: 15px;
}
.overlay.muted {
  color: var(--muted);
}
.notes {
  font-size: 13px;
  color: var(--muted);
}
.notes summary {
  cursor: pointer;
}
.notes ul {
  margin: 6px 0 0;
  padding-left: 18px;
}
</style>
