<script setup lang="ts">
import { ref } from 'vue'
import Section from '@/components/common/Section.vue'
import { wafApi } from '@/api'
import { useToast } from '@/composables/useToast'
import type { WafRule } from '@/types'

const { showSuccess, showError } = useToast()

const argsRules = ref<WafRule[]>([])
const postRules = ref<WafRule[]>([])

// Args 规则操作
const addArgsRule = async () => {
  const pattern = prompt('请输入 Args 检测规则（正则表达式）:')
  if (!pattern) return
  
  try {
    const res = await wafApi.addArgsRule(pattern)
    if (res.success) {
      argsRules.value.push({ id: Date.now().toString(), pattern, type: 'args' })
      showSuccess('Args 规则已添加')
    }
  } catch {
    showError('添加失败')
  }
}

const removeArgsRule = async (rule: WafRule) => {
  if (!confirm('确定要删除此规则吗？')) return
  
  try {
    const res = await wafApi.removeArgsRule(rule.pattern)
    if (res.success) {
      argsRules.value = argsRules.value.filter(r => r.id !== rule.id)
      showSuccess('规则已删除')
    }
  } catch {
    showError('删除失败')
  }
}

// Post 规则操作
const addPostRule = async () => {
  const pattern = prompt('请输入 Post 检测规则（正则表达式）:')
  if (!pattern) return
  
  try {
    const res = await wafApi.addPostRule(pattern)
    if (res.success) {
      postRules.value.push({ id: Date.now().toString(), pattern, type: 'post' })
      showSuccess('Post 规则已添加')
    }
  } catch {
    showError('添加失败')
  }
}

const removePostRule = async (rule: WafRule) => {
  if (!confirm('确定要删除此规则吗？')) return
  
  try {
    const res = await wafApi.removePostRule(rule.pattern)
    if (res.success) {
      postRules.value = postRules.value.filter(r => r.id !== rule.id)
      showSuccess('规则已删除')
    }
  } catch {
    showError('删除失败')
  }
}
</script>

<template>
  <Section id="waf-rules" title="WAF 规则">
    <div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
      <!-- Args 规则 -->
      <div>
        <div class="flex items-center justify-between mb-4">
          <h3 class="text-gray-300 font-medium">
            Args 检测规则
            <span class="badge badge-info ml-2">{{ argsRules.length }}</span>
          </h3>
          <button @click="addArgsRule" class="btn btn-sm btn-primary">+ 添加</button>
        </div>
        <div class="space-y-2 max-h-[300px] overflow-y-auto">
          <div 
            v-for="rule in argsRules" 
            :key="rule.id"
            class="flex items-center justify-between p-3 bg-dark-card-hover rounded-lg"
          >
            <code class="text-blue-400 text-sm truncate flex-1 mr-2">{{ rule.pattern }}</code>
            <button 
              @click="removeArgsRule(rule)"
              class="text-gray-500 hover:text-red-400 transition-colors shrink-0"
            >
              ×
            </button>
          </div>
          <div v-if="argsRules.length === 0" class="text-gray-500 text-sm text-center py-4">
            暂无 Args 检测规则
          </div>
        </div>
      </div>
      
      <!-- Post 规则 -->
      <div>
        <div class="flex items-center justify-between mb-4">
          <h3 class="text-gray-300 font-medium">
            Post 检测规则
            <span class="badge badge-info ml-2">{{ postRules.length }}</span>
          </h3>
          <button @click="addPostRule" class="btn btn-sm btn-primary">+ 添加</button>
        </div>
        <div class="space-y-2 max-h-[300px] overflow-y-auto">
          <div 
            v-for="rule in postRules" 
            :key="rule.id"
            class="flex items-center justify-between p-3 bg-dark-card-hover rounded-lg"
          >
            <code class="text-blue-400 text-sm truncate flex-1 mr-2">{{ rule.pattern }}</code>
            <button 
              @click="removePostRule(rule)"
              class="text-gray-500 hover:text-red-400 transition-colors shrink-0"
            >
              ×
            </button>
          </div>
          <div v-if="postRules.length === 0" class="text-gray-500 text-sm text-center py-4">
            暂无 Post 检测规则
          </div>
        </div>
      </div>
    </div>
  </Section>
</template>
