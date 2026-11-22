<script setup>
import { computed, ref, watch } from 'vue'
import ConceptualModelStep from './views/ConceptualModelStep.vue'
import ErldStep from './views/ErldStep.vue'

const text = ref('')
const conceptual = ref(null) // step 1 result
const erld = ref(null) // step 2 result
const step = ref('conceptual')

// A new conceptual model invalidates the ERLD generated from the previous one.
watch(conceptual, () => {
  erld.value = null
  if (!conceptual.value) step.value = 'conceptual'
})

const steps = computed(() => [
  { id: 'conceptual', label: '1. Conceptual model', enabled: true },
  { id: 'erld', label: '2. ERLD', enabled: !!conceptual.value },
  { id: 'ddl', label: '3. DDL', enabled: false },
  { id: 'erlq', label: '4. ERLQ', enabled: false }
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
