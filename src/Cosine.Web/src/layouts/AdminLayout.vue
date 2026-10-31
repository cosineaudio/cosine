<script setup lang="ts">
import { useRoute } from 'vue-router'
import { LayoutDashboard, Users, Settings, ScanLine, Speaker, ArrowLeft } from 'lucide-vue-next'
import type { Component } from 'vue'
import NavProgress from '@/components/ui/NavProgress.vue'

const route = useRoute()

const navItems: { name: string; label: string; path: string; icon: Component }[] = [
  { name: 'admin-dashboard', label: 'Dashboard', path: '/admin', icon: LayoutDashboard },
  { name: 'admin-users', label: 'Users', path: '/admin/users', icon: Users },
  { name: 'admin-settings', label: 'Settings', path: '/admin/settings', icon: Settings },
  { name: 'admin-scanner', label: 'Scanner', path: '/admin/scanner', icon: ScanLine },
  { name: 'admin-devices', label: 'Playback devices', path: '/admin/devices', icon: Speaker },
]

function isActive(name: string): boolean {
  return route.name === name
}
</script>

<template>
  <div class="flex h-viewport bg-bg text-text">
    <aside class="flex w-60 shrink-0 flex-col border-r border-border bg-surface">
      <div class="flex items-center gap-2 border-b border-border px-5 py-4">
        <span class="font-display text-lg font-bold lowercase tracking-tight text-text">cosine</span>
        <span class="label">Admin</span>
      </div>
      <nav class="flex-1 space-y-1 p-3">
        <router-link
          v-for="item in navItems"
          :key="item.name"
          :to="item.path"
          :class="[
            'flex items-center gap-3 rounded-control px-3 py-2 text-sm transition-colors',
            isActive(item.name)
              ? 'bg-surface-2 font-medium text-text'
              : 'text-muted hover:bg-surface-2/60 hover:text-text',
          ]"
        >
          <component :is="item.icon" :size="17" :stroke-width="2" />
          {{ item.label }}
        </router-link>
      </nav>
      <div class="border-t border-border p-3">
        <router-link
          to="/"
          class="flex items-center gap-2 rounded-control px-3 py-2 text-sm text-muted transition-colors hover:bg-surface-2/60 hover:text-text"
        >
          <ArrowLeft :size="16" :stroke-width="2" /> Back to app
        </router-link>
      </div>
    </aside>

    <div class="relative flex min-w-0 flex-1 flex-col">
      <NavProgress edge="top" />
      <main class="flex-1 overflow-y-auto">
        <router-view />
      </main>
    </div>
  </div>
</template>
