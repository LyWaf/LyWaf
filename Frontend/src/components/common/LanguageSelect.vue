<script setup lang="ts">
import { locale, setLocale, t } from '@/i18n'
</script>
<template>
  <select :value="locale" :aria-label="t('common.language')" class="bg-dark-card border border-dark-border rounded px-2 py-1 text-sm text-gray-200 max-w-[120px]" @change="setLocale(($event.target as HTMLSelectElement).value)">
    <option value="zh-CN">简体中文</option>
    <option value="en-US">English</option>
  </select>
</template>
