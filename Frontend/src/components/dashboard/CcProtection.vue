<script setup lang="ts">
import { ref } from 'vue'
import Section from '@/components/common/Section.vue'
import { ccApi } from '@/api'
import { useToast } from '@/composables/useToast'
import type { CcRule } from '@/types'

const { showSuccess, showError } = useToast()

const rules = ref<CcRule[]>([])

const addRule = async () => {
  const path = prompt('请输入路径前缀（如 /api/）:')
  if (!path) return
  
  const limitNumStr = prompt('请输入限制次数:', '100')
  const limitNum = parseInt(limitNumStr || '100')
  
  const periodStr = prompt('请输入时间窗口（秒）:', '60')
  const period = parseInt(periodStr || '60')
  
  const fbTimeStr = prompt('请输入封禁时间（秒）:', '3600')
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
      showSuccess('CC 规则已添加')
    }
  } catch {
    showError('添加失败')
  }
}

const removeRule = async (rule: CcRule) => {
  if (!confirm(`确定要删除规则 ${rule.path} 吗？`)) return
  
  try {
    const res = await ccApi.removeRule(rule.path)
    if (res.success) {
      rules.value = rules.value.filter(r => r.id !== rule.id)
      showSuccess('规则已删除')
    }
  } catch {
    showError('删除失败')
  }
}

const formatTime = (seconds: number) => {
  if (seconds >= 3600) return `${Math.floor(seconds / 3600)}小时`
  if (seconds >= 60) return `${Math.floor(seconds / 60)}分钟`
  return `${seconds}秒`
}
</script>

<template>
  <Section id="cc-rules" title="CC 防护规则">
    <template #actions>
      <button @click="addRule" class="btn btn-sm btn-primary">+ 添加规则</button>
    </template>
    
    <div class="space-y-3">
      <div 
        v-for="rule in rules" 
        :key="rule.id"
        class="flex items-center justify-between p-4 bg-dark-card-hover rounded-lg"
      >
        <div class="flex items-center gap-6">
          <div>
            <span class="text-gray-400 text-sm">路径</span>
            <div class="text-blue-400 font-mono">{{ rule.path }}</div>
          </div>
          <div>
            <span class="text-gray-400 text-sm">限制</span>
            <div class="text-gray-200">{{ rule.limitNum }}次/{{ formatTime(rule.period) }}</div>
          </div>
          <div>
            <span class="text-gray-400 text-sm">封禁时长</span>
            <div class="text-yellow-400">{{ formatTime(rule.fbTime) }}</div>
          </div>
        </div>
        <button 
          @click="removeRule(rule)"
          class="btn btn-sm btn-danger"
        >
          删除
        </button>
      </div>
      
      <div v-if="rules.length === 0" class="text-gray-500 text-center py-8">
        暂无 CC 防护规则
      </div>
    </div>
  </Section>
</template>
