import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createWebHistory } from 'vue-router'
import LoginPage from '../LoginPage.vue'

vi.mock('../../api/auth', () => ({
  authApi: {
    config: vi.fn().mockResolvedValue({ localLoginEnabled: true, oidcEnabled: false, oidcDisplayName: 'SSO' }),
    me: vi.fn().mockRejectedValue(new Error('not authenticated')),
    login: vi.fn(),
    logout: vi.fn(),
  },
}))

describe('LoginPage', () => {
  let router: ReturnType<typeof createRouter>

  beforeEach(async () => {
    const { authApi } = await import('../../api/auth')
    vi.mocked(authApi.config).mockResolvedValue({ localLoginEnabled: true, oidcEnabled: false, oidcDisplayName: 'SSO' })
    setActivePinia(createPinia())
    router = createRouter({
      history: createWebHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div>Home</div>' } },
        { path: '/login', name: 'login', component: LoginPage, meta: { guest: true } },
      ],
    })
    vi.clearAllMocks()
  })

  it('renders login form', async () => {
    const wrapper = mount(LoginPage, {
      global: { plugins: [router] },
    })
    await flushPromises()
    // Brand is always lowercase per the LIBERTAE redesign.
    expect(wrapper.find('h1').text()).toBe('cosine')
    expect(wrapper.find('input#username').exists()).toBe(true)
    expect(wrapper.find('input#password').exists()).toBe(true)
    expect(wrapper.find('button[type="submit"]').exists()).toBe(true)
  })

  it('calls login on form submit', async () => {
    const { authApi } = await import('../../api/auth')
    vi.mocked(authApi.login).mockResolvedValue({ id: '1', username: 'test', isAdmin: false })

    const wrapper = mount(LoginPage, {
      global: { plugins: [router] },
    })

    await flushPromises()
    await wrapper.find('input#username').setValue('testuser')
    await wrapper.find('input#password').setValue('password123')
    await wrapper.find('form').trigger('submit')

    expect(authApi.login).toHaveBeenCalledWith({ username: 'testuser', password: 'password123' })
  })

  it('shows error message on failed login', async () => {
    const { authApi } = await import('../../api/auth')
    vi.mocked(authApi.login).mockRejectedValue(new Error('fail'))

    const wrapper = mount(LoginPage, {
      global: { plugins: [router] },
    })

    await flushPromises()
    await wrapper.find('input#username').setValue('bad')
    await wrapper.find('input#password').setValue('wrong')
    await wrapper.find('form').trigger('submit')

    await flushPromises()
    expect(wrapper.text()).toContain('An unexpected error occurred')
  })
  it('shows only the provider when local login is disabled and preserves the destination', async () => {
    const { authApi } = await import('../../api/auth')
    vi.mocked(authApi.config).mockResolvedValue({ localLoginEnabled: false, oidcEnabled: true, oidcDisplayName: 'Keycloak' })
    await router.push('/login?redirect=%2Falbums')
    const wrapper = mount(LoginPage, { global: { plugins: [router] } })
    await flushPromises()
    expect(wrapper.find('form').exists()).toBe(false)
    expect(wrapper.find('a').text()).toBe('Sign in with Keycloak')
    expect(wrapper.find('a').attributes('href')).toBe('/auth/oidc/login?returnUrl=%2Falbums')
  })

  it('offers both login methods in mixed mode', async () => {
    const { authApi } = await import('../../api/auth')
    vi.mocked(authApi.config).mockResolvedValue({ localLoginEnabled: true, oidcEnabled: true, oidcDisplayName: 'Keycloak' })
    const wrapper = mount(LoginPage, { global: { plugins: [router] } })
    await flushPromises()
    expect(wrapper.find('form').exists()).toBe(true)
    expect(wrapper.find('a').exists()).toBe(true)
  })

  it('shows a retry action when login configuration cannot be loaded', async () => {
    const { authApi } = await import('../../api/auth')
    vi.mocked(authApi.config).mockRejectedValue(new Error('offline'))
    const wrapper = mount(LoginPage, { global: { plugins: [router] } })
    await flushPromises()
    expect(wrapper.text()).toContain('Could not load sign-in options')
    expect(wrapper.find('form').exists()).toBe(false)
    vi.mocked(authApi.config).mockResolvedValue({ localLoginEnabled: true, oidcEnabled: false, oidcDisplayName: 'SSO' })
    await wrapper.find('button').trigger('click')
    await flushPromises()
    expect(wrapper.find('form').exists()).toBe(true)
  })

  it('rejects an external return destination', async () => {
    const { authApi } = await import('../../api/auth')
    vi.mocked(authApi.config).mockResolvedValue({ localLoginEnabled: true, oidcEnabled: true, oidcDisplayName: 'SSO' })
    await router.push('/login?redirect=%2F%2Fevil.example')
    const wrapper = mount(LoginPage, { global: { plugins: [router] } })
    await flushPromises()
    expect(wrapper.find('a').attributes('href')).toBe('/auth/oidc/login?returnUrl=%2F')
  })

})
