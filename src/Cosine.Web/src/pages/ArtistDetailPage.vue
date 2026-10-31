<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue';
import { toast } from 'vue-sonner';
import { Play, Shuffle, Radio } from 'lucide-vue-next';
import { getArtist, getArtistInfo2, getTopSongs, getSimilarSongs2 } from '@/api/browsing';
import { getArtistSongs } from '@/api/library';
import { useFavoritesStore } from '@/stores/favorites';
import { usePlayback } from '@/composables/usePlayback';
import PageHeader from '@/components/ui/PageHeader.vue';
import SectionHeader from '@/components/ui/SectionHeader.vue';
import AlbumCard from '@/components/AlbumCard.vue';
import ArtistCard from '@/components/ArtistCard.vue';
import TrackRow from '@/components/TrackRow.vue';
import FavoriteButton from '@/components/ui/FavoriteButton.vue';
import Spinner from '@/components/ui/Spinner.vue';
import SkeletonDetailHeader from '@/components/ui/SkeletonDetailHeader.vue';
import SkeletonTrackList from '@/components/ui/SkeletonTrackList.vue';
import SkeletonGrid from '@/components/ui/SkeletonGrid.vue';
import Skeleton from '@/components/ui/Skeleton.vue';
import { useDominantColor } from '@/composables/useDominantColor';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import { countLabel } from '@/lib/format';
import { shuffled } from '@/lib/shuffle';
import type { ArtistDetail, ArtistInfo, Track } from '@/api/types';
import type { QueueSource } from '@/stores/autoPlay';

const props = defineProps<{ id: string }>();

const favorites = useFavoritesStore();
const { playTracks } = usePlayback();

const artist = ref<ArtistDetail | null>(null);
const info = ref<ArtistInfo | null>(null);
const topSongs = ref<Track[]>([]);
/** The artist's whole catalogue - own albums plus anything they are credited on. */
const allSongs = ref<Track[]>([]);
const loading = ref(true);
const showSkeleton = useDeferredLoading(loading);
const showFullBio = ref(false);
const radioLoading = ref(false);
/** Which of Play/Shuffle is fetching, so only that button shows as busy. */
const queueLoading = ref<'play' | 'shuffle' | null>(null);

/**
 * Tracks this artist is credited on that live on somebody else's album - the guest spots.
 *
 * Without this a featured-only artist's page is blank: they have no albums of their own, and
 * "Top songs" is play-count ranked, so an artist nobody has played yet shows nothing at all.
 * That is a bad place to land, and every guest name in every artist line now links here.
 */
const guestAppearances = computed(() => {
  const own = new Set((artist.value?.album ?? []).map((a) => a.id));
  return allSongs.value.filter((t) => !t.albumId || !own.has(t.albumId));
});

/**
 * What auto-play should read on through once one of these short lists runs out: the rest of this
 * artist's catalogue, rather than a blend seeded off the last few tracks. Top songs are ten
 * play-count-ranked tracks and "Appears on" is the guest spots - neither is all there is of them.
 *
 * The Play and Shuffle buttons deliberately do not carry it: they queue the whole catalogue
 * already, so there is nothing left of the artist to read on into.
 */
const queueSource = computed<QueueSource>(() => ({ kind: 'artist', id: props.id }));

const isFav = computed(() => (artist.value ? favorites.isArtist(artist.value.id) : false));
const bio = computed(() => (info.value?.biography ?? '').replace(/<[^>]*>/g, '').trim());
const { tint } = useDominantColor(() => (artist.value ? artist.value.coverArt ?? artist.value.id : null));

async function load() {
  loading.value = true;
  artist.value = null;
  info.value = null;
  topSongs.value = [];
  allSongs.value = [];
  showFullBio.value = false;
  try {
    artist.value = await getArtist(props.id);
    favorites.ensureLoaded();
    const [i, t, s] = await Promise.allSettled([
      getArtistInfo2(props.id, { count: 12 }),
      getTopSongs(props.id, 10),
      getArtistSongs(props.id),
    ]);
    if (i.status === 'fulfilled') info.value = i.value;
    if (t.status === 'fulfilled') topSongs.value = t.value;
    if (s.status === 'fulfilled') allSongs.value = s.value;
  } catch { /* not found handled below */ } finally {
    loading.value = false;
  }
}

/**
 * Queue the artist's whole catalogue - every album, in order, optionally shuffled.
 *
 * Both buttons used to short-circuit on `topSongs` and queue those ten tracks instead, which
 * made Shuffle a shuffle of the ten most-played songs. The catalogue arrives in one request
 * (`/api/library`, not Subsonic - see `api/library.ts`), so this no longer costs one round
 * trip per album either.
 */
async function playAll(shuffle = false) {
  if (!artist.value || queueLoading.value) return;
  queueLoading.value = shuffle ? 'shuffle' : 'play';
  try {
    const tracks = await getArtistSongs(artist.value.id);
    if (tracks.length === 0) {
      toast.error('This artist has no playable tracks');
      return;
    }
    playTracks(shuffle ? shuffled(tracks) : tracks, 0);
  } catch {
    toast.error(shuffle ? 'Could not shuffle this artist' : 'Could not play this artist');
  } finally {
    queueLoading.value = null;
  }
}

