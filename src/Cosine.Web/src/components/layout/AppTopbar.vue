<script setup lang="ts">
import { ref } from 'vue';
import { useRouter } from 'vue-router';
import { Search, LogOut, User as UserIcon, Command } from 'lucide-vue-next';
import { useAuthStore } from '@/stores/auth';
import { useUiStore } from '@/stores/ui';
import ContextMenu, { type MenuItem } from '@/components/ui/ContextMenu.vue';
import NavProgress from '@/components/ui/NavProgress.vue';

const router = useRouter();
const auth = useAuthStore();
const ui = useUiStore();

const query = ref('');

function submit() {
  const q = query.value.trim();
  if (!q) return;
  router.push({ name: 'search', query: { q } });
}

const initials = computedInitials();
function computedInitials() {
  const name = auth.user?.username ?? '';
  return name.slice(0, 2).toUpperCase();
}

const menuItems: MenuItem[] = [
  {
    label: auth.user?.username ?? 'Account',
    icon: UserIcon,
    onSelect: () => {
      router.push({ name: 'profile' });
    },
  },
  { separator: true, label: '' },
  {
    label: 'Sign out',
    icon: LogOut,
    onSelect: async () => {
      await auth.logout();
      router.push({ name: 'login' });
    },
  },
];
</script>

<template>
  <header class="relative flex h-16 shrink-0 items-center gap-3 border-b border-border bg-bg/80 px-4 backdrop-blur sm:px-6">
    <!-- route/chunk loading hairline, riding the header's bottom border -->
    <NavProgress />

    <!-- mobile wordmark -->
    <RouterLink :to="{ name: 'home' }" class="font-display text-xl font-bold lowercase tracking-tight text-text lg:hidden">
      cosine
    </RouterLink>

    <!-- search -->
    <div class="relative hidden max-w-md flex-1 sm:block">
      <Search :size="17" :stroke-width="2" class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-muted" />
      <input
        id="global-search"
        v-model="query"
        type="search"
        placeholder="Search artists, albums, songs…"
        class="w-full rounded-control border border-border bg-surface py-2 pl-9 pr-20 text-sm text-text placeholder:text-faint focus:border-transparent"
        @keyup.enter="submit"
      />
      <button
        type="button"
        class="absolute right-2 top-1/2 hidden -translate-y-1/2 items-center gap-1 rounded-[3px] border border-border px-1.5 py-1 text-[10px] font-medium text-faint transition-colors hover:text-muted md:flex"
        aria-label="Open command palette"
        @click="ui.toggleCommand()"
      >
        <Command :size="11" :stroke-width="2" />K
      </button>
    </div>

    <div class="flex-1 sm:hidden" />

    <!-- mobile search icon -->
    <button
      type="button"
      class="grid h-9 w-9 place-items-center rounded-control text-muted hover:bg-surface-2 hover:text-text sm:hidden"
      aria-label="Search"
      @click="router.push({ name: 'search' })"
    >
      <Search :size="20" :stroke-width="2" />
    </button>

    <!-- user menu -->
    <ContextMenu :items="menuItems">
      <button
        type="button"
        class="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-surface-2 font-mono text-xs font-semibold text-text ring-1 ring-border transition-colors hover:ring-faint"
        aria-label="Account menu"
      >
        {{ initials }}
      </button>
    </ContextMenu>
  </header>
</template>
