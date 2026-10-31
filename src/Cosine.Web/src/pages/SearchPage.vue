<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { refDebounced } from '@vueuse/core';
import { Search as SearchIcon, X, Clock, Play } from 'lucide-vue-next';
import { search3 } from '@/api/browsing';
import { useFavoritesStore } from '@/stores/favorites';
import { usePlayback } from '@/composables/usePlayback';
import CoverArt from '@/components/CoverArt.vue';
import AlbumCard from '@/components/AlbumCard.vue';
import ArtistCard from '@/components/ArtistCard.vue';
import TrackRow from '@/components/TrackRow.vue';
import SectionHeader from '@/components/ui/SectionHeader.vue';
import Skeleton from '@/components/ui/Skeleton.vue';
import SkeletonGrid from '@/components/ui/SkeletonGrid.vue';
import SkeletonTrackList from '@/components/ui/SkeletonTrackList.vue';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import type { SearchResults } from '@/api/types';

const RECENT_KEY = 'cosine_recent_searches';
const MAX_RECENT = 8;

const route = useRoute();
const router = useRouter();
const favorites = useFavoritesStore();
const { playTracks } = usePlayback();

const query = ref((route.query.q as string) ?? '');
const debounced = refDebounced(query, 250);
const results = ref<SearchResults | null>(null);
const loading = ref(false);
const recent = ref<string[]>([]);

let reqId = 0;

const hasResults = computed(
  () => !!results.value && (results.value.artist.length || results.value.album.length || results.value.song.length),
);

// A first search gets a skeleton; refining an existing one keeps the results on
// screen and dims them. Blanking the page on every keystroke would make a fast
// search feel far more disruptive than it is.
const showSkeleton = useDeferredLoading(computed(() => loading.value && !results.value));
const dimStale = useDeferredLoading(computed(() => loading.value && !!results.value));

// Top result: prefer an artist, then album, then song.
const topResult = computed<
  { kind: 'artist' | 'album'; id: string; name: string; sub: string; cover: string } | null
>(() => {
  const r = results.value;
  if (!r) return null;
  if (r.artist.length) {
    const a = r.artist[0];
    return { kind: 'artist', id: a.id, name: a.name, sub: 'Artist', cover: a.coverArt ?? a.id };
  }
  if (r.album.length) {
    const a = r.album[0];
    return { kind: 'album', id: a.id, name: a.name, sub: a.artist ?? 'Album', cover: a.coverArt ?? a.id };
  }
  return null;
});

function loadRecent() {
  try {
    recent.value = JSON.parse(localStorage.getItem(RECENT_KEY) ?? '[]');
  } catch { recent.value = []; }
}

function pushRecent(term: string) {
  const t = term.trim();
  if (!t) return;
  recent.value = [t, ...recent.value.filter((r) => r.toLowerCase() !== t.toLowerCase())].slice(0, MAX_RECENT);
  localStorage.setItem(RECENT_KEY, JSON.stringify(recent.value));
}

function clearRecent() {
  recent.value = [];
  localStorage.removeItem(RECENT_KEY);
}

async function run(term: string) {
  const q = term.trim();
  if (!q) {
    results.value = null;
    loading.value = false;
    return;
  }
  const id = ++reqId;
  loading.value = true;
  try {
    const data = await search3(q, { artistCount: 12, albumCount: 24, songCount: 40 });
    if (id !== reqId) return; // stale response
    results.value = data.searchResult3;
    favorites.seed(data.searchResult3);
  } catch {
    if (id === reqId) results.value = { artist: [], album: [], song: [] };
  } finally {
    if (id === reqId) loading.value = false;
  }
}

function openTop() {
  if (!topResult.value) return;
  pushRecent(query.value);
  router.push({ name: topResult.value.kind, params: { id: topResult.value.id } });
}

function useRecent(term: string) {
  query.value = term;
}

watch(debounced, (q) => {
  run(q);
  router.replace({ query: q ? { q } : {} });
  if (q.trim()) pushRecent(q);
});

onMounted(() => {
  favorites.ensureLoaded();
  loadRecent();
  if (query.value) run(query.value);
});
</script>

