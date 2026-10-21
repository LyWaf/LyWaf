export function isHealthyActive(node: {
  online: boolean
  stale: boolean
  lifecycle?: string
  clockSkewWarning?: boolean
  admissionState?: string
  admissionEnabled?: boolean
  healthyActive?: boolean
}) {
  if (node.admissionEnabled === false || (node.admissionState !== undefined
    && node.admissionState !== 'Active' && node.admissionState !== 'Legacy')) return false
  return node.online && !node.stale && !node.clockSkewWarning
    && (node.lifecycle === undefined || node.lifecycle === 'Active')
    && node.healthyActive !== false
}
