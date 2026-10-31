<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { toast } from 'vue-sonner';
import { Radio, Plus, Play, Trash2, ExternalLink, X } from 'lucide-vue-next';
import {
  getInternetRadioStations,
  createInternetRadioStation,
  deleteInternetRadioStation,
  type InternetRadioStation,
} from '@/api/radio';
import { usePlaybackStore } from '@/stores/playback';
import { useAuthStore } from '@/stores/auth';
import EmptyState from '@/components/ui/EmptyState.vue';
import Skeleton from '@/components/ui/Skeleton.vue';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import { shimmerDelay } from '@/lib/skeleton';
import type { Track } from '@/api/types';

const player = usePlaybackStore();
// Stations are a single global list, so the server only lets admins mutate them.
const auth = useAuthStore();

const stations = ref<InternetRadioStation[]>([]);
const loading = ref(true);
const showSkeleton = useDeferredLoading(loading);
const errored = ref(false);

const adding = ref(false);
const formName = ref('');
const formUrl = ref('');
const formHomepage = ref('');
const saving = ref(false);

const nowPlayingId = computed(() => player.currentTrackId);

function stationToTrack(s: InternetRadioStation): Track {
  return {
    id: s.id,
    isDir: false,
    title: s.name,
    artist: 'Internet radio',
    streamUrl: s.streamUrl,
    coverArt: undefined,
  };
}

function playStation(s: InternetRadioStation) {
  const track = stationToTrack(s);
  player.play(track, [track]);
}

async function load() {
  loading.value = true;
  errored.value = false;
  try {
    stations.value = await getInternetRadioStations();
  } catch {
    errored.value = true;
  } finally {
    loading.value = false;
  }
}

function openAdd() {
  formName.value = '';
  formUrl.value = '';
  formHomepage.value = '';
  adding.value = true;
}

async function addStation() {
  const name = formName.value.trim();
  const streamUrl = formUrl.value.trim();
  if (!name || !streamUrl) return;
  saving.value = true;
  try {
    await createInternetRadioStation({ name, streamUrl, homepageUrl: formHomepage.value.trim() || undefined });
    adding.value = false;
    toast.success(`Added “${name}”`);
    await load();
  } catch {
    toast.error('Could not add station');
  } finally {
    saving.value = false;
  }
}

async function removeStation(s: InternetRadioStation) {
  const prev = stations.value;
  stations.value = stations.value.filter((x) => x.id !== s.id);
  try {
    await deleteInternetRadioStation(s.id);
    toast.success(`Removed “${s.name}”`);
  } catch {
    stations.value = prev;
    toast.error('Could not remove station');
  }
}

onMounted(load);
</script>

