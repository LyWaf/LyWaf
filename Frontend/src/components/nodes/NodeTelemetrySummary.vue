<script setup lang="ts">
import { computed } from 'vue'
import { locale, t } from '@/i18n'
import type { NodeTelemetryAggregate } from '@/types'

const props = defineProps<{ telemetry?: NodeTelemetryAggregate | null }>()
const supported = computed(() => props.telemetry?.schema === 'proxy-completions-v1:5xx-or-exception:intercept-marker:ms')
const hasCoverage = computed(() => (props.telemetry?.includedNodes.length ?? 0) > 0)
const missing = computed(() => Object.entries(props.telemetry?.missingNodes ?? {}))
const number = (value: number) => Number.isFinite(value)
  ? `${Number.isSafeInteger(value) ? '' : '~'}${value.toLocaleString(locale.value)}` : '-'
const rate = (value: number | null) => value === null || !Number.isFinite(value) ? '-'
  : new Intl.NumberFormat(locale.value, { style: 'percent', maximumFractionDigits: 2 }).format(value)
const utc = (value: string | null) => value && Number.isFinite(Date.parse(value))
  ? new Date(value).toISOString().replace('T', ' ').replace('.000Z', ' UTC') : '-'
const latency = (value: number | null) => {
  if (!hasCoverage.value || !props.telemetry?.requests) return '-'
  if (value !== null && Number.isFinite(value)) return `~${value.toLocaleString(locale.value)} ms`
  const bounds = props.telemetry.boundsMilliseconds
  const last = bounds[bounds.length - 1]
  return last === undefined ? '-' : `> ${last.toLocaleString(locale.value)} ms`
}
const reasons: Record<string, string> = {
  telemetry_missing: 'nodes.telemetry.reason.missing',
  window_missing_or_duplicate: 'nodes.telemetry.reason.window',
  window_incomplete: 'nodes.telemetry.reason.incomplete',
  invalid_watermark: 'nodes.telemetry.reason.watermark',
  invalid_histogram_or_counts: 'nodes.telemetry.reason.invalid',
  incompatible_histogram: 'nodes.telemetry.reason.incompatible',
  count_overflow: 'nodes.telemetry.reason.overflow',
  clock_skew: 'nodes.telemetry.reason.clock',
  visitor_set_incomplete: 'nodes.telemetry.reason.visitors',
  incompatible_method: 'nodes.telemetry.reason.incompatible',
  invalid_set: 'nodes.telemetry.reason.invalid',
  log_retention_gap: 'nodes.telemetry.reason.retention',
  legacy_decision_id_missing: 'nodes.telemetry.reason.decisionId',
}
</script>

