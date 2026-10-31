<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { Users } from 'lucide-vue-next';
import { getArtists } from '@/api/browsing';
import { useFavoritesStore } from '@/stores/favorites';
import ArtistCard from '@/components/ArtistCard.vue';
import EmptyState from '@/components/ui/EmptyState.vue';
import SkeletonGrid from '@/components/ui/SkeletonGrid.vue';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import type { IndexGroup } from '@/api/types';

const favorites = useFavoritesStore();
const indexes = ref<IndexGroup[]>([]);
const loading = ref(true);
const showSkeleton = useDeferredLoading(loading);

const groups = computed(() => indexes.value.filter((g) => g.artist.length > 0));
const totalCount = computed(() => groups.value.reduce((n, g) => n + g.artist.length, 0));

function slug(name: string) {
  return `group-${name.replace(/[^a-z0-9]/gi, '_')}`;
}

function jumpTo(name: string) {
  document.getElementById(slug(name))?.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

onMounted(async () => {
  favorites.ensureLoaded();
  try {
    const data = await getArtists();
    indexes.value = data.artists?.index ?? [];
  } catch { /* handled by empty state */ } finally {
    loading.value = false;
  }
});
</script>

<template>
  <div class="mx-auto max-w-screen-2xl px-6 pb-16 pt-8">
    <div class="mb-8 flex items-end justify-between gap-4">
      <h1 class="font-display font-bold tracking-tight text-text" :style="{ fontSize: 'var(--text-display-l)', lineHeight: '1' }">
        Artists
      </h1>
      <span v-if="totalCount" class="font-mono text-sm text-muted">{{ totalCount }}</span>
    </div>

    <SkeletonGrid
      v-if="showSkeleton"
      class="grid grid-cols-2 gap-x-4 gap-y-8 sm:grid-cols-4 lg:grid-cols-6"
      shape="circle"
      :count="12"
    />

    <!--
      Nothing renders in the gap between mount and the skeleton's delay. Falling
      through to the empty state there would flash "No artists" at every visitor
      for a fifth of a second before their artists arrived.
    -->
    <template v-else-if="!loading">
      <EmptyState
        v-if="!groups.length"
        :icon="Users"
        title="No artists"
        description="Scan your library to build your collection."
      />

      <div v-else class="reveal relative flex gap-4">
        <div class="min-w-0 flex-1 space-y-12">
          <section v-for="group in groups" :id="slug(group.name)" :key="group.name" class="scroll-mt-20">
            <h2 class="mb-4 font-display text-title font-semibold tracking-tight text-muted">
              {{ group.name }}
            </h2>
            <div class="grid grid-cols-2 gap-x-4 gap-y-8 sm:grid-cols-4 lg:grid-cols-6">
              <ArtistCard v-for="artist in group.artist" :key="artist.id" :artist="artist" />
            </div>
          </section>
        </div>

        <!-- A–Z jump rail -->
        <nav
          class="sticky top-4 hidden h-fit shrink-0 flex-col items-center gap-0.5 self-start py-2 md:flex"
          aria-label="Jump to letter"
        >
          <button
            v-for="group in groups"
            :key="group.name"
            type="button"
            class="grid h-5 w-5 place-items-center rounded-[3px] font-mono text-[11px] text-faint transition-colors hover:bg-surface-2 hover:text-text"
            @click="jumpTo(group.name)"
          >
            {{ group.name }}
          </button>
        </nav>
      </div>
    </template>
  </div>
</template>
