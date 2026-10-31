<script setup lang="ts">
import { computed, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { storeToRefs } from 'pinia';
import { ListMusic, Plus, Play, ListEnd, Trash2, Radio, MoreHorizontal } from 'lucide-vue-next';
import { usePlaylistsStore } from '@/stores/playlists';
import { useUiStore } from '@/stores/ui';
import { usePlayback } from '@/composables/usePlayback';
import { getPlaylist, isSystemPlaylist, type Playlist } from '@/api/playlists';
import CoverArt from '@/components/CoverArt.vue';
import PlayButton from '@/components/ui/PlayButton.vue';
import ContextMenu, { type MenuItem } from '@/components/ui/ContextMenu.vue';
import EmptyState from '@/components/ui/EmptyState.vue';
import SkeletonGrid from '@/components/ui/SkeletonGrid.vue';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import { countLabel } from '@/lib/format';

const router = useRouter();
const playlists = usePlaylistsStore();
const ui = useUiStore();
const { playTracks, queueTracks } = usePlayback();
const { items, loaded } = storeToRefs(playlists);

const showSkeleton = useDeferredLoading(computed(() => !loaded.value));

// Pin system playlists (Release Radar) to the front.
const sortedItems = computed(() =>
  [...items.value].sort((a, b) => Number(isSystemPlaylist(b)) - Number(isSystemPlaylist(a))),
);

function open(pl: Playlist) {
  router.push({ name: 'playlist', params: { id: pl.id } });
}

async function playPlaylist(id: string) {
  try {
    const detail = await getPlaylist(id);
    playTracks(detail.entry, 0);
  } catch { /* toast handled elsewhere */ }
}

async function queuePlaylist(id: string) {
  try {
    const detail = await getPlaylist(id);
    queueTracks(detail.entry, 'end');
  } catch { /* ignore */ }
}

function menuFor(pl: Playlist): MenuItem[] {
  const base: MenuItem[] = [
    { label: 'Play', icon: Play, onSelect: () => playPlaylist(pl.id) },
    { label: 'Add to queue', icon: ListEnd, onSelect: () => queuePlaylist(pl.id) },
  ];
  // System playlists (Release Radar) are job-managed and can't be deleted.
  if (isSystemPlaylist(pl)) return base;
  return [
    ...base,
    { separator: true, label: '' },
    { label: 'Delete', icon: Trash2, danger: true, onSelect: () => playlists.remove(pl.id, pl.name) },
  ];
}

onMounted(() => playlists.load());
</script>

<template>
  <div class="mx-auto max-w-screen-2xl px-6 pb-16 pt-8">
    <header class="mb-6 flex items-end justify-between gap-4">
      <h1 class="font-display font-bold tracking-tight text-text" :style="{ fontSize: 'var(--text-display-l)', lineHeight: '1' }">
        Playlists
      </h1>
      <button
        type="button"
        class="flex items-center gap-2 rounded-control bg-accent px-4 py-2 text-sm font-medium text-accent-ink transition-transform hover:scale-[1.02] active:scale-95"
        @click="ui.openCreatePlaylist()"
      >
        <Plus :size="16" :stroke-width="2.5" /> New playlist
      </button>
    </header>

    <div
      v-if="items.length"
      class="grid grid-cols-2 gap-x-4 gap-y-6 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6"
    >
      <div v-for="pl in sortedItems" :key="pl.id" class="w-full">
        <ContextMenu :items="menuFor(pl)" context-menu>
        <div class="group/card w-full">
        <div class="relative overflow-hidden rounded-control">
          <button type="button" class="block w-full" :aria-label="`Open ${pl.name}`" @click="open(pl)">
            <CoverArt :cover-art="pl.coverArt ?? pl.id" :alt="pl.name" :size="400" />
          </button>
          <div
            v-if="isSystemPlaylist(pl)"
            class="pointer-events-none absolute bottom-2 left-2 flex items-center gap-1 rounded-full bg-accent/90 px-2 py-0.5 text-[11px] font-medium text-accent-ink backdrop-blur"
          >
            <Radio :size="12" :stroke-width="2.5" /> Auto
          </div>
          <div class="pointer-events-none absolute bottom-2 right-2 translate-y-1 opacity-0 transition-all duration-150 group-hover/card:pointer-events-auto group-hover/card:translate-y-0 group-hover/card:opacity-100">
            <PlayButton size="md" @click.stop="playPlaylist(pl.id)" />
          </div>
          <div class="absolute left-2 top-2 opacity-0 transition-opacity duration-150 focus-within:opacity-100 group-hover/card:opacity-100">
            <ContextMenu :items="menuFor(pl)">
              <button
                type="button"
                class="grid h-8 w-8 place-items-center rounded-full bg-bg/70 text-text backdrop-blur transition-colors hover:bg-bg"
                aria-label="More options"
              >
                <MoreHorizontal :size="18" :stroke-width="2" />
              </button>
            </ContextMenu>
          </div>
        </div>
        <button type="button" class="mt-3 block w-full text-left" @click="open(pl)">
          <div class="truncate font-medium text-text">{{ pl.name }}</div>
          <div class="truncate text-sm text-muted">{{ countLabel(pl.songCount, 'track') }}</div>
        </button>
        </div>
        </ContextMenu>
      </div>
    </div>

    <SkeletonGrid
      v-else-if="showSkeleton"
      class="grid grid-cols-2 gap-x-4 gap-y-6 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6"
      :count="12"
    />

    <EmptyState
      v-else-if="loaded"
      :icon="ListMusic"
      title="No playlists yet"
      description="Create your first playlist to start collecting tracks."
    >
      <button
        type="button"
        class="flex items-center gap-2 rounded-control bg-accent px-4 py-2 text-sm font-medium text-accent-ink transition-transform hover:scale-[1.02] active:scale-95"
        @click="ui.openCreatePlaylist()"
      >
        <Plus :size="16" :stroke-width="2.5" /> New playlist
      </button>
    </EmptyState>
  </div>
</template>