<template>
  <div class="mx-auto max-w-screen-2xl px-6 pb-16 pt-8">
    <!-- search field -->
    <div class="relative mb-8">
      <SearchIcon :size="20" :stroke-width="2" class="pointer-events-none absolute left-4 top-1/2 -translate-y-1/2 text-muted" />
      <input
        v-model="query"
        type="search"
        autofocus
        placeholder="Search artists, albums, songs…"
        class="w-full rounded-control border border-border bg-surface py-3.5 pl-12 pr-12 text-lg text-text placeholder:text-faint focus:border-transparent"
      />
      <button
        v-if="query"
        type="button"
        class="absolute right-3 top-1/2 grid h-8 w-8 -translate-y-1/2 place-items-center rounded-control text-muted hover:bg-surface-2 hover:text-text"
        aria-label="Clear search"
        @click="query = ''"
      >
        <X :size="18" :stroke-width="2" />
      </button>
    </div>

    <!-- recent searches (no query) -->
    <div v-if="!query.trim()">
      <div v-if="recent.length">
        <div class="mb-4 flex items-center justify-between">
          <h2 class="font-display text-title font-semibold tracking-tight text-text">Recent</h2>
          <button type="button" class="text-xs font-medium uppercase tracking-widest text-faint hover:text-text" @click="clearRecent">
            Clear
          </button>
        </div>
        <div class="flex flex-wrap gap-2">
          <button
            v-for="term in recent"
            :key="term"
            type="button"
            class="flex items-center gap-2 rounded-control border border-border px-3 py-1.5 text-sm text-muted transition-colors hover:border-faint hover:text-text"
            @click="useRecent(term)"
          >
            <Clock :size="14" :stroke-width="2" />{{ term }}
          </button>
        </div>
      </div>
      <div v-else class="flex flex-col items-center py-24 text-center">
        <SearchIcon :size="40" :stroke-width="1.5" class="mb-4 text-faint" />
        <p class="font-display text-title text-text">Find your sound</p>
        <p class="mt-1 text-sm text-muted">Search across every artist, album, and track.</p>
      </div>
    </div>

    <!-- results -->
    <template v-else>
      <div v-if="showSkeleton" class="space-y-12">
        <div class="grid gap-8 lg:grid-cols-2">
          <section>
            <div class="mb-4 flex items-center" :style="{ height: 'calc(var(--text-title) * 1.1)' }">
              <Skeleton class="h-5 w-28" />
            </div>
            <div class="flex w-full items-center gap-4 rounded-control border border-border bg-surface p-4">
              <Skeleton class="h-20 w-20 shrink-0" />
              <div class="flex min-w-0 flex-1 flex-col gap-2">
                <Skeleton class="h-6 w-2/3" />
                <Skeleton class="h-4 w-20" />
              </div>
            </div>
          </section>
          <section>
            <div class="mb-4 flex items-center" :style="{ height: 'calc(var(--text-title) * 1.1)' }">
              <Skeleton class="h-5 w-20" />
            </div>
            <SkeletonTrackList :count="6" show-art />
          </section>
        </div>
        <section>
          <div class="mb-4 flex items-center" :style="{ height: 'calc(var(--text-title) * 1.1)' }">
            <Skeleton class="h-5 w-24" />
          </div>
          <SkeletonGrid
            class="grid grid-cols-2 gap-x-4 gap-y-8 sm:grid-cols-4 lg:grid-cols-6"
            shape="circle"
            :count="6"
          />
        </section>
        <section>
          <div class="mb-4 flex items-center" :style="{ height: 'calc(var(--text-title) * 1.1)' }">
            <Skeleton class="h-5 w-24" />
          </div>
          <SkeletonGrid
            class="grid grid-cols-2 gap-x-4 gap-y-6 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6"
            :count="12"
          />
        </section>
      </div>

      <div
        v-else-if="hasResults"
        class="space-y-12 transition-opacity duration-200"
        :class="dimStale ? 'pointer-events-none opacity-40' : ''"
      >
        <!-- Top result + Songs -->
        <div class="grid gap-8 lg:grid-cols-2">
          <section v-if="topResult">
            <SectionHeader title="Top result" />
            <button
              type="button"
              class="group flex w-full items-center gap-4 rounded-control border border-border bg-surface p-4 text-left transition-colors hover:bg-surface-2"
              @click="openTop"
            >
              <div class="h-20 w-20 shrink-0 overflow-hidden" :class="topResult.kind === 'artist' ? 'rounded-full' : 'rounded-control'">
                <CoverArt :cover-art="topResult.cover" :alt="topResult.name" :size="160" :shape="topResult.kind === 'artist' ? 'circle' : 'square'" />
              </div>
              <div class="min-w-0 flex-1">
                <div class="truncate font-display text-title font-semibold tracking-tight text-text">{{ topResult.name }}</div>
                <div class="truncate text-sm text-muted">{{ topResult.sub }}</div>
              </div>
            </button>
          </section>

          <section v-if="results?.song.length">
            <SectionHeader title="Songs" />
            <div class="grid gap-0.5">
              <TrackRow
                v-for="track in results.song.slice(0, 6)"
                :key="track.id"
                :track="track"
                :queue="results.song"
                show-art
              />
            </div>
            <button
              v-if="results.song.length > 6"
              type="button"
              class="mt-3 flex items-center gap-2 text-xs font-medium uppercase tracking-widest text-faint hover:text-text"
              @click="playTracks(results.song, 0)"
            >
              <Play :size="14" :stroke-width="2" fill="currentColor" /> Play all {{ results.song.length }} songs
            </button>
          </section>
        </div>

        <!-- Artists -->
        <section v-if="results?.artist.length">
          <SectionHeader title="Artists" />
          <div class="grid grid-cols-2 gap-x-4 gap-y-8 sm:grid-cols-4 lg:grid-cols-6">
            <ArtistCard v-for="a in results.artist" :key="a.id" :artist="a" />
          </div>
        </section>

        <!-- Albums -->
        <section v-if="results?.album.length">
          <SectionHeader title="Albums" />
          <div class="grid grid-cols-2 gap-x-4 gap-y-6 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6">
            <AlbumCard v-for="album in results.album" :key="album.id" :album="album" />
          </div>
        </section>
      </div>

      <div v-else-if="!loading" class="flex flex-col items-center py-24 text-center">
        <SearchIcon :size="40" :stroke-width="1.5" class="mb-4 text-faint" />
        <p class="font-display text-title text-text">No results</p>
        <p class="mt-1 text-sm text-muted">Nothing matches “{{ query }}”.</p>
      </div>
    </template>
  </div>
</template>
