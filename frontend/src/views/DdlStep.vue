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
/**
 * SQL Server: { sql, errors, warnings, erldCode, includeAttribution, dropExisting, deployed: { database, server } }
 * plus store ('sqlserver' | 'mongodb') and
 * mongo: { script, notEnforced, errors, warnings, erldCode, dropExisting, deployed: { database, server } }
 */
const ddl = defineModel('ddl', { type: Object, default: null })

const store = ref(ddl.value?.store ?? 'sqlserver')
const isMongo = computed(() => store.value === 'mongodb')
const includeAttribution = ref(ddl.value?.includeAttribution ?? true)
const dropExisting = ref(ddl.value?.dropExisting ?? false)
const dropCollections = ref(ddl.value?.mongo?.dropExisting ?? false)
const loading = ref(false)
const error = ref('')
const currentLine = ref(0)
const copied = ref(false)

// Database to create; remembered in this browser only (one name per store).
const databaseName = ref(readName(store.value))
const deploying = ref(false)
const deployed = ref(null) // { database, server, created, tables, error }

function readName(which) {
  try {
    return localStorage.getItem(`texter.databaseName.${which}`) ?? localStorage.getItem('texter.databaseName') ?? ''
  } catch {
    return ''
  }
}

function switchStore(which) {
  store.value = which
  ddl.value = { ...ddl.value, store: which }
  databaseName.value = readName(which)
  deployed.value = null
  currentLine.value = 0
  if (which === 'mongodb' && ddl.value?.mongo?.erldCode !== props.erldCode) generateMongo()
}

async function deploy() {
  deploying.value = true
  deployed.value = null
  const name = databaseName.value.trim()
  try {
    localStorage.setItem(`texter.databaseName.${store.value}`, name)
  } catch {
    // no storage (private window): the name is just not remembered
  }
  try {
    if (isMongo.value) {
      deployed.value = await texterApi.deployMongoSchema(props.erldCode, name, dropCollections.value)
      if (!deployed.value.error) ddl.value = { ...ddl.value, mongo: { ...ddl.value.mongo, deployed: { database: deployed.value.database, server: deployed.value.server } } }
    } else {
      deployed.value = await texterApi.deploySqlServerDdl(props.erldCode, name, includeAttribution.value, dropExisting.value)
      // Step 4 runs its queries in this database.
      if (!deployed.value.error) ddl.value = { ...ddl.value, deployed: { database: deployed.value.database, server: deployed.value.server } }
    }
  } catch (e) {
    deployed.value = { error: e.message }
  } finally {
    deploying.value = false
  }
}

// The ERLD may have been edited since the DDL was generated.
onMounted(() => {
  if (!ddl.value || ddl.value.erldCode !== props.erldCode) generate()
  if (isMongo.value && ddl.value?.mongo?.erldCode !== props.erldCode) generateMongo()
})

function regenerate() {
  return isMongo.value ? generateMongo() : generate()
}

