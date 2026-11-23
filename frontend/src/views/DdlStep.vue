<script setup>
import { computed, onMounted, ref } from 'vue'
import RequirementsText from '../components/RequirementsText.vue'
import ErldEditor from '../components/ErldEditor.vue'
import { attributionAt } from '../diagram/lineAttribution'
import { texterApi } from '../services/texterApi'

const props = defineProps({
  text: { type: String, required: true },
  /** Result of step 1: { model, sentences, ... } */
  conceptual: { type: Object, required: true },
  /** ERLD source from step 2 (generated or edited) */
  erldCode: { type: String, required: true }
})
/** { sql, errors, warnings, erldCode, includeAttribution } */
const ddl = defineModel('ddl', { type: Object, default: null })

const includeAttribution = ref(ddl.value?.includeAttribution ?? true)
const loading = ref(false)
const error = ref('')
const currentLine = ref(0)
const copied = ref(false)

// The ERLD may have been edited since the DDL was generated.
onMounted(() => {
  if (!ddl.value || ddl.value.erldCode !== props.erldCode) generate()
})

async function generate() {
  loading.value = true
  error.value = ''
  try {
    const result = await texterApi.generateSqlServerDdl(props.erldCode, includeAttribution.value)
    ddl.value = { ...result, erldCode: props.erldCode, includeAttribution: includeAttribution.value }
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

async function copy() {
  await navigator.clipboard.writeText(ddl.value.sql)
  copied.value = true
  setTimeout(() => (copied.value = false), 1500)
}

function download() {
  const name = (props.erldCode.match(/^\s*model\s+([^\s;]+)/im)?.[1] ?? 'texter').replace(/"/g, '')
  const url = URL.createObjectURL(new Blob([ddl.value.sql], { type: 'text/plain;charset=utf-8' }))
  const link = Object.assign(document.createElement('a'), { href: url, download: `${name}.sqlserver.sql` })
  link.click()
  URL.revokeObjectURL(url)
}

/** @source / @confidence of the SQL line under the cursor (or of the comments above a CREATE TABLE). */
const attribution = computed(() => attributionAt(ddl.value?.sql, currentLine.value, props.conceptual.sentences))
const hasErrors = computed(() => (ddl.value?.errors.length ?? 0) > 0)
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
        :spans="attribution.spans"
        :confidence-level="attribution.level"
        :sentences="conceptual.sentences"
        :active-sentence-ids="attribution.sentenceIds"
      />
      <p class="hint">Hover over a table or column with <code>@source</code> to see the requirements it comes from.</p>
    </section>

    <section class="panel">
      <div class="toolbar">
        <h2>SQL Server DDL</h2>
        <label class="check"><input v-model="includeAttribution" type="checkbox" /> attribution</label>
        <span class="spacer" />
        <button :disabled="loading" @click="generate">{{ loading ? 'Generating…' : 'Regenerate' }}</button>
        <button :disabled="!ddl?.sql" @click="copy">{{ copied ? 'Copied' : 'Copy' }}</button>
        <button class="primary" :disabled="!ddl?.sql" @click="download">Download .sql</button>
      </div>

      <div v-if="hasErrors" class="errors-panel">
        <p>The ERLD has errors, so no DDL was generated. Fix them in step 2:</p>
        <ul>
          <li v-for="(e, i) in ddl.errors" :key="i"><b>ERLD {{ e.line }}:{{ e.column }}</b> {{ e.message }}</li>
        </ul>
      </div>
      <div v-else class="editor-host">
        <ErldEditor v-if="ddl" :model-value="ddl.sql" readonly @line="currentLine = $event" />
      </div>

      <details v-if="ddl?.warnings.length" class="warnings">
        <summary>Warnings ({{ ddl.warnings.length }})</summary>
        <ul>
          <li v-for="(w, i) in ddl.warnings" :key="i"><b v-if="w.line">ERLD {{ w.line }}:{{ w.column }}</b> {{ w.message }}</li>
        </ul>
      </details>
      <p v-if="error" class="error">{{ error }}</p>
    </section>
  </main>
</template>

<style scoped>
.editor-host {
  flex: 1;
  min-height: 0;
}
.check {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 13px;
  color: var(--muted);
}
.hint {
  margin: 0;
  font-size: 13px;
  color: var(--muted);
}
.errors-panel {
  flex: 1;
  color: #b02a2a;
  font-size: 14px;
}
.errors-panel ul,
.warnings ul {
  margin: 6px 0 0;
  padding-left: 18px;
}
.warnings {
  font-size: 13px;
  color: #7a5c00;
  max-height: 120px;
  overflow: auto;
}
.warnings summary {
  cursor: pointer;
}
</style>
