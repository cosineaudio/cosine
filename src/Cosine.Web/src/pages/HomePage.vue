<script setup lang="ts">
import { ref, reactive, computed, onMounted, type Ref } from 'vue';
import { Sparkles, Radio } from 'lucide-vue-next';
import { getAlbumList2, getRandomSongs, getStarred2, getNowPlaying } from '@/api/lists';
import { getPlaylists, getPlaylist, isSystemPlaylist } from '@/api/playlists';
import type { NowPlayingEntry } from '@/api/lists';
import { useAuthStore } from '@/stores/auth';
import { useFavoritesStore } from '@/stores/favorites';
import { usePlayback } from '@/composables/usePlayback';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import SectionHeader from '@/components/ui/SectionHeader.vue';
import Shelf from '@/components/ui/Shelf.vue';
import AlbumCard from '@/components/AlbumCard.vue';
import ArtistCredit from '@/components/ArtistCredit.vue';
import TrackRow from '@/components/TrackRow.vue';
import PlayButton from '@/components/ui/PlayButton.vue';
import CoverArt from '@/components/CoverArt.vue';
import Skeleton from '@/components/ui/Skeleton.vue';
import SkeletonShelf from '@/components/ui/SkeletonShelf.vue';
import SkeletonTrackList from '@/components/ui/SkeletonTrackList.vue';
import type { Album, Track } from '@/api/types';

const auth = useAuthStore();
const favorites = useFavoritesStore();
const { playTracks } = usePlayback();

interface Section<T> {
  /** Whatever the endpoint returned; the initial value if it failed. */
  data: T;
  /** True only once the request has been slow enough to be worth admitting to. */
  pending: boolean;
  /** True from mount until the request settles, however fast that is. */
  loading: boolean;
}

/**
 * One independently-loading home section.
 *
 * The page used to `Promise.allSettled` all seven requests and reveal
 * everything at once, which meant the slowest endpoint set the speed of the
 * whole page - a sluggish `getNowPlaying` held the album shelves hostage. Each
 * section now resolves on its own and fills its own slot.
 *
 * Sections keep their place in the layout while loading, so nothing that
 * arrives late shoves the sections below it down the page.
 */
function useSection<T>(initial: T, load: () => Promise<T>): Section<T> & { run: () => Promise<void> } {
  const data = ref(initial) as Ref<T>;
  const loading = ref(true);
  const pending = useDeferredLoading(loading);

  async function run() {
    try {
      data.value = await load();
    } catch {
      /* a failed section is an absent section, not a broken page */
    } finally {
      loading.value = false;
    }
  }

  // `reactive` so the template can read `.data` / `.pending` without `.value`.
  return reactive({ data, pending, loading, run }) as Section<T> & { run: () => Promise<void> };
}

const releaseRadarId = ref<string | null>(null);

const nowPlaying = useSection<NowPlayingEntry[]>([], () => getNowPlaying());
const discovery = useSection<Track[]>([], () => getRandomSongs({ size: 12 }));
const newest = useSection<Album[]>([], () => getAlbumList2('newest', { size: 18 }));
const recent = useSection<Album[]>([], () => getAlbumList2('recent', { size: 18 }));
const frequent = useSection<Album[]>([], () => getAlbumList2('frequent', { size: 18 }));
const random = useSection<Album[]>([], () => getAlbumList2('random', { size: 18 }));

const favTracks = useSection<Track[]>([], async () => {
  const starred = await getStarred2();
  favorites.seed(starred);
  return starred.song.slice(0, 12);
});

/** The job-managed Release Radar playlist, if the user has one. */
const releaseRadar = useSection<Track[]>([], async () => {
  const lists = await getPlaylists();
  const radar = lists.find(isSystemPlaylist);
  if (!radar) return [];
  releaseRadarId.value = radar.id;
  return (await getPlaylist(radar.id)).entry;
});

const sections = [nowPlaying, discovery, releaseRadar, newest, recent, frequent, favTracks, random];

/** The library-is-empty message can only be honest once the album shelves have answered. */
const libraryEmpty = computed(
  () =>
    !newest.loading && !recent.loading && !random.loading &&
    !newest.data.length && !recent.data.length && !random.data.length,
);

const greeting = computed(() => {
  const h = new Date().getHours();
  if (h < 5) return 'Up late';
  if (h < 12) return 'Good morning';
  if (h < 18) return 'Good afternoon';
  return 'Good evening';
});