async function generate() {
  loading.value = true
  error.value = ''
  try {
    const result = await texterApi.generateSqlServerDdl(props.erldCode, includeAttribution.value, dropExisting.value)
    // A regenerated DDL for the same ERLD keeps the database it was created in.
    const same = ddl.value?.erldCode === props.erldCode
    ddl.value = {
      ...result,
      erldCode: props.erldCode,
      includeAttribution: includeAttribution.value,
      dropExisting: dropExisting.value,
      deployed: same ? ddl.value?.deployed : null,
      store: store.value,
      mongo: ddl.value?.mongo?.erldCode === props.erldCode ? ddl.value.mongo : null
    }
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

async function generateMongo() {
  loading.value = true
  error.value = ''
  try {
    const result = await texterApi.generateMongoSchema(props.erldCode, dropCollections.value)
    const same = ddl.value?.mongo?.erldCode === props.erldCode
    ddl.value = {
      ...ddl.value,
      mongo: { ...result, erldCode: props.erldCode, dropExisting: dropCollections.value, deployed: same ? ddl.value.mongo.deployed : null }
    }
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

/** What is shown: the SQL Server DDL or the MongoDB script. */
const current = computed(() =>
  isMongo.value
    ? ddl.value?.mongo && { code: ddl.value.mongo.script, errors: ddl.value.mongo.errors, warnings: ddl.value.mongo.warnings }
    : ddl.value && { code: ddl.value.sql, errors: ddl.value.errors, warnings: ddl.value.warnings }
)

async function copy() {
  await navigator.clipboard.writeText(current.value.code)
  copied.value = true
  setTimeout(() => (copied.value = false), 1500)
}

function download() {
  const name = (props.erldCode.match(/^\s*model\s+([^\s;]+)/im)?.[1] ?? 'texter').replace(/"/g, '')
  const url = URL.createObjectURL(new Blob([current.value.code], { type: 'text/plain;charset=utf-8' }))
  const file = isMongo.value ? `${name}.mongodb.js` : `${name}.sqlserver.sql`
  const link = Object.assign(document.createElement('a'), { href: url, download: file })
  link.click()
  URL.revokeObjectURL(url)
}

/** @source / @confidence of the SQL line under the cursor (or of the comments above a CREATE TABLE). */
const attribution = computed(() =>
  isMongo.value ? { sentenceIds: [], spans: [], level: 'High' } : attributionAt(ddl.value?.sql, currentLine.value, props.conceptual.sentences)
)
const hasErrors = computed(() => (current.value?.errors.length ?? 0) > 0)
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
      <p v-if="!isMongo" class="hint">Hover over a table or column with <code>@source</code> to see the requirements it comes from.</p>
      <div v-else-if="ddl?.mongo?.notEnforced?.length" class="not-enforced">
        <b>Not checked by MongoDB</b>
        <ul>
          <li v-for="(n, i) in ddl.mongo.notEnforced" :key="i">{{ n }}</li>
        </ul>
      </div>
    </section>

    <section class="panel">
      <div class="toolbar">
        <div class="stores">
          <button :class="{ current: !isMongo }" @click="switchStore('sqlserver')">SQL Server</button>
          <button :class="{ current: isMongo }" @click="switchStore('mongodb')">MongoDB</button>
        </div>
        <template v-if="!isMongo">
          <label class="check"><input v-model="includeAttribution" type="checkbox" @change="generate" /> attribution</label>
          <label
            class="check"
            title="Starts the script by dropping its tables (and foreign keys to them) if they exist, so it can be run again in the same database. Their data is lost."
          >
            <input v-model="dropExisting" type="checkbox" @change="generate" /> drop existing tables
          </label>
        </template>
        <label
          v-else
          class="check"
          title="Drops the collections of the schema if they exist, so it can be created again in the same database. Their documents are lost."
        >
          <input v-model="dropCollections" type="checkbox" @change="generateMongo" /> drop existing collections
        </label>
        <span class="spacer" />
        <button :disabled="loading" @click="regenerate">{{ loading ? 'Generating…' : 'Regenerate' }}</button>
        <button :disabled="!current?.code" @click="copy">{{ copied ? 'Copied' : 'Copy' }}</button>
        <button class="primary" :disabled="!current?.code" @click="download">{{ isMongo ? 'Download .js' : 'Download .sql' }}</button>
      </div>

      <div v-if="current?.code" class="deploy">
        <input
          v-model="databaseName"
          class="database-name"
          placeholder="Database name, e.g. Biblioteka"
          :title="isMongo ? 'Created on MongoDB with the collections (validators) and indexes below' : 'Created on SQL Server if it does not exist; the DDL above is run in it'"
          @keydown.enter="databaseName.trim() && deploy()"
        />
        <button class="primary" :disabled="deploying || !databaseName.trim()" @click="deploy">
          {{ deploying ? 'Creating…' : 'Create database' }}
        </button>
        <span v-if="deployed?.error" class="deploy-error">{{ deployed.error }}</span>
        <span v-else-if="deployed" class="deploy-ok">
          ✓ {{ deployed.created ? 'Created' : 'Updated' }} <b>{{ deployed.database }}</b> on {{ deployed.server }}:
          {{ deployed.tables.length }} {{ isMongo ? 'collections' : 'tables' }}
        </span>
      </div>

      <div v-if="hasErrors" class="errors-panel">
        <p>The ERLD has errors, so nothing was generated. Fix them in step 2:</p>
        <ul>
          <li v-for="(e, i) in current.errors" :key="i"><b>ERLD {{ e.line }}:{{ e.column }}</b> {{ e.message }}</li>
        </ul>
      </div>
      <div v-else class="editor-host">
        <ErldEditor v-if="current" :model-value="current.code" readonly @line="currentLine = $event" />
      </div>

      <details v-if="current?.warnings.length" class="warnings">
        <summary>Warnings ({{ current.warnings.length }})</summary>
        <ul>
          <li v-for="(w, i) in current.warnings" :key="i"><b v-if="w.line">ERLD {{ w.line }}:{{ w.column }}</b> {{ w.message }}</li>
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
.stores {
  display: flex;
  gap: 2px;
}
.stores button.current {
  background: var(--accent);
  border-color: var(--accent);
  color: white;
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
.not-enforced {
  font-size: 13px;
  color: var(--muted);
  max-height: 180px;
  overflow: auto;
}
.not-enforced ul {
  margin: 4px 0 0;
  padding-left: 18px;
}
.deploy {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  font-size: 13px;
}
.database-name {
  width: 220px;
  padding: 5px 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
  font: inherit;
  font-size: 14px;
}
.deploy-ok {
  color: #1e6b2e;
}
.deploy-error {
  color: #b02a2a;
  flex-basis: 100%;
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
