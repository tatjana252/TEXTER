<script setup>
import { computed, ref } from 'vue'
import RequirementsText from './components/RequirementsText.vue'
import ErDiagram from './components/ErDiagram.vue'
import ConfidenceLegend from './components/ConfidenceLegend.vue'
import ElementDetails from './components/ElementDetails.vue'
import { texterApi } from './services/texterApi'
import { LIBRARY_SAMPLE } from './samples'

// TEXTER pipeline steps; the next ones are enabled as they get implemented.
const steps = [
  { id: 'conceptual', label: '1. Conceptual model', enabled: true },
  { id: 'erld', label: '2. ERLD', enabled: false },
  { id: 'ddl', label: '3. DDL', enabled: false },
  { id: 'erlq', label: '4. ERLQ', enabled: false }
]

const text = ref('')
const result = ref(null) // { model, diagram, sentences, notes }
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
  <div class="app">
    <header>
      <h1>TEXTER</h1>
      <nav>
        <button v-for="step in steps" :key="step.id" :class="{ current: step.id === 'conceptual' }" :disabled="!step.enabled">
          {{ step.label }}
        </button>
      </nav>
    </header>

    <main>
      <section class="panel text-panel">
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

      <section class="panel diagram-panel">
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
  </div>
</template>

<style scoped>
.app {
  display: flex;
  flex-direction: column;
  height: 100vh;
}
header {
  display: flex;
  align-items: center;
  gap: 24px;
  padding: 10px 20px;
  border-bottom: 1px solid var(--border);
  background: white;
}
h1 {
  margin: 0;
  font-size: 20px;
  letter-spacing: 2px;
}
nav {
  display: flex;
  gap: 4px;
}
nav button.current {
  background: var(--accent);
  border-color: var(--accent);
  color: white;
}
main {
  flex: 1;
  display: grid;
  grid-template-columns: minmax(320px, 2fr) 3fr;
  gap: 16px;
  padding: 16px;
  min-height: 0;
}
.panel {
  display: flex;
  flex-direction: column;
  gap: 10px;
  min-height: 0;
}
.toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 34px;
}
.spacer {
  flex: 1;
}
h2 {
  margin: 0;
  font-size: 15px;
  font-weight: 600;
}
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
.error {
  margin: 0;
  color: #b02a2a;
}
@media (max-width: 900px) {
  main {
    grid-template-columns: 1fr;
    grid-template-rows: 40vh 70vh;
  }
}
</style>
