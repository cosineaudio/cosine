<script setup lang="ts">
import { Home, Search, Library, Disc3 } from 'lucide-vue-next';
import { useRoute } from 'vue-router';
import { useUiStore } from '@/stores/ui';
import { usePlaybackStore } from '@/stores/playback';

const route = useRoute();
const ui = useUiStore();
const player = usePlaybackStore();

const tabs = [
  { name: 'home', label: 'Home', icon: Home },
  { name: 'search', label: 'Search', icon: Search },
  { name: 'favorites', label: 'Library', icon: Library },
] as const;
</script>

<template>
  <!-- data attr: style.css offsets toasts above this bar via :has() -->
  <nav data-mobile-tab-bar class="flex items-stretch border-t border-border bg-surface">
    <RouterLink
      v-for="tab in tabs"
      :key="tab.name"
      :to="{ name: tab.name }"
      class="flex flex-1 flex-col items-center gap-1 py-2 text-[10px] font-medium uppercase tracking-wider transition-colors"
      :class="route.name === tab.name ? 'text-text' : 'text-muted'"
    >
      <component :is="tab.icon" :size="21" :stroke-width="1.75" />
      {{ tab.label }}
    </RouterLink>
    <button
      type="button"
      class="flex flex-1 flex-col items-center gap-1 py-2 text-[10px] font-medium uppercase tracking-wider transition-colors"
      :class="ui.fullScreenOpen ? 'text-text' : 'text-muted'"
      :disabled="!player.hasTrack"
      @click="ui.openFullScreen()"
    >
      <Disc3 :size="21" :stroke-width="1.75" :class="{ 'animate-spin [animation-duration:6s]': player.isPlaying }" />
      Playing
    </button>
  </nav>
</template>
