<script setup>
import { computed, onMounted, ref } from 'vue'
import ErldEditor from '../components/ErldEditor.vue'
import { texterApi } from '../services/texterApi'

const props = defineProps({
  text: { type: String, required: true },
  /** ERLD source the DDL of step 3 was generated from */
  erldCode: { type: String, required: true },
  /** Database created in step 3 on SQL Server (Create database); null: none, a temporary database is used */
  createdDatabase: { type: String, default: null },
  /** Database created in step 3 on MongoDB; null: none */
  createdMongoDatabase: { type: String, default: null },
  /** Store shown in step 3 when leaving it */
  initialStore: { type: String, default: 'sqlserver' }
})

/** Where the queries run: SQL Server or MongoDB. */
const store = ref(props.initialStore)
const isMongo = computed(() => store.value === 'mongodb')
const createdHere = computed(() => (isMongo.value ? props.createdMongoDatabase : props.createdDatabase))

/** Database the queries run in: the one from step 3, or '' for the temporary one. */
const target = ref(createdHere.value ?? '')
const targetOrNull = () => target.value || null

function switchStore(which) {
  if (store.value === which) return
  store.value = which
  target.value = createdHere.value ?? ''
  database.value = null
  switchTarget()
}

function switchTarget() {
  result.value = null
  tableRows.value = null
  rejected.value = []
  loadDatabase()
}
/** ERLQ query text (kept when switching steps) */
const query = defineModel('query', { type: String, default: '' })

const database = ref(null) // { server, database, tables, examples }
const copiedName = ref(false)

async function copyDatabaseName() {
  await navigator.clipboard.writeText(database.value.database)
  copiedName.value = true
  setTimeout(() => (copiedName.value = false), 1500)
}
const result = ref(null) // { sql, columns, rows, truncated, errors, executionError }
const rejected = ref([])
const tab = ref('result')
const table = ref('')
const tableRows = ref(null)
const busy = ref('') // 'database' | 'data' | 'run' | 'table'
const error = ref('')

const totalRows = computed(() => database.value?.tables.reduce((sum, t) => sum + t.rows, 0) ?? 0)

onMounted(loadDatabase)

async function loadDatabase() {
  await call('database', async () => {
    database.value = await texterApi.erlqDatabase(props.erldCode, store.value, targetOrNull())
    if (!query.value && database.value.examples.length) query.value = database.value.examples[0]
    table.value ||= database.value.tables[0]?.name ?? ''
  })
}

async function generateData() {
  await call('data', async () => {
    const loaded = await texterApi.erlqSampleData(props.erldCode, store.value, props.text, targetOrNull())
    database.value = { ...database.value, tables: loaded.tables }
    rejected.value = loaded.rejected
    if (tab.value === 'data') await showTable()
    if (result.value) await run()
  })
}

async function run() {
  if (!query.value.trim()) return
  await call('run', async () => {
    result.value = await texterApi.erlqRun(props.erldCode, store.value, query.value, targetOrNull())
    tab.value = 'result'
  })
}

async function showTable() {
  if (!table.value) return
  await call('table', async () => {
    tableRows.value = await texterApi.erlqTable(props.erldCode, store.value, table.value, targetOrNull())
  })
}

async function call(what, action) {
  busy.value = what
  error.value = ''
  try {
    await action()
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = ''
  }
}

function onKeydown(event) {
  if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
    event.preventDefault()
    run()
  }
}

function openData() {
  tab.value = 'data'
  if (!tableRows.value) showTable()
}
</script>

