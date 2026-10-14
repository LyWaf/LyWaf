import { createApp, watch } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import router from './router'
import './styles/main.css'
import { i18n, locale, translateTitle } from './i18n'

const app = createApp(App)

app.use(createPinia())
app.use(i18n)
app.use(router)
watch([locale, () => router.currentRoute.value.meta.title], () => {
  document.title = `${translateTitle(router.currentRoute.value.meta.title)} - LyWaf`
}, { immediate: true })
app.mount('#app')