/** Build an endless-ish radio queue from this artist and similar artists. */
async function startRadio() {
  if (!artist.value || radioLoading.value) return;
  radioLoading.value = true;
  try {
    const tracks = await getSimilarSongs2(artist.value.id, 100);
    if (tracks.length === 0) {
      toast.error('Not enough library data to build a radio yet');
      return;
    }
    playTracks(tracks, 0);
    toast.success(`Radio started from ${artist.value.name}`);
  } catch {
    toast.error('Could not start radio');
  } finally {
    radioLoading.value = false;
  }
}

onMounted(load);
watch(() => props.id, load);
</script>

<template>
  <div v-if="showSkeleton">
    <SkeletonDetailHeader shape="circle" :actions="4" />
    <div class="mx-auto max-w-screen-2xl space-y-12 px-6 pb-16 pt-4">
      <!-- bio -->
      <div class="flex max-w-3xl flex-col gap-2">
        <Skeleton class="h-3.5 w-full" />
        <Skeleton class="h-3.5 w-11/12" />
        <Skeleton class="h-3.5 w-3/5" />
      </div>
      <!-- top songs -->
      <section>
        <div class="mb-4 flex items-center" :style="{ height: 'calc(var(--text-title) * 1.1)' }">
          <Skeleton class="h-5 w-32" />
        </div>
        <SkeletonTrackList :count="5" show-art />
      </section>
      <!-- albums -->
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
  </div>

  <!-- blank until the skeleton's delay elapses; see useDeferredLoading -->
  <template v-else-if="loading" />

  <div v-else-if="artist" class="reveal">
    <PageHeader kicker="Artist" :title="artist.name" :cover-art="artist.coverArt ?? artist.id" shape="circle" :tint="tint">
      <template #meta>
        <span>{{ countLabel(artist.albumCount, 'album') }}</span>
      </template>
      <template #actions>
        <button
          type="button"
          class="flex items-center gap-2 rounded-control bg-accent px-5 py-2.5 font-medium text-accent-ink transition-transform hover:scale-[1.02] active:scale-95 disabled:opacity-60"
          :disabled="queueLoading !== null"
          @click="playAll(false)"
        >
          <!-- the icon carries the busy state; swapping the label would jitter the width -->
          <Spinner v-if="queueLoading === 'play'" size="md" />
          <Play v-else :size="18" :stroke-width="2" fill="currentColor" /> Play
        </button>
        <button
          type="button"
          class="flex items-center gap-2 rounded-control border border-border px-4 py-2.5 font-medium text-text transition-colors hover:bg-surface-2 disabled:opacity-60"
          :disabled="queueLoading !== null"
          @click="playAll(true)"
        >
          <Spinner v-if="queueLoading === 'shuffle'" size="md" />
          <Shuffle v-else :size="18" :stroke-width="2" /> Shuffle
        </button>
        <button
          type="button"
          class="flex items-center gap-2 rounded-control border border-border px-4 py-2.5 font-medium text-text transition-colors hover:bg-surface-2 disabled:opacity-60"
          :disabled="radioLoading"
          @click="startRadio"
        >
          <Spinner v-if="radioLoading" size="md" />
          <Radio v-else :size="18" :stroke-width="2" /> Radio
        </button>
        <div class="rounded-control border border-border p-1.5">
          <FavoriteButton :active="isFav" :size="20" @toggle="favorites.toggle('artist', artist.id, artist.name)" />
        </div>
      </template>
    </PageHeader>

    <div class="mx-auto max-w-screen-2xl space-y-12 px-6 pb-16 pt-4">
      <!-- Bio -->
      <section v-if="bio">
        <p class="max-w-3xl text-sm leading-relaxed text-muted" :class="showFullBio ? '' : 'line-clamp-3'">
          {{ bio }}
        </p>
        <button
          v-if="bio.length > 240"
          type="button"
          class="mt-2 text-xs font-medium uppercase tracking-widest text-faint transition-colors hover:text-text"
          @click="showFullBio = !showFullBio"
        >
          {{ showFullBio ? 'Show less' : 'Read more' }}
        </button>
      </section>

      <!-- Top songs -->
      <section v-if="topSongs.length">
        <SectionHeader title="Top songs" />
        <div class="grid gap-0.5">
          <TrackRow
            v-for="(track, i) in topSongs"
            :key="track.id"
            :track="track"
            :queue="topSongs"
            :queue-source="queueSource"
            :index="i + 1"
            show-art
          />
        </div>
      </section>

      <!-- Appears on -->
      <section v-if="guestAppearances.length">
        <SectionHeader title="Appears on" />
        <div class="grid gap-0.5">
          <TrackRow
            v-for="(track, i) in guestAppearances"
            :key="track.id"
            :track="track"
            :queue="guestAppearances"
            :queue-source="queueSource"
            :index="i + 1"
            show-art
            show-album
          />
        </div>
      </section>

      <!-- Albums -->
      <section v-if="artist.album.length">
        <SectionHeader title="Albums" />
        <div class="grid grid-cols-2 gap-x-4 gap-y-6 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6">
          <AlbumCard v-for="album in artist.album" :key="album.id" :album="album" />
        </div>
      </section>

      <!-- Similar artists -->
      <section v-if="info?.similarArtist.length">
        <SectionHeader title="Similar artists" />
        <div class="grid grid-cols-2 gap-x-4 gap-y-8 sm:grid-cols-4 lg:grid-cols-6">
          <ArtistCard v-for="a in info.similarArtist.slice(0, 12)" :key="a.id" :artist="a" />
        </div>
      </section>
    </div>
  </div>

  <div v-else class="mx-auto max-w-screen-2xl px-6 py-20 text-center text-muted">Artist not found.</div>
</template>
