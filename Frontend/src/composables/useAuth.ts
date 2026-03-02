import { ref, computed } from 'vue'

const TOKEN_KEY = 'lywaf_token'
const USERNAME_KEY = 'lywaf_username'

const token = ref<string | null>(localStorage.getItem(TOKEN_KEY))
const username = ref<string | null>(localStorage.getItem(USERNAME_KEY))

export function useAuth() {
  const isAuthenticated = computed(() => !!token.value)

  const setAuth = (newToken: string, newUsername: string) => {
    token.value = newToken
    username.value = newUsername
    localStorage.setItem(TOKEN_KEY, newToken)
    localStorage.setItem(USERNAME_KEY, newUsername)
  }

  const logout = () => {
    token.value = null
    username.value = null
    localStorage.removeItem(TOKEN_KEY)
    localStorage.removeItem(USERNAME_KEY)
  }

  const getToken = () => token.value

  return { token, username, isAuthenticated, setAuth, logout, getToken }
}
