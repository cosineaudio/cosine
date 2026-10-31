import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import router from './router'
import { setUnauthorizedHandler } from './api/client'
import { useAuthStore } from './stores/auth'
// vue-sonner first: style.css re-dresses its toasts and needs to win the cascade
import 'vue-sonner/style.css'
import './style.css'

const app = createApp(App)
app.use(createPinia())
app.use(router)

// When the auth cookie expires mid-session every in-flight request starts failing, and the UI
// would otherwise sit on a page that can no longer load anything until the user navigates.
// Drop the local session and send them to login, remembering where they were.
setUnauthorizedHandler(() => {
  const auth = useAuthStore()
  // A rejected login attempt is a 401 too - it must not bounce the login page onto itself.
  if (!auth.isAuthenticated) return
  auth.clearSession()
  const from = router.currentRoute.value.fullPath
  router.replace({ name: 'login', query: from === '/' ? {} : { redirect: from } })
})

app.mount('#app')
