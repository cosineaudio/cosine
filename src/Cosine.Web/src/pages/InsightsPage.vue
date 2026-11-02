<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { BarChart3, Play } from 'lucide-vue-next';
import { toast } from 'vue-sonner';
import { getListeningInsights, type ListeningInsights } from '@/api/insights';
import { getTrack } from '@/api/browsing';
import { usePlayback } from '@/composables/usePlayback';
import EmptyState from '@/components/ui/EmptyState.vue';
import Spinner from '@/components/ui/Spinner.vue';
import MonthStepper from '@/components/ui/MonthStepper.vue';

const now = new Date();
const month = ref(`${now.getUTCFullYear()}-${String(now.getUTCMonth() + 1).padStart(2, '0')}`);
const maxMonth = month.value;
const data = ref<ListeningInsights | null>(null);
const loading = ref(false);
const error = ref('');
const { playTracks } = usePlayback();
let requestId = 0;
async function load() {
  const id = ++requestId;
  loading.value = true;
  error.value = '';
  data.value = null;
  const [year, m] = month.value.split('-').map(Number);
  try {
    const result = await getListeningInsights(year!, m!);
    if (id === requestId) data.value = result;
  } catch (e) { if (id === requestId) error.value = e instanceof Error ? e.message : 'Could not load insights'; }
  finally { if (id === requestId) loading.value = false; }
}
watch(month, () => { if (/^\d{4}-\d{2}$/.test(month.value)) void load(); }, { immediate: true });
const progress = computed(() => data.value?.libraryTracks ? Math.round(data.value.exploredTracks / data.value.libraryTracks * 100) : 0);
const change = computed(() => {
  if (!data.value) return '';
  const previous = data.value.previousMonthPlays;
  if (!previous) return 'No plays recorded in the previous month';
  const pct = Math.round((data.value.recap.totalPlays - previous) / previous * 100);
  return `${pct > 0 ? '+' : ''}${pct}% plays vs. previous month`;
});
const days = computed(() => {
  if (!data.value) return [];
  const start = new Date(data.value.from);
  const end = new Date(data.value.until);
  const counts = new Map(data.value.recap.playsOverTime.map(b => [b.date.slice(0, 10), b.count]));
  const result = [];
  for (let date = new Date(start); date < end; date.setUTCDate(date.getUTCDate() + 1)) {
    result.push({ day: date.getUTCDate(), date: date.toISOString().slice(0, 10), count: counts.get(date.toISOString().slice(0, 10)) ?? 0 });
  }
  return result;
});
const maxPlays = computed(() => Math.max(1, ...days.value.map(d => d.count)));
const genreTrends = computed(() => {
  if (!data.value) return [];
  const previous = new Map(data.value.previousMonthGenres.map(g => [g.id, g.playCount]));
  return data.value.recap.topGenres.map(g => ({ ...g, previous: previous.get(g.id) }));
});
async function play(id: string) {
  try { await playTracks([await getTrack(id)], 0); }
  catch { toast.error('Could not play this track'); }
}
</script>

