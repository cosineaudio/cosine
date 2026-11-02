import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { startNavigation, endNavigation } from '../composables/useNavProgress'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/login',
      name: 'login',
      component: () => import('../pages/LoginPage.vue'),
      meta: { guest: true },
    },
    {
      // The listen-together share link. `public`: reachable with no account at all - the page
      // authenticates with its own party ticket, never the cookie.
      path: '/party/:token',
      name: 'party',
      component: () => import('../pages/PartyPage.vue'),
      meta: { public: true },
    },
    {
      path: '/',
      component: () => import('../layouts/DefaultLayout.vue'),
      children: [
        { path: '', name: 'home', component: () => import('../pages/HomePage.vue') },
        { path: 'search', name: 'search', component: () => import('../pages/SearchPage.vue') },
        { path: 'albums', name: 'albums', component: () => import('../pages/AlbumsPage.vue') },
        { path: 'artists', name: 'artists', component: () => import('../pages/ArtistListPage.vue') },
        { path: 'artist/:id', name: 'artist', component: () => import('../pages/ArtistDetailPage.vue'), props: true },
        { path: 'album/:id', name: 'album', component: () => import('../pages/AlbumDetailPage.vue'), props: true },
        { path: 'genres', name: 'genres', component: () => import('../pages/GenresPage.vue') },
        { path: 'genre/:name', name: 'genre', component: () => import('../pages/GenreDetailPage.vue'), props: true },
        { path: 'favorites', name: 'favorites', component: () => import('../pages/FavoritesPage.vue') },
        { path: 'playlists', name: 'playlists', component: () => import('../pages/PlaylistsPage.vue') },
        { path: 'playlist/:id', name: 'playlist', component: () => import('../pages/PlaylistDetailPage.vue'), props: true },
        { path: 'insights', name: 'insights', component: () => import('../pages/InsightsPage.vue') },
        { path: 'history', name: 'history', component: () => import('../pages/HistoryPage.vue') },
        { path: 'profile', name: 'profile', component: () => import('../pages/ProfilePage.vue') },
        { path: 'radio', name: 'radio', component: () => import('../pages/RadioPage.vue') },
      ],
    },
    {
      path: '/admin',
      component: () => import('../layouts/AdminLayout.vue'),
      meta: { admin: true },
      children: [
        { path: '', name: 'admin-dashboard', component: () => import('../pages/admin/AdminDashboardPage.vue') },
        { path: 'users', name: 'admin-users', component: () => import('../pages/admin/AdminUsersPage.vue') },
        { path: 'settings', name: 'admin-settings', component: () => import('../pages/admin/AdminSettingsPage.vue') },
        { path: 'library-health', name: 'admin-library-health', component: () => import('../pages/admin/AdminLibraryHealthPage.vue') },
        { path: 'scanner', name: 'admin-scanner', component: () => import('../pages/admin/AdminScannerPage.vue') },
        { path: 'devices', name: 'admin-devices', component: () => import('../pages/admin/AdminPlaybackDevicesPage.vue') },
      ],
    },
  ],
})

router.beforeEach(async (to, _from, next) => {
  const auth = useAuthStore()
  startNavigation()

  // Public pages skip the session probe entirely: a guest with only a party link has no
  // cookie, and the probe's 401 would be pure noise on the way to a page that never needs it.
  if (to.meta.public) {
    return next()
  }

  if (!auth.initialized) {
    await auth.fetchUser()
  }

  if (to.meta.guest && auth.isAuthenticated) {
    return next({ name: 'home' })
  }

  if (!to.meta.guest && !auth.isAuthenticated) {
    return next({ name: 'login', query: { redirect: to.fullPath } })
  }

  if (to.meta.admin && !auth.isAdmin) {
    return next({ name: 'home' })
  }

  next()
})

// `afterEach` also fires for cancelled and redirected navigations, so the bar
// cannot be left running by a guard that never reaches its destination.
router.afterEach(endNavigation)
router.onError(endNavigation)

export default router