<template>
  <div class="mx-auto max-w-screen-2xl px-6 pb-16 pt-8">
    <header class="mb-6 flex flex-wrap items-end justify-between gap-4">
      <div>
        <span class="label">Listen</span>
        <h1 class="mt-1 font-display font-bold tracking-tight text-text" :style="{ fontSize: 'var(--text-display-l)', lineHeight: '1' }">
          Radio
        </h1>
      </div>
      <button
        v-if="auth.isAdmin"
        type="button"
        class="flex items-center gap-2 rounded-control bg-accent px-4 py-2 text-sm font-medium text-accent-ink transition-transform hover:scale-[1.02] active:scale-95"
        @click="openAdd"
      >
        <Plus :size="16" :stroke-width="2" /> Add station
      </button>
    </header>

    <!-- add form -->
    <form
      v-if="adding && auth.isAdmin"
      class="mb-8 grid gap-3 rounded-control border border-border bg-surface p-4 sm:grid-cols-[1fr_1fr_auto]"
      @submit.prevent="addStation"
    >
      <div>
        <label class="label mb-1.5 block">Name</label>
        <input v-model="formName" type="text" required placeholder="Station name" class="w-full rounded-control border border-border bg-surface-2 px-3 py-2 text-sm text-text placeholder:text-faint focus:border-transparent" />
      </div>
      <div>
        <label class="label mb-1.5 block">Stream URL</label>
        <input v-model="formUrl" type="url" required placeholder="https://…" class="w-full rounded-control border border-border bg-surface-2 px-3 py-2 text-sm text-text placeholder:text-faint focus:border-transparent" />
      </div>
      <div class="flex items-end gap-2">
        <button type="submit" :disabled="saving" class="rounded-control bg-accent px-4 py-2 text-sm font-medium text-accent-ink disabled:opacity-50">
          {{ saving ? 'Saving…' : 'Save' }}
        </button>
        <button type="button" class="grid h-9 w-9 place-items-center rounded-control border border-border text-muted hover:text-text" aria-label="Cancel" @click="adding = false">
          <X :size="18" :stroke-width="2" />
        </button>
      </div>
    </form>

    <!-- loading -->
    <div v-if="showSkeleton" class="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
      <Skeleton v-for="i in 6" :key="i" class="h-20" :style="shimmerDelay(i)" />
    </div>

    <!-- nothing in the pre-skeleton gap; see useDeferredLoading -->
    <template v-else-if="loading" />

    <!-- error -->
    <EmptyState
      v-else-if="errored"
      :icon="Radio"
      title="Couldn't load stations"
      description="The server may not support internet radio, or something went wrong."
    >
      <button type="button" class="rounded-control border border-border px-4 py-2 text-sm text-text hover:bg-surface-2" @click="load">Try again</button>
    </EmptyState>

    <!-- stations -->
    <div v-else-if="stations.length" class="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
      <div
        v-for="s in stations"
        :key="s.id"
        class="group/station flex items-center gap-3 rounded-control border border-border bg-surface p-3 transition-colors hover:bg-surface-2"
        :class="s.id === nowPlayingId ? 'border-accent/60' : ''"
      >
        <button
          type="button"
          class="grid h-11 w-11 shrink-0 place-items-center rounded-control bg-surface-2 text-text ring-1 ring-border transition-colors group-hover/station:bg-accent group-hover/station:text-accent-ink"
          :aria-label="`Play ${s.name}`"
          @click="playStation(s)"
        >
          <Play :size="18" :stroke-width="2" fill="currentColor" class="translate-x-px" />
        </button>
        <div class="min-w-0 flex-1">
          <div class="truncate font-medium text-text" :class="s.id === nowPlayingId ? 'text-accent' : ''">{{ s.name }}</div>
          <div class="truncate font-mono text-[11px] text-muted">{{ s.streamUrl }}</div>
        </div>
        <a
          v-if="s.homepageUrl"
          :href="s.homepageUrl"
          target="_blank"
          rel="noopener noreferrer"
          class="grid h-8 w-8 shrink-0 place-items-center rounded-control text-muted transition-colors hover:text-text"
          :aria-label="`Open ${s.name} homepage`"
        >
          <ExternalLink :size="16" :stroke-width="2" />
        </a>
        <button
          v-if="auth.isAdmin"
          type="button"
          class="grid h-8 w-8 shrink-0 place-items-center rounded-control text-muted opacity-0 transition-all hover:text-danger group-hover/station:opacity-100"
          :aria-label="`Remove ${s.name}`"
          @click="removeStation(s)"
        >
          <Trash2 :size="16" :stroke-width="2" />
        </button>
      </div>
    </div>

    <!-- empty -->
    <EmptyState
      v-else
      :icon="Radio"
      title="No stations yet"
      :description="auth.isAdmin
        ? 'Add an internet-radio stream URL to start listening.'
        : 'An administrator can add internet-radio stations for everyone.'"
    >
      <button v-if="auth.isAdmin" type="button" class="flex items-center gap-2 rounded-control bg-accent px-4 py-2 text-sm font-medium text-accent-ink" @click="openAdd">
        <Plus :size="16" :stroke-width="2" /> Add station
      </button>
    </EmptyState>
  </div>
</template>
