<script setup lang="ts">
import { computed } from 'vue';
import { useRouter } from 'vue-router';
import { Play, ListPlus, ListEnd, User, Heart } from 'lucide-vue-next';
import CoverArt from '@/components/CoverArt.vue';
import PlayButton from '@/components/ui/PlayButton.vue';
import ContextMenu, { type MenuItem } from '@/components/ui/ContextMenu.vue';
import { MoreHorizontal } from 'lucide-vue-next';
import { usePlayback } from '@/composables/usePlayback';
import { usePlaylistMenu } from '@/composables/usePlaylistMenu';
import { useFavoritesStore } from '@/stores/favorites';
import type { Album } from '@/api/types';

const props = defineProps<{ album: Album }>();

const router = useRouter();
const { playAlbum, queueAlbum, albumTracks } = usePlayback();
const { addToPlaylistItem } = usePlaylistMenu();
const favorites = useFavoritesStore();

const isFav = computed(() => favorites.isAlbum(props.album.id));

function open() {
  router.push({ name: 'album', params: { id: props.album.id } });
}

const menuItems = computed<MenuItem[]>(() => [
  { label: 'Play', icon: Play, onSelect: () => playAlbum(props.album.id) },
  { label: 'Play next', icon: ListPlus, onSelect: () => queueAlbum(props.album.id, 'next') },
  { label: 'Add to queue', icon: ListEnd, onSelect: () => queueAlbum(props.album.id, 'end') },
  { separator: true, label: '' },
  {
    label: isFav.value ? 'Remove favorite' : 'Favorite',
    icon: Heart,
    onSelect: () => favorites.toggle('album', props.album.id, props.album.name),
  },
  addToPlaylistItem(() => albumTracks(props.album.id).then((ts) => ts.map((t) => t.id))),
  ...(props.album.artistId
    ? [{ label: 'Go to artist', icon: User, onSelect: () => router.push({ name: 'artist', params: { id: props.album.artistId! } }) } as MenuItem]
    : []),
]);
</script>

<template>
  <ContextMenu :items="menuItems" context-menu>
  <div class="group/card w-full">
    <div class="relative overflow-hidden rounded-control">
      <button type="button" class="block w-full" @click="open" :aria-label="`Open ${album.name}`">
        <CoverArt :cover-art="album.coverArt ?? album.id" :alt="album.name" :size="400" />
      </button>
      <!-- hover play -->
      <div class="pointer-events-none absolute bottom-2 right-2 translate-y-1 opacity-0 transition-all duration-150 group-hover/card:pointer-events-auto group-hover/card:translate-y-0 group-hover/card:opacity-100">
        <PlayButton size="md" @click.stop="playAlbum(album.id)" />
      </div>
      <!-- context menu -->
      <div class="absolute left-2 top-2 opacity-0 transition-opacity duration-150 focus-within:opacity-100 group-hover/card:opacity-100">
        <ContextMenu :items="menuItems">
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
    <button type="button" class="mt-3 block w-full text-left" @click="open">
      <div class="truncate font-medium text-text">{{ album.name }}</div>
      <div class="truncate text-sm text-muted">
        {{ album.artist ?? 'Unknown artist' }}<span v-if="album.year"> · {{ album.year }}</span>
      </div>
    </button>
  </div>
  </ContextMenu>
</template>
