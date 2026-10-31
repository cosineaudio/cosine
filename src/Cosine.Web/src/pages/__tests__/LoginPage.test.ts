import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createWebHistory } from 'vue-router'
import LoginPage from '../LoginPage.vue'

vi.mock('../../api/auth', () => ({
  authApi: {
    me: vi.fn().mockRejectedValue(new Error('not authenticated')),
    login: vi.fn(),
    logout: vi.fn(),
  },
}))

describe('LoginPage', () => {
  let router: ReturnType<typeof createRouter>

  beforeEach(() => {
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

  it('renders login form', () => {
    const wrapper = mount(LoginPage, {
      global: { plugins: [router] },
    })
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

    await wrapper.find('input#username').setValue('bad')
    await wrapper.find('input#password').setValue('wrong')
    await wrapper.find('form').trigger('submit')

    await flushPromises()
    expect(wrapper.text()).toContain('An unexpected error occurred')
  })
})
