<script setup>
import { computed, ref, watch } from 'vue'
import ConceptualModelStep from './views/ConceptualModelStep.vue'
import ErldStep from './views/ErldStep.vue'
import DdlStep from './views/DdlStep.vue'
import ErlqStep from './views/ErlqStep.vue'

const text = ref('')
const conceptual = ref(null) // step 1 result
const erld = ref(null) // step 2 result
const ddl = ref(null) // step 3 result
const query = ref('') // step 4 query text
const step = ref('conceptual')

// A new conceptual model invalidates the ERLD generated from the previous one.
watch(conceptual, () => {
  erld.value = null
  ddl.value = null
  if (!conceptual.value) step.value = 'conceptual'
})

const steps = computed(() => [
  { id: 'conceptual', label: '1. Conceptual model', enabled: true },
  { id: 'erld', label: '2. ERLD', enabled: !!conceptual.value },
  { id: 'ddl', label: '3. DDL', enabled: !!erld.value?.code },
  { id: 'erlq', label: '4. ERLQ', enabled: !!ddl.value?.sql }
])
</script>

<template>
  <div class="app">
    <header>
      <h1>TEXTER</h1>
      <nav>
        <button
          v-for="s in steps"
          :key="s.id"
          :class="{ current: s.id === step }"
          :disabled="!s.enabled"
          @click="step = s.id"
        >
          {{ s.label }}
        </button>
      </nav>
    </header>

    <ConceptualModelStep v-show="step === 'conceptual'" v-model:text="text" v-model:result="conceptual" />
    <ErldStep v-if="step === 'erld' && conceptual" v-model:erld="erld" :text="text" :conceptual="conceptual" />
    <DdlStep v-if="step === 'ddl' && erld?.code" v-model:ddl="ddl" :text="text" :conceptual="conceptual" :erld-code="erld.code" />
    <ErlqStep
      v-if="step === 'erlq' && ddl?.sql"
      v-model:query="query"
      :text="text"
      :erld-code="ddl.erldCode"
      :created-database="ddl.deployed?.database ?? null"
      :created-mongo-database="ddl.mongo?.deployed?.database ?? null"
      :initial-store="ddl.store ?? 'sqlserver'"
    />
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
</style>