<template>
  <main class="two-panels">
    <section class="panel">
      <div class="toolbar">
        <h2>ERLQ</h2>
        <select v-if="database?.examples.length" class="examples" @change="query = $event.target.value; $event.target.selectedIndex = 0">
          <option value="">Examples…</option>
          <option v-for="(example, i) in database.examples" :key="i" :value="example">{{ example.replace(/\s+/g, ' ') }}</option>
        </select>
        <span class="spacer" />
        <button class="primary" :disabled="busy === 'run' || !query.trim()" @click="run">
          {{ busy === 'run' ? 'Running…' : 'Run' }}
        </button>
      </div>
      <div class="query-host" @keydown="onKeydown">
        <ErldEditor v-model="query" :errors="result?.errors ?? []" />
      </div>
      <ul v-if="result?.errors.length" class="errors">
        <li v-for="(e, i) in result.errors" :key="i"><b>{{ e.line }}:{{ e.column }}</b> {{ e.message }}</li>
      </ul>
      <p class="hint">
        Ctrl+Enter runs the query. Long form: <code>from r in Entity where … select …</code>; short form: a path such as
        <code>Entity[attribute &gt; 0].role</code>.
      </p>
      <div v-if="result?.sql" class="sql">
        <div class="label">{{ isMongo ? 'Translated to a MongoDB aggregation pipeline' : 'Translated to SQL Server' }}</div>
        <pre>{{ result.sql }}</pre>
      </div>
    </section>

    <section class="panel">
      <div class="toolbar">
        <h2>Database</h2>
        <span v-if="database" class="muted">
          {{ database.tables.length }} {{ isMongo ? 'collections' : 'tables' }}, {{ totalRows }} {{ isMongo ? 'documents' : 'rows' }}
        </span>
        <span class="spacer" />
        <button
          :disabled="!!busy"
          :title="`The LLM writes sample rows; they replace the rows in the model's tables of ${database?.database ?? 'the database'}`"
          @click="generateData"
        >
          {{ busy === 'data' ? 'The LLM is writing data…' : totalRows ? 'Regenerate sample data' : 'Generate sample data' }}
        </button>
      </div>

      <div class="stores">
        <button :class="{ current: !isMongo }" @click="switchStore('sqlserver')">SQL Server</button>
        <button :class="{ current: isMongo }" @click="switchStore('mongodb')">MongoDB</button>
      </div>
      <div v-if="createdHere" class="connection">
        <label>
          <span class="muted">Run in</span>
          <select v-model="target" @change="switchTarget">
            <option :value="createdHere">{{ createdHere }} (created in step 3)</option>
            <option value="">a temporary database built from the DDL</option>
          </select>
        </label>
      </div>
      <div v-if="database" class="connection">
        <span><span class="muted">Instance</span> <code>{{ database.server }}</code></span>
        <span>
          <span class="muted">Database</span> <code>{{ database.database }}</code>
          <button class="link" title="Copy the database name (to open it in SSMS)" @click="copyDatabaseName">
            {{ copiedName ? 'copied' : 'copy' }}
          </button>
        </span>
      </div>

      <div class="tabs">
        <button :class="{ current: tab === 'result' }" @click="tab = 'result'">Result</button>
        <button :class="{ current: tab === 'data' }" @click="openData">Data</button>
      </div>

      <div v-if="busy === 'database'" class="placeholder">Creating the database from the DDL…</div>
      <div v-else-if="database && !totalRows && tab === 'result' && !result" class="placeholder">
        The database is empty. Generate sample data (the LLM writes rows that fit the requirements and the constraints).
      </div>

      <template v-if="tab === 'result' && result">
        <p v-if="result.executionError" class="error">SQL Server: {{ result.executionError }}</p>
        <div v-else-if="result.sql" class="grid-host">
          <table class="grid">
            <thead>
              <tr><th v-for="(c, i) in result.columns" :key="i">{{ c }}</th></tr>
            </thead>
            <tbody>
              <tr v-for="(row, r) in result.rows" :key="r">
                <td v-for="(v, i) in row" :key="i" :class="{ null: v === null }">{{ v ?? 'NULL' }}</td>
              </tr>
            </tbody>
          </table>
          <p class="muted count">{{ result.rows.length }} row(s){{ result.truncated ? ' (truncated)' : '' }}</p>
        </div>
      </template>

      <template v-if="tab === 'data' && database">
        <div class="table-picker">
          <select v-model="table" @change="showTable">
            <option v-for="t in database.tables" :key="t.name" :value="t.name">{{ t.name }} ({{ t.rows }})</option>
          </select>
        </div>
        <div v-if="tableRows" class="grid-host">
          <table class="grid">
            <thead>
              <tr><th v-for="(c, i) in tableRows.columns" :key="i">{{ c }}</th></tr>
            </thead>
            <tbody>
              <tr v-for="(row, r) in tableRows.rows" :key="r">
                <td v-for="(v, i) in row" :key="i" :class="{ null: v === null }">{{ v ?? 'NULL' }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>

      <details v-if="rejected.length" class="warnings">
        <summary>Rows the database rejected ({{ rejected.length }})</summary>
        <ul>
          <li v-for="(r, i) in rejected" :key="i">{{ r }}</li>
        </ul>
      </details>
      <p v-if="error" class="error">{{ error }}</p>
    </section>
  </main>
</template>

<style scoped>
.query-host {
  height: 180px;
  flex: none;
}
.examples {
  /* Shrinks first, so that Run stays inside the panel on narrow screens. */
  flex: 0 1 320px;
  min-width: 0;
  padding: 5px 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
  font: inherit;
  font-size: 13px;
}
.hint,
.muted {
  margin: 0;
  font-size: 13px;
  color: var(--muted);
}
.errors {
  margin: 0;
  padding-left: 18px;
  color: #b02a2a;
  font-size: 13px;
}
.sql {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
.label {
  font-size: 12px;
  font-weight: 600;
  color: var(--muted);
  margin-bottom: 4px;
}
pre {
  flex: 1;
  margin: 0;
  padding: 10px 12px;
  overflow: auto;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: #f6f7f9;
  font: 12.5px/1.55 ui-monospace, 'Cascadia Code', Consolas, monospace;
  white-space: pre;
}
.connection {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 16px;
  font-size: 13px;
}
.stores {
  display: flex;
  gap: 2px;
}
.stores button.current {
  background: var(--accent);
  border-color: var(--accent);
  color: white;
}
.connection select {
  margin-left: 6px;
  padding: 3px 6px;
  border: 1px solid var(--border);
  border-radius: 6px;
  font: inherit;
}
.connection code {
  font-size: 12.5px;
  background: #f6f7f9;
  border: 1px solid var(--border);
  border-radius: 4px;
  padding: 1px 6px;
}
.tabs {
  display: flex;
  gap: 4px;
}
.tabs button.current {
  background: var(--accent);
  border-color: var(--accent);
  color: white;
}
.placeholder {
  padding: 24px;
  text-align: center;
  color: var(--muted);
  border: 1px dashed var(--border);
  border-radius: 8px;
}
.grid-host {
  flex: 1;
  min-height: 0;
  overflow: auto;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: white;
}
.grid {
  border-collapse: collapse;
  font-size: 13px;
  min-width: 100%;
}
.grid th,
.grid td {
  padding: 5px 10px;
  border-bottom: 1px solid var(--border);
  text-align: left;
  white-space: nowrap;
}
.grid th {
  position: sticky;
  top: 0;
  background: #f6f7f9;
  font-weight: 600;
}
.grid td.null {
  color: #9aa1aa;
  font-style: italic;
}
.count {
  padding: 6px 10px;
}
.table-picker select {
  padding: 5px 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
  font: inherit;
  font-size: 13px;
}
.warnings {
  font-size: 13px;
  color: #7a5c00;
  max-height: 140px;
  overflow: auto;
}
.warnings summary {
  cursor: pointer;
}
.warnings ul {
  margin: 6px 0 0;
  padding-left: 18px;
}
</style>
