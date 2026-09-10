import { ref, computed } from 'vue'

const TOKEN_KEY = 'lywaf_token'
const USERNAME_KEY = 'lywaf_username'
const ROLE_KEY = 'lywaf_role'
const PERMISSIONS_KEY = 'lywaf_permissions'

const token = ref<string | null>(localStorage.getItem(TOKEN_KEY))
const username = ref<string | null>(localStorage.getItem(USERNAME_KEY))
const role = ref<string | null>(localStorage.getItem(ROLE_KEY))
const permissions = ref<string[]>(JSON.parse(localStorage.getItem(PERMISSIONS_KEY) || '[]'))

export function useAuth() {
  const isAuthenticated = computed(() => !!token.value)

  const setAuth = (newToken: string, newUsername: string, newRole = 'auditor', newPermissions: string[] = ['read']) => {
    token.value = newToken
    username.value = newUsername
    role.value = newRole
    permissions.value = newPermissions
    localStorage.setItem(TOKEN_KEY, newToken)
    localStorage.setItem(USERNAME_KEY, newUsername)
    localStorage.setItem(ROLE_KEY, newRole)
    localStorage.setItem(PERMISSIONS_KEY, JSON.stringify(newPermissions))
  }

  const logout = () => {
    token.value = null
    username.value = null
    role.value = null
    permissions.value = []
    localStorage.removeItem(TOKEN_KEY)
    localStorage.removeItem(USERNAME_KEY)
    localStorage.removeItem(ROLE_KEY)
    localStorage.removeItem(PERMISSIONS_KEY)
  }

  const getToken = () => token.value
  const can = (permission: string) => permissions.value.includes(permission)

  return { token, username, role, permissions, isAuthenticated, setAuth, logout, getToken, can }
}