<template>
  <section class="border-y border-dark-border py-4 space-y-3 min-w-0" data-testid="node-telemetry">
    <div class="flex flex-wrap items-baseline justify-between gap-2">
      <h2 class="text-base font-semibold text-gray-200">{{ t('nodes.telemetry.title') }}</h2>
      <span v-if="telemetry && supported" class="text-sm" :class="telemetry.status === 'Complete' ? 'text-emerald-400' : 'text-amber-400'" data-testid="telemetry-coverage">
        {{ t(telemetry.status === 'Complete' ? 'nodes.telemetry.complete' : 'nodes.telemetry.partial', { included: telemetry.includedNodes.length, expected: telemetry.includedNodes.length + missing.length }) }}
      </span>
    </div>
    <p v-if="!telemetry" class="text-sm text-amber-400" data-testid="telemetry-unavailable">{{ t('nodes.telemetry.unavailable') }}</p>
    <p v-else-if="!supported" class="text-sm text-amber-400" data-testid="telemetry-unsupported">{{ t('nodes.telemetry.unsupported') }}</p>
    <template v-else>
      <div class="text-xs text-gray-400 flex flex-wrap gap-x-5 gap-y-1">
        <span data-testid="telemetry-window">{{ utc(telemetry.start) }} / {{ utc(telemetry.end) }}</span>
        <span>{{ t('nodes.telemetry.watermark') }}: {{ utc(telemetry.watermark) }}</span>
      </div>
      <p v-if="telemetry.status !== 'Complete'" class="text-sm text-amber-400">{{ t('nodes.telemetry.coveredOnly') }}</p>
      <dl class="grid grid-cols-2 lg:grid-cols-5 gap-x-5 gap-y-4">
        <div class="min-w-0"><dt class="text-xs text-gray-400">{{ t('nodes.telemetry.requests') }}</dt><dd class="mt-1 text-xl text-gray-100 break-all" data-testid="telemetry-requests">{{ hasCoverage ? number(telemetry.requests) : '-' }}</dd></div>
        <div class="min-w-0"><dt class="text-xs text-gray-400">{{ t('nodes.telemetry.errorRate') }}</dt><dd class="mt-1 text-xl text-gray-100" data-testid="telemetry-error-rate">{{ hasCoverage ? rate(telemetry.errorRate) : '-' }}</dd><dd class="text-xs text-gray-500 break-all" data-testid="telemetry-error-fraction">{{ hasCoverage ? `${number(telemetry.errors)} / ${number(telemetry.requests)}` : '-' }}</dd></div>
        <div class="min-w-0"><dt class="text-xs text-gray-400">{{ t('nodes.telemetry.interceptRate') }}</dt><dd class="mt-1 text-xl text-gray-100" data-testid="telemetry-intercept-rate">{{ hasCoverage ? rate(telemetry.interceptRate) : '-' }}</dd><dd class="text-xs text-gray-500 break-all">{{ hasCoverage ? `${number(telemetry.intercepts)} / ${number(telemetry.requests)}` : '-' }}</dd></div>
        <div class="min-w-0"><dt class="text-xs text-gray-400">{{ t('nodes.telemetry.p95') }}</dt><dd class="mt-1 text-xl text-gray-100 break-words" data-testid="telemetry-p95">{{ latency(telemetry.p95Milliseconds) }}</dd></div>
        <div class="min-w-0"><dt class="text-xs text-gray-400">{{ t('nodes.telemetry.p99') }}</dt><dd class="mt-1 text-xl text-gray-100 break-words" data-testid="telemetry-p99">{{ latency(telemetry.p99Milliseconds) }}</dd></div>
      </dl>
      <div class="grid grid-cols-1 sm:grid-cols-2 gap-5 border-t border-dark-border pt-3">
        <div v-for="[key, metric] in ([['visitors', telemetry.visitors], ['logicalBans', telemetry.logicalBans]] as const)" :key="key" :data-testid="`telemetry-${key}`" class="min-w-0 space-y-1">
          <h3 class="text-sm text-gray-300">{{ t(key === 'visitors' ? 'nodes.telemetry.visitors' : 'nodes.telemetry.logicalBans') }}</h3>
          <div class="text-xl text-gray-100" :data-testid="`telemetry-${key}-count`">{{ metric?.count == null ? '-' : number(metric.count) }}</div>
          <template v-if="metric">
            <div class="text-xs" :class="metric.status === 'Complete' ? 'text-gray-400' : 'text-amber-400'">{{ t(metric.status === 'Complete' ? 'nodes.telemetry.complete' : 'nodes.telemetry.partial', { included: metric.includedNodes.length, expected: metric.includedNodes.length + Object.keys(metric.missingNodes).length }) }}</div>
            <div v-if="metric.count == null" class="text-xs text-amber-400">{{ t('nodes.telemetry.observedCount', { count: number(metric.observedCount) }) }}</div>
            <div v-if="!metric.final" class="text-xs text-amber-400">{{ t('nodes.telemetry.provisional') }}</div>
            <div v-if="metric.observedAt" class="text-xs text-gray-500">{{ t('nodes.telemetry.observedAt') }}: {{ utc(metric.observedAt) }}</div>
            <div class="text-xs text-gray-500 break-all">{{ t('nodes.telemetry.included') }}: {{ metric.includedNodes.join(', ') || '-' }}</div>
            <details v-if="metric.sourceGenerations" class="text-xs text-gray-500"><summary>{{ t('nodes.telemetry.generations') }}</summary><div v-for="(generation, source) in metric.sourceGenerations" :key="source" class="font-mono break-all">{{ source }}: {{ generation }}</div></details>
            <div v-for="(reason, node) in metric.missingNodes" :key="node" class="text-xs text-amber-400 break-words">{{ node }}: {{ t(reasons[reason] ?? 'nodes.telemetry.reason.unknown') }}</div>
          </template>
          <div v-else class="text-xs text-amber-400">{{ t('nodes.telemetry.unavailable') }}</div>
        </div>
      </div>
      <div v-if="missing.length" class="text-sm space-y-1" data-testid="telemetry-missing">
        <div v-for="[node, reason] in missing" :key="node" class="flex flex-wrap gap-x-2 text-amber-400"><span class="font-mono break-all">{{ node }}</span><span>{{ t(reasons[reason] ?? 'nodes.telemetry.reason.unknown') }}</span></div>
      </div>
      <div class="text-xs text-gray-500 space-y-1">
        <div class="break-all">{{ t('nodes.telemetry.included') }}: {{ telemetry.includedNodes.join(', ') || '-' }}</div>
        <div class="break-all">{{ t('nodes.telemetry.setVersion') }}: <span class="font-mono">{{ telemetry.expectedSetVersion }}</span></div>
        <details v-if="telemetry.sourceGenerations"><summary>{{ t('nodes.telemetry.generations') }}</summary><div v-for="(generation, source) in telemetry.sourceGenerations" :key="source" class="font-mono break-all">{{ source }}: {{ generation }}</div></details>
      </div>
    </template>
  </section>
</template>
