import { ref } from 'vue'

export const stepUpOpen = ref(false)
let pending: Promise<string> | null = null
let resolvePending: ((proof: string) => void) | undefined
let rejectPending: ((error: Error) => void) | undefined

export function requestStepUp(): Promise<string> {
  if (pending) return pending
  stepUpOpen.value = true
  pending = new Promise<string>((resolve, reject) => { resolvePending = resolve; rejectPending = reject })
  return pending
}

export function finishStepUp(proof?: string) {
  if (proof) resolvePending?.(proof)
  else rejectPending?.(new Error('已取消身份验证'))
  stepUpOpen.value = false
  pending = null
  resolvePending = undefined
  rejectPending = undefined
}
