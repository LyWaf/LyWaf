<script setup lang="ts">
import { t } from '@/plugins/analysis/i18n'
import { ref } from 'vue'
import Section from '@/components/common/Section.vue'
import { ccApi } from '@/plugins/analysis/api'
import { useToast } from '@/composables/useToast'
import type { CcRule } from '@/types'

const { showSuccess, showError } = useToast()

const rules = ref<CcRule[]>([])

const addRule = async () => {
  const path = prompt(t('page.0d1a2b5cac0b'))
  if (!path) return
  
  const limitNumStr = prompt(t('page.b0a4621bd361'), '100')
  const limitNum = parseInt(limitNumStr || '100')
  
  const periodStr = prompt(t('page.47c6022e4411'), '60')
  const period = parseInt(periodStr || '60')
  
  const fbTimeStr = prompt(t('page.7e5304d28cb7'), '3600')
  const fbTime = parseInt(fbTimeStr || '3600')
  
  try {
    const res = await ccApi.addRule(path, limitNum, period, fbTime)
    if (res.success) {
      rules.value.push({
        id: Date.now().toString(),
        path,
        limitNum,
        period,
        fbTime,
      })
      showSuccess(t('page.3a49ba602563'))
    }
  } catch {
    showError(t('page.2ca15f1e2bb7'))
  }
}

const removeRule = async (rule: CcRule) => {
  if (!confirm(t('page.4e1e6e47f4b6', { p0: rule.path }))) return
  
  try {
    const res = await ccApi.removeRule(rule.path)
    if (res.success) {
      rules.value = rules.value.filter(r => r.id !== rule.id)
      showSuccess(t('page.04e366e33981'))
    }
  } catch {
    showError(t('page.c228558cf257'))
  }
}

const formatTime = (seconds: number) => {
  if (seconds >= 3600) return t('page.230784b15dbd', { p0: Math.floor(seconds / 3600) })
  if (seconds >= 60) return t('page.e235256e9729', { p0: Math.floor(seconds / 60) })
  return t('page.e9dc5dd21306', { p0: seconds })
}
</script>

<template>
  <Section id="cc-rules" :title="t('page.54ad621bd04c')">
    <template #actions>
      <button @click="addRule" class="btn btn-sm btn-primary">{{ t('page.fbf5151f677c') }}</button>
    </template>
    
    <div class="space-y-3">
      <div 
        v-for="rule in rules" 
        :key="rule.id"
        class="flex items-center justify-between p-4 bg-dark-card-hover rounded-lg"
      >
        <div class="flex items-center gap-6">
          <div>
            <span class="text-gray-400 text-sm">{{ t('page.77e1ea5c5688') }}</span>
            <div class="text-blue-400 font-mono">{{ rule.path }}</div>
          </div>
          <div>
            <span class="text-gray-400 text-sm">{{ t('page.688f892378ee') }}</span>
            <div class="text-gray-200">{{ rule.limitNum }}{{ t('page.37551ddc521b') }}{{ formatTime(rule.period) }}</div>
          </div>
          <div>
            <span class="text-gray-400 text-sm">{{ t('page.fb399f3df46d') }}</span>
            <div class="text-yellow-400">{{ formatTime(rule.fbTime) }}</div>
          </div>
        </div>
        <button 
          @click="removeRule(rule)"
          class="btn btn-sm btn-danger"
        >
          {{ t('page.2f9daa828907') }}
        </button>
      </div>
      
      <div v-if="rules.length === 0" class="text-gray-500 text-center py-8">
        {{ t('page.a3d955c29e10') }}
      </div>
    </div>
  </Section>
</template>
