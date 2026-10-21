<script setup lang="ts">
import type { ManagedNodeSnapshot } from '@/types'
import { locale, t } from '@/i18n'
defineProps<{ nodes: ManagedNodeSnapshot[] }>()
const number = (value?: number | null) => value == null ? '-' : value.toLocaleString(locale.value, { maximumFractionDigits: 1 })
const time = (value?: string | null) => value ? new Date(value).toLocaleString(locale.value) : '-'
const eventNames: Record<string, string> = {
  retry_query: 'nodes.communication.retry', idempotency_hit: 'nodes.communication.idempotency',
  signature_rejected: 'nodes.communication.signature', replay_rejected: 'nodes.communication.replay', outcome_unknown: 'nodes.communication.unknown',
}
const states: Record<string, string> = {
  Ready: 'nodes.communication.ready', NeedsReconciliation: 'nodes.communication.needsReconciliation',
  IdentityConflict: 'nodes.communication.identityConflict',
  node_identity: 'nodes.communication.identityBoot', process_telemetry: 'nodes.communication.processBoot',
  process_telemetry_identity_unavailable: 'nodes.communication.identityUnavailable',
}
const state = (value?: string | null) => value ? t(states[value] ?? 'nodes.communication.unknownState') : '-'
</script>

<template>
  <section class="space-y-4" data-testid="node-communication">
    <div v-for="node in nodes" :key="node.nodeId" class="border-b border-dark-border pb-4 space-y-3 min-w-0">
      <div class="flex flex-wrap items-baseline justify-between gap-2">
        <h2 class="text-base font-semibold text-gray-200">{{ node.nodeId }}</h2>
        <span class="text-xs text-gray-400">{{ t(node.online && !node.stale ? 'nodes.communication.current' : 'nodes.communication.cached') }}: {{ time(node.snapshot?.timestamp) }}</span>
      </div>
      <dl class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-3 text-xs">
        <div class="min-w-0"><dt class="text-gray-500">BootId</dt><dd class="font-mono text-gray-300 break-all">{{ node.snapshot?.bootId || '-' }}</dd><dd class="text-gray-500 break-all">{{ state(node.snapshot?.bootSource) }}</dd></div>
        <div class="min-w-0"><dt class="text-gray-500">{{ t('nodes.communication.handshake') }}</dt><dd class="text-gray-300">{{ time(node.diagnostics?.lastHandshakeAt) }}</dd><dd class="font-mono break-all text-gray-500">{{ node.diagnostics?.bootId || '-' }}</dd></div>
        <div><dt class="text-gray-500">{{ t('nodes.communication.reconciliation') }}</dt><dd class="text-amber-400">{{ state(node.diagnostics?.reconciliationState) }}</dd></div>
        <div><dt class="text-gray-500">{{ t('nodes.communication.pendingUnknown') }}</dt><dd class="text-gray-300">{{ number(node.diagnostics?.pendingCommands) }} / {{ number(node.diagnostics?.unknownCommands) }}</dd></div>
        <div class="min-w-0"><dt class="text-gray-500">{{ t('nodes.communication.expected') }}</dt><dd class="font-mono break-all text-gray-300">{{ node.diagnostics?.expectedSourceVersion || '-' }}</dd></div>
        <div class="min-w-0"><dt class="text-gray-500">{{ t('nodes.communication.acknowledged') }}</dt><dd class="font-mono break-all text-gray-300">{{ node.diagnostics?.acknowledgedAppliedVersion || '-' }}</dd></div>
        <div class="min-w-0"><dt class="text-gray-500">{{ t('nodes.communication.actual') }}</dt><dd class="font-mono break-all text-gray-300">{{ node.diagnostics?.actualAppliedVersion || node.snapshot?.configVersionId || '-' }}</dd><dd class="font-mono break-all text-gray-500">{{ node.diagnostics?.actualFileVersion || '-' }}</dd></div>
        <div><dt class="text-gray-500">{{ t('nodes.communication.retryAge') }}</dt><dd class="text-gray-300">{{ number(node.diagnostics?.retryQueries) }} / {{ number(node.diagnostics?.oldestPendingMilliseconds) }} ms</dd></div>
      </dl>
      <div v-if="node.diagnostics?.error" class="text-xs text-amber-400">{{ t('nodes.communication.diagnosticError') }}</div>
      <template v-if="node.snapshot?.diagnostics">
        <div v-if="node.snapshot.diagnostics.replicationQueue" class="flex flex-wrap gap-x-6 gap-y-2 text-sm text-gray-300" data-testid="replication-queue">
          <span>{{ t('nodes.communication.queue') }}: {{ number(node.snapshot.diagnostics.replicationQueue.entries) }} / {{ number(node.snapshot.diagnostics.replicationQueue.bytes) }} B</span>
          <span>{{ t('nodes.communication.dropped') }}: {{ number(node.snapshot.diagnostics.replicationQueue.capacityDropped + node.snapshot.diagnostics.replicationQueue.oversizeDropped) }}</span>
          <span>{{ t('nodes.communication.coalesced') }}: {{ number(node.snapshot.diagnostics.replicationQueue.coalesced) }}</span>
          <span>{{ t('nodes.communication.closedDropped') }}: {{ number(node.snapshot.diagnostics.replicationQueue.closedDropped) }}</span>
          <span>{{ t('nodes.communication.contentionBuffered') }}: {{ number(node.snapshot.diagnostics.replicationQueue.contentionBuffered) }}</span>
          <span>{{ t('nodes.communication.authorization') }}: {{ number(node.snapshot.diagnostics.replicationQueue.authorizationRejected) }}</span>
        </div>
        <div class="text-xs text-gray-500">{{ t('nodes.communication.period') }}: {{ time(node.snapshot.diagnostics.communication.startedAt) }} / {{ time(node.snapshot.diagnostics.communication.capturedAt) }}</div>
        <div class="flex flex-wrap gap-x-5 gap-y-2 text-xs text-gray-400" data-testid="communication-events">
          <span v-for="(label, name) in eventNames" :key="name">{{ t(label) }}: {{ number(node.snapshot.diagnostics.communication.events[name] ?? 0) }}</span>
        </div>
        <div v-if="node.snapshot.diagnostics.communication.replicationAcknowledgement" class="text-xs text-gray-400" data-testid="ack-delay">
          {{ t('nodes.communication.replicationDelay') }}:
          {{ number(node.snapshot.diagnostics.communication.replicationAcknowledgement.samples ? node.snapshot.diagnostics.communication.replicationAcknowledgement.totalMilliseconds / node.snapshot.diagnostics.communication.replicationAcknowledgement.samples : null) }} ms
          / {{ number(node.snapshot.diagnostics.communication.replicationAcknowledgement.samples) }}
          <span class="ml-3">{{ t('nodes.communication.ackMax') }}: {{ number(node.snapshot.diagnostics.communication.replicationAcknowledgement.samples ? node.snapshot.diagnostics.communication.replicationAcknowledgement.maxMilliseconds : null) }} ms</span>
          <span class="ml-3" data-testid="ack-skew">{{ t('nodes.communication.ackSkew') }}: {{ number(node.snapshot.diagnostics.communication.replicationAcknowledgement.clockSkewSamples) }}</span>
          <div class="mt-1" data-testid="ack-scope">{{ t('nodes.communication.ackScope') }}</div>
        </div>
        <div v-if="!node.snapshot.diagnostics.communication.replicationAcknowledgement?.samples" class="text-xs text-amber-400" data-testid="ack-unsampled">{{ t('nodes.communication.ackUnsampled') }}</div>
        <div class="overflow-x-auto">
          <table class="w-full min-w-[680px] text-xs text-left">
            <thead class="text-gray-500"><tr><th class="py-2">{{ t('nodes.communication.operation') }}</th><th>{{ t('nodes.communication.direction') }}</th><th>{{ t('nodes.communication.requestsFailed') }}</th><th>{{ t('nodes.communication.duration') }}</th><th>{{ t('nodes.communication.bytes') }}</th><th>{{ t('nodes.communication.unknownSizes') }}</th></tr></thead>
            <tbody class="divide-y divide-dark-border text-gray-300"><tr v-for="series in node.snapshot.diagnostics.communication.series" :key="`${series.direction}:${series.operation}`"><td class="py-2 font-mono">{{ series.operation }}</td><td>{{ t(series.direction === 'inbound' ? 'nodes.communication.inbound' : 'nodes.communication.outbound') }}</td><td>{{ number(series.requests) }} / {{ number(series.failed) }}</td><td>{{ number(series.requests ? series.totalMilliseconds / series.requests : null) }}</td><td>{{ number(series.requestBytes) }} / {{ number(series.responseBytes) }}</td><td>{{ number(series.unknownSizes) }}</td></tr></tbody>
          </table>
        </div>
      </template>
      <p v-else class="text-sm text-amber-400">{{ t('nodes.communication.unavailable') }}</p>
    </div>
  </section>
</template>
