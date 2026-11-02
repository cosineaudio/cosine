<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { ShieldCheck, RefreshCw, ArrowUpRight } from 'lucide-vue-next';
import { getLibraryHealth, type LibraryHealthReport } from '@/api/insights';
import Spinner from '@/components/ui/Spinner.vue';

const report = ref<LibraryHealthReport | null>(null);
const loading = ref(false);
const error = ref('');
const selected = ref('artwork');
const category = computed(() => report.value?.categories.find(c => c.key === selected.value));
async function load() {
  loading.value = true;
  error.value = '';
  try { report.value = await getLibraryHealth(); }
  catch (e) { error.value = e instanceof Error ? e.message : 'Could not check library health'; }
  finally { loading.value = false; }
}
onMounted(load);
</script>

<template>
  <div class="mx-auto max-w-5xl space-y-6 p-6" :aria-busy="loading">
    <header class="flex flex-wrap items-center justify-between gap-4">
      <h1 class="font-display text-2xl font-bold tracking-tight text-text">Library health</h1>
      <button :disabled="loading" class="flex items-center gap-2 rounded-control border border-border bg-surface px-4 py-2 text-sm font-medium text-text transition-transform hover:scale-[1.02] active:scale-95 disabled:cursor-not-allowed disabled:opacity-50" @click="load">
        <RefreshCw :size="16" :class="{ 'animate-spin': loading }" /> {{ loading ? 'Checking…' : 'Check again' }}
      </button>
    </header>
    <p v-if="error" role="alert" class="rounded-control border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger">{{ error }} <button class="underline" @click="load">Retry</button></p>
    <div v-if="loading && !report" role="status" class="flex items-center gap-3 py-12 text-muted"><Spinner /> Checking metadata and file availability…</div>
    <template v-if="report">
      <div class="flex flex-wrap items-center gap-x-6 gap-y-2 text-sm text-muted">
        <span>{{ report.totalTracks.toLocaleString() }} tracks</span><span>{{ report.totalAlbums.toLocaleString() }} albums</span>
        <span>Checked {{ new Date(report.checkedAt).toLocaleString() }}</span>
      </div>
      <p v-if="!report.totalTracks" class="rounded-control border border-border bg-surface p-5 text-sm text-muted">Your library is empty. <RouterLink to="/admin/scanner" class="text-text underline">Run a library scan</RouterLink> to populate this report.</p>
      <p v-if="!report.mediaRootAvailable" role="status" class="rounded-control border border-border bg-surface p-5 text-sm">The media root is unavailable. File checks were skipped; restore the mount or configuration and check again.</p>
      <div class="grid grid-cols-2 gap-4 lg:grid-cols-3 xl:grid-cols-5">
        <button v-for="item in report.categories" :key="item.key" :aria-pressed="selected === item.key" class="rounded-control border bg-surface p-4 text-left transition-colors hover:bg-surface-2" :class="selected === item.key ? 'border-accent' : 'border-border'" @click="selected = item.key">
          <p class="label min-h-6">{{ item.title }}</p>
          <p class="mt-2 font-mono text-3xl font-semibold tabular-nums text-text">{{ item.key === 'files' && !report.mediaRootAvailable ? '—' : item.count.toLocaleString() }}</p>
        </button>
      </div>
      <section v-if="category" class="overflow-hidden rounded-control border border-border bg-surface">
        <div class="space-y-1 border-b border-border bg-surface-2/40 p-4">
          <h2 class="font-display text-lg font-semibold text-text">{{ category.title }}</h2>
          <p class="text-sm text-muted">{{ category.description }}</p>
        </div>
        <div v-if="category.key === 'files' && !report.mediaRootAvailable" class="p-4 text-sm text-muted">File availability could not be checked.</div>
        <div v-else-if="!category.count" class="flex items-center gap-3 p-8 text-muted"><ShieldCheck :size="24" /><span>No issues found in this category.</span></div>
        <template v-else>
          <p class="px-4 pt-4 text-xs text-muted">Showing {{ category.items.length }} of {{ category.count.toLocaleString() }} {{ category.key === 'duplicates' || category.key === 'artists' ? 'groups' : 'items' }}.</p>
          <ul class="divide-y divide-border p-1">
            <li v-for="(item, index) in category.items" :key="`${item.id}-${index}`">
              <RouterLink :to="{ name: item.kind, params: { id: item.id } }" class="flex items-center gap-4 rounded-control p-3 hover:bg-surface-2">
                <div class="min-w-0 flex-1"><p class="font-medium">{{ item.name || 'Untitled' }}</p><p class="mt-1 break-words text-sm text-muted">{{ item.detail }}</p></div>
                <ArrowUpRight :size="16" class="shrink-0 text-muted" />
              </RouterLink>
            </li>
          </ul>
        </template>
      </section>
    </template>
  </div>
</template>