onMounted(() => {
  favorites.ensureLoaded();
  // Fired together rather than awaited in sequence: they are independent, and
  // each one reveals its own section the moment it lands.
  sections.forEach((s) => void s.run());
});

function playDiscovery() {
  playTracks(discovery.data, 0);
}

function playReleaseRadar() {
  playTracks(releaseRadar.data, 0);
}
</script>

<template>
  <div class="mx-auto max-w-screen-2xl px-6 pb-16 pt-8">
    <!-- greeting -->
    <div class="mb-10">
      <p class="label mb-2">{{ greeting }}</p>
      <h1
        class="font-display font-bold tracking-tight text-text"
        :style="{ fontSize: 'var(--text-display-xl)', lineHeight: '1' }"
      >
        {{ auth.user?.username ?? 'Welcome' }}
      </h1>
    </div>

    <div class="space-y-12">
      <!-- Listening now -->
      <SkeletonShelf v-if="nowPlaying.pending" :count="4" />
      <section v-else-if="nowPlaying.data.length" class="reveal">
        <SectionHeader title="Listening now" />
        <Shelf>
          <div
            v-for="(entry, i) in nowPlaying.data"
            :key="`${entry.id}-${i}`"
            class="w-44 shrink-0 snap-start sm:w-48"
          >
            <div class="group/card w-full">
              <div class="relative overflow-hidden rounded-control">
                <CoverArt :cover-art="entry.coverArt ?? entry.albumId ?? entry.id" :alt="entry.title" :size="400" />
                <div class="pointer-events-none absolute bottom-2 right-2 opacity-0 transition-opacity group-hover/card:pointer-events-auto group-hover/card:opacity-100">
                  <PlayButton size="md" @click.stop="playTracks([entry], 0)" />
                </div>
              </div>
              <div class="mt-3 truncate font-medium text-text">{{ entry.title }}</div>
              <div class="truncate text-sm text-muted">
                <!-- No username suffix: the shelf only ever shows the viewer's own sessions. -->
                <ArtistCredit :track="entry" class="inline" />
              </div>
            </div>
          </div>
        </Shelf>
      </section>

      <!-- Random discovery -->
      <div v-if="discovery.pending">
        <div class="mb-4 flex items-center" :style="{ height: 'calc(var(--text-title) * 1.1)' }">
          <Skeleton class="h-5 w-32" />
        </div>
        <div class="rounded-control border border-border bg-surface p-4 sm:p-5">
          <div class="mb-4 flex items-center gap-3">
            <Skeleton class="h-11 w-11" rounded="full" />
            <div class="flex flex-col gap-2">
              <Skeleton class="h-5 w-36" />
              <Skeleton class="h-3.5 w-52" />
            </div>
          </div>
          <SkeletonTrackList :count="12" show-art :columns="2" />
        </div>
      </div>
      <section v-else-if="discovery.data.length" class="reveal">
        <SectionHeader title="Discover" />
        <div class="rounded-control border border-border bg-surface p-4 sm:p-5">
          <div class="mb-4 flex items-center gap-3">
            <button
              type="button"
              class="grid h-11 w-11 place-items-center rounded-full bg-accent text-accent-ink transition-transform hover:scale-105 active:scale-95"
              aria-label="Play discovery mix"
              @click="playDiscovery"
            >
              <Sparkles :size="20" :stroke-width="2" />
            </button>
            <div>
              <div class="font-display text-title font-semibold tracking-tight text-text">Random mix</div>
              <div class="text-sm text-muted">A fresh handful from your library</div>
            </div>
          </div>
          <div class="grid gap-0.5 sm:grid-cols-2">
            <TrackRow
              v-for="(track, i) in discovery.data"
              :key="track.id"
              :track="track"
              :queue="discovery.data"
              :index="i + 1"
              show-art
            />
          </div>
        </div>
      </section>

      <!-- Release Radar -->
      <div v-if="releaseRadar.pending">
        <div class="mb-4 flex items-center" :style="{ height: 'calc(var(--text-title) * 1.1)' }">
          <Skeleton class="h-5 w-44" />
        </div>
        <div class="rounded-control border border-border bg-surface p-4 sm:p-5">
          <div class="mb-4 flex items-center gap-3">
            <Skeleton class="h-11 w-11" rounded="full" />
            <div class="flex flex-col gap-2">
              <Skeleton class="h-5 w-32" />
              <Skeleton class="h-3.5 w-56" />
            </div>
          </div>
          <SkeletonTrackList :count="12" show-art :columns="2" />
        </div>
      </div>
      <section v-else-if="releaseRadar.data.length" class="reveal">
        <SectionHeader
          title="Release Radar"
          :see-all="releaseRadarId ? { name: 'playlist', params: { id: releaseRadarId } } : undefined"
        />
        <div class="rounded-control border border-border bg-surface p-4 sm:p-5">
          <div class="mb-4 flex items-center gap-3">
            <button
              type="button"
              class="grid h-11 w-11 place-items-center rounded-full bg-accent text-accent-ink transition-transform hover:scale-105 active:scale-95"
              aria-label="Play Release Radar"
              @click="playReleaseRadar"
            >
              <Radio :size="20" :stroke-width="2" />
            </button>
            <div>
              <div class="font-display text-title font-semibold tracking-tight text-text">New for you</div>
              <div class="text-sm text-muted">Fresh releases from artists you love</div>
            </div>
          </div>
          <div class="grid gap-0.5 sm:grid-cols-2">
            <TrackRow
              v-for="(track, i) in releaseRadar.data.slice(0, 12)"
              :key="track.id"
              :track="track"
              :queue="releaseRadar.data"
              :index="i + 1"
              show-art
            />
          </div>
        </div>
      </section>

      <!-- Recently added -->
      <SkeletonShelf v-if="newest.pending" />
      <section v-else-if="newest.data.length" class="reveal">
        <SectionHeader title="Recently added" :see-all="{ name: 'albums', query: { sort: 'newest' } }" />
        <Shelf>
          <div v-for="album in newest.data" :key="album.id" class="w-44 shrink-0 snap-start sm:w-48">
            <AlbumCard :album="album" />
          </div>
        </Shelf>
      </section>

      <!-- Jump back in -->
      <SkeletonShelf v-if="recent.pending" />
      <section v-else-if="recent.data.length" class="reveal">
        <SectionHeader title="Jump back in" />
        <Shelf>
          <div v-for="album in recent.data" :key="album.id" class="w-44 shrink-0 snap-start sm:w-48">
            <AlbumCard :album="album" />
          </div>
        </Shelf>
      </section>

      <!-- Most played -->
      <SkeletonShelf v-if="frequent.pending" />
      <section v-else-if="frequent.data.length" class="reveal">
        <SectionHeader title="Most played" :see-all="{ name: 'albums', query: { sort: 'frequent' } }" />
        <Shelf>
          <div v-for="album in frequent.data" :key="album.id" class="w-44 shrink-0 snap-start sm:w-48">
            <AlbumCard :album="album" />
          </div>
        </Shelf>
      </section>

      <!-- Your favorites -->
      <div v-if="favTracks.pending">
        <div class="mb-4 flex items-center" :style="{ height: 'calc(var(--text-title) * 1.1)' }">
          <Skeleton class="h-5 w-40" />
        </div>
        <SkeletonTrackList :count="12" show-art :columns="2" />
      </div>
      <section v-else-if="favTracks.data.length" class="reveal">
        <SectionHeader title="Your favorites" :see-all="{ name: 'favorites' }" />
        <div class="grid gap-0.5 sm:grid-cols-2">
          <TrackRow
            v-for="track in favTracks.data"
            :key="track.id"
            :track="track"
            :queue="favTracks.data"
            show-art
          />
        </div>
      </section>

      <!-- Random albums -->
      <SkeletonShelf v-if="random.pending" />
      <section v-else-if="random.data.length" class="reveal">
        <SectionHeader title="From the vault" :see-all="{ name: 'albums', query: { sort: 'random' } }" />
        <Shelf>
          <div v-for="album in random.data" :key="album.id" class="w-44 shrink-0 snap-start sm:w-48">
            <AlbumCard :album="album" />
          </div>
        </Shelf>
      </section>

      <!-- empty -->
      <div v-if="libraryEmpty" class="reveal flex flex-col items-center py-24 text-center">
        <Radio :size="40" :stroke-width="1.5" class="mb-4 text-faint" />
        <p class="font-display text-title text-text">Nothing here yet</p>
        <p class="mt-1 text-sm text-muted">Scan your library to fill the airwaves.</p>
      </div>
    </div>
  </div>
</template>