<template>
  <div class="mx-auto max-w-screen-2xl space-y-6 px-6 pb-16 pt-8" :aria-busy="loading">
    <header class="flex flex-wrap items-end justify-between gap-4">
      <h1 class="font-display font-bold tracking-tight text-text" :style="{ fontSize: 'var(--text-display-l)', lineHeight: '1' }">
        Listening insights
      </h1>
      <MonthStepper v-model="month" :max="maxMonth" />
    </header>
    <p v-if="error" role="alert" class="text-sm text-danger">{{ error }} <button class="underline" @click="load">Retry</button></p>
    <div v-if="loading" role="status" class="flex items-center gap-3 py-12 text-muted"><Spinner /> Loading your month…</div>
    <template v-if="data">
      <div class="grid grid-cols-2 gap-4 lg:grid-cols-4">
        <div v-for="tile in [{ label: 'Plays', value: data.recap.totalPlays.toLocaleString() }, { label: 'Estimated hours', value: (data.recap.estimatedMinutes / 60).toLocaleString(undefined, { maximumFractionDigits: 1 }) }, { label: 'Different tracks', value: data.recap.uniqueTracks.toLocaleString() }, { label: 'Different artists', value: data.recap.uniqueArtists.toLocaleString() }]" :key="tile.label" class="rounded-control border border-border bg-surface p-4">
          <p class="label">{{ tile.label }}</p><p class="mt-2 font-mono text-3xl font-semibold tabular-nums text-text">{{ tile.value }}</p>
        </div>
      </div>
      <p class="text-xs text-muted">{{ change }}</p>
      <EmptyState v-if="!data.recap.totalPlays" :icon="BarChart3" title="No listening recorded this month" description="Play some music or choose another month to see your listening patterns." />
      <template v-else>
        <section class="rounded-control border border-border bg-surface p-4">
          <h2 class="font-display text-lg font-semibold text-text">Daily listening</h2><p class="mt-1 text-xs text-muted">Recorded plays per day · hover or focus a bar for details</p>
          <div class="mt-6 flex h-36 items-end gap-1" role="group" aria-label="Daily listening activity">
            <div v-for="day in days" :key="day.date" tabindex="0" :aria-label="`${day.date}: ${day.count} plays`" :title="`${day.date}: ${day.count} plays`" class="min-w-0 flex-1 rounded-t-sm bg-accent/70 transition-colors hover:bg-accent focus:bg-accent focus:outline focus:outline-2 focus:outline-text" :style="{ height: `${Math.max(2, day.count / maxPlays * 100)}%` }" />
          </div>
          <div class="mt-2 flex justify-between font-mono text-[10px] text-muted"><span>1</span><span>{{ days.length }}</span></div>
        </section>
        <div class="grid gap-6 lg:grid-cols-2">
          <section v-for="section in [{ title: 'Top artists', kind: 'artist', entries: data.recap.topArtists }, { title: 'Most revisited albums', kind: 'album', entries: data.recap.topAlbums }]" :key="section.kind" class="rounded-control border border-border bg-surface p-4">
            <h2 class="mb-4 font-display text-lg font-semibold text-text">{{ section.title }}</h2>
            <ol class="space-y-1"><li v-for="(entry, i) in section.entries" :key="entry.id"><RouterLink :to="{ name: section.kind, params: { id: entry.id } }" class="flex items-center gap-3 rounded-control p-2 hover:bg-surface-2"><span class="w-5 font-mono text-xs text-muted">{{ i + 1 }}</span><div class="min-w-0 flex-1"><p class="truncate text-sm font-medium">{{ entry.name }}</p><p v-if="entry.subtitle" class="truncate text-xs text-muted">{{ entry.subtitle }}</p></div><span class="text-xs text-muted">{{ entry.playCount }} plays</span></RouterLink></li></ol>
          </section>
          <section class="rounded-control border border-border bg-surface p-4">
            <h2 class="mb-4 font-display text-lg font-semibold text-text">Top tracks</h2>
            <ol class="space-y-1"><li v-for="entry in data.recap.topTracks" :key="entry.id"><button class="flex w-full items-center gap-3 rounded-control p-2 text-left hover:bg-surface-2" :aria-label="`Play ${entry.name}`" @click="play(entry.id)"><Play :size="15" class="shrink-0 text-muted" /><div class="min-w-0 flex-1"><p class="truncate text-sm font-medium">{{ entry.name }}</p><p class="truncate text-xs text-muted">{{ entry.subtitle }}</p></div><span class="text-xs text-muted">{{ entry.playCount }} plays</span></button></li></ol>
          </section>
          <section class="rounded-control border border-border bg-surface p-4">
            <h2 class="font-display text-lg font-semibold text-text">Genre trends</h2><p class="mb-4 mt-1 text-xs text-muted">Plays this month vs. last month. Tracks can have multiple genres.</p>
            <p v-if="!genreTrends.length" class="text-sm text-muted">No genre tags found for this month's tracks.</p>
            <div v-for="genre in genreTrends" :key="genre.id" class="mb-4">
              <div class="mb-2 flex justify-between gap-3 text-sm"><RouterLink :to="{ name: 'genre', params: { name: genre.name } }" class="hover:underline">{{ genre.name }}</RouterLink><span class="text-xs text-muted">{{ genre.playCount }} <span v-if="genre.previous !== undefined">vs. {{ genre.previous }}</span><span v-else> · outside last month's top 10</span></span></div>
              <div class="h-1.5 overflow-hidden rounded-full bg-surface-2"><div class="h-full rounded-full bg-accent" :style="{ width: `${genre.playCount / Math.max(1, data.recap.topGenres[0]?.playCount ?? 1) * 100}%` }" /></div>
            </div>
          </section>
        </div>
      </template>
      <section class="rounded-control border border-border bg-surface p-4">
        <div class="flex flex-wrap justify-between gap-3"><h2 class="font-display text-lg font-semibold text-text">Explore your library</h2><span class="font-mono text-xl tabular-nums">{{ progress }}%</span></div>
        <p class="mb-4 mt-2 text-sm text-muted">You've heard {{ data.exploredTracks.toLocaleString() }} of {{ data.libraryTracks.toLocaleString() }} tracks, based on your retained listening history across all months.</p>
        <div role="progressbar" aria-label="Library exploration" :aria-valuenow="progress" aria-valuemin="0" aria-valuemax="100" class="h-2 overflow-hidden rounded-full bg-surface-2"><div class="h-full rounded-full bg-accent" :style="{ width: `${progress}%` }" /></div>
        <RouterLink :to="{ name: 'albums' }" class="mt-4 inline-block text-sm underline">Find your next album</RouterLink>
      </section>
    </template>
  </div>
</template>
