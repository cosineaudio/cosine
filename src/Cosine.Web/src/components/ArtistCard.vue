<script setup lang="ts">
import { useRouter } from 'vue-router';
import CoverArt from '@/components/CoverArt.vue';
import { countLabel } from '@/lib/format';
import type { Artist } from '@/api/types';

const props = defineProps<{ artist: Artist }>();
const router = useRouter();

function open() {
  router.push({ name: 'artist', params: { id: props.artist.id } });
}
</script>

<template>
  <button
    type="button"
    class="group/artist flex w-full flex-col items-center text-center"
    @click="open"
  >
    <div class="w-full overflow-hidden rounded-full ring-1 ring-border transition-transform duration-150 group-hover/artist:scale-[1.03]">
      <CoverArt :cover-art="artist.coverArt ?? artist.id" :alt="artist.name" :size="400" shape="circle" />
    </div>
    <div class="mt-3 w-full truncate font-medium text-text">{{ artist.name }}</div>
    <div v-if="artist.albumCount != null" class="text-sm text-muted">
      {{ countLabel(artist.albumCount, 'album') }}
    </div>
  </button>
</template>
