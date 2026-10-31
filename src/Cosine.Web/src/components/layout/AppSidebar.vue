<script setup lang="ts">
import { computed } from 'vue';
import {
  Home, Search, Disc3, Mic2, Tags,
  Heart, ListMusic, Clock, Radio,
  ListVideo, PanelLeftClose, PanelLeft, SlidersHorizontal,
} from 'lucide-vue-next';
import { useAuthStore } from '@/stores/auth';
import { useUiStore } from '@/stores/ui';
import { usePlaylistsStore } from '@/stores/playlists';
import { storeToRefs } from 'pinia';

const auth = useAuthStore();
const ui = useUiStore();
const playlists = usePlaylistsStore();
const { collapsed } = defineProps<{ collapsed?: boolean }>();

const primary = [
  { name: 'home', label: 'Home', icon: Home },
  { name: 'search', label: 'Search', icon: Search },
  { name: 'albums', label: 'Albums', icon: Disc3 },
  { name: 'artists', label: 'Artists', icon: Mic2 },
  { name: 'genres', label: 'Genres', icon: Tags },
];

const library = [
  { name: 'favorites', label: 'Favorites', icon: Heart },
  { name: 'playlists', label: 'Playlists', icon: ListMusic },
  { name: 'history', label: 'History', icon: Clock },
  { name: 'radio', label: 'Radio', icon: Radio },
];

const { items: pinnedPlaylists } = storeToRefs(playlists);
const pinned = computed(() => pinnedPlaylists.value.slice(0, 8));
</script>

<template>
  <aside
    class="flex h-full flex-col border-r border-border bg-surface transition-[width] duration-200"
    :class="collapsed ? 'w-[68px]' : 'w-64'"
  >
    <!-- wordmark + collapse -->
    <div class="flex items-center justify-between px-4 py-5" :class="collapsed ? 'justify-center' : ''">
      <RouterLink v-if="!collapsed" :to="{ name: 'home' }" class="font-display text-2xl font-bold lowercase tracking-tight text-text">
        cosine
      </RouterLink>
      <button
        type="button"
        class="grid h-8 w-8 place-items-center rounded-control text-muted transition-colors hover:bg-surface-2 hover:text-text"
        :aria-label="collapsed ? 'Expand sidebar' : 'Collapse sidebar'"
        @click="ui.toggleSidebar()"
      >
        <PanelLeft v-if="collapsed" :size="18" :stroke-width="1.75" />
        <PanelLeftClose v-else :size="18" :stroke-width="1.75" />
      </button>
    </div>

    <nav class="flex-1 overflow-y-auto px-3 pb-4">
      <ul class="space-y-0.5">
        <li v-for="item in primary" :key="item.name">
          <RouterLink
            :to="{ name: item.name }"
            class="group flex items-center gap-3 rounded-control px-3 py-2 text-sm font-medium text-muted transition-colors hover:bg-surface-2 hover:text-text"
            active-class="!bg-surface-2 !text-text"
            :title="collapsed ? item.label : undefined"
          >
            <component :is="item.icon" :size="19" :stroke-width="1.75" class="shrink-0" />
            <span v-if="!collapsed" class="truncate">{{ item.label }}</span>
          </RouterLink>
        </li>
      </ul>

      <div class="my-4 border-t border-border" />
      <div v-if="!collapsed" class="label px-3 pb-2">Library</div>
      <ul class="space-y-0.5">
        <li v-for="item in library" :key="item.name">
          <RouterLink
            :to="{ name: item.name }"
            class="group flex items-center gap-3 rounded-control px-3 py-2 text-sm font-medium text-muted transition-colors hover:bg-surface-2 hover:text-text"
            active-class="!bg-surface-2 !text-text"
            :title="collapsed ? item.label : undefined"
          >
            <component :is="item.icon" :size="19" :stroke-width="1.75" class="shrink-0" />
            <span v-if="!collapsed" class="truncate">{{ item.label }}</span>
          </RouterLink>
        </li>
      </ul>

      <!-- pinned playlists -->
      <template v-if="!collapsed && pinned.length">
        <div class="my-4 border-t border-border" />
        <div class="label px-3 pb-2">Playlists</div>
        <ul class="space-y-0.5">
          <li v-for="pl in pinned" :key="pl.id">
            <RouterLink
              :to="{ name: 'playlist', params: { id: pl.id } }"
              class="block truncate rounded-control px-3 py-1.5 text-sm text-muted transition-colors hover:bg-surface-2 hover:text-text"
              active-class="!text-text"
            >
              {{ pl.name }}
            </RouterLink>
          </li>
        </ul>
      </template>
    </nav>

    <!-- queue toggle + admin -->
    <div class="border-t border-border p-3">
      <button
        type="button"
        class="flex w-full items-center gap-3 rounded-control px-3 py-2 text-sm font-medium text-muted transition-colors hover:bg-surface-2 hover:text-text"
        :class="ui.queueOpen ? '!text-text' : ''"
        :title="collapsed ? 'Queue' : undefined"
        @click="ui.toggleQueue()"
      >
        <ListVideo :size="19" :stroke-width="1.75" class="shrink-0" />
        <span v-if="!collapsed">Queue</span>
      </button>
      <RouterLink
        v-if="auth.isAdmin"
        to="/admin"
        class="flex w-full items-center gap-3 rounded-control px-3 py-2 text-sm font-medium text-muted transition-colors hover:bg-surface-2 hover:text-text"
        :title="collapsed ? 'Admin' : undefined"
      >
        <SlidersHorizontal :size="19" :stroke-width="1.75" class="shrink-0" />
        <span v-if="!collapsed">Admin</span>
      </RouterLink>
    </div>
  </aside>
</template>
