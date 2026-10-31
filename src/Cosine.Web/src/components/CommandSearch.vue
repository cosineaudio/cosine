<script setup lang="ts">
import { ref, computed, watch, nextTick } from 'vue';
import { useRouter } from 'vue-router';
import { refDebounced } from '@vueuse/core';
import { DialogRoot, DialogPortal, DialogOverlay, DialogContent, DialogTitle } from 'reka-ui';
import { Search, CornerDownLeft, User, Disc, Music, Clock, ArrowRight } from 'lucide-vue-next';
import { search3 } from '@/api/browsing';
import { usePlayback } from '@/composables/usePlayback';
import { useFavoritesStore } from '@/stores/favorites';
import { useUiStore } from '@/stores/ui';
import CoverArt from '@/components/CoverArt.vue';
import Spinner from '@/components/ui/Spinner.vue';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import type { SearchResults } from '@/api/types';

const RECENT_KEY = 'cosine_recent_searches';
const MAX_RECENT = 6;

const router = useRouter();
const ui = useUiStore();
const favorites = useFavoritesStore();
const { playTracks } = usePlayback();

const query = ref('');
const debounced = refDebounced(query, 200);
const results = ref<SearchResults | null>(null);
const loading = ref(false);
// Typing fires a search per debounce tick; without the gate the palette would
// blink a spinner on every keystroke.
const showSpinner = useDeferredLoading(loading);
const activeIndex = ref(0);
const recent = ref<string[]>([]);
const inputEl = ref<HTMLInputElement | null>(null);
const listEl = ref<HTMLElement | null>(null);

let reqId = 0;

type Item =
  | { kind: 'artist'; id: string; label: string; sub: string; cover: string | null }
  | { kind: 'album'; id: string; label: string; sub: string; cover: string | null }
  | { kind: 'song'; id: string; label: string; sub: string; cover: string | null; index: number }
  | { kind: 'recent'; term: string }
  | { kind: 'search'; term: string };

const items = computed<Item[]>(() => {
  const q = query.value.trim();
  if (!q) {
    return recent.value.map((term) => ({ kind: 'recent', term }));
  }
  const list: Item[] = [{ kind: 'search', term: q }];
  const r = results.value;
  if (r) {
    for (const a of r.artist.slice(0, 4)) {
      list.push({ kind: 'artist', id: a.id, label: a.name, sub: 'Artist', cover: a.coverArt ?? a.id });
    }
    for (const a of r.album.slice(0, 4)) {
      list.push({ kind: 'album', id: a.id, label: a.name, sub: a.artist ?? 'Album', cover: a.coverArt ?? a.id });
    }
    r.song.slice(0, 6).forEach((s, i) =>
      list.push({
        kind: 'song',
        id: s.id,
        label: s.title,
        sub: s.artist ?? 'Song',
        cover: s.coverArt ?? s.albumId ?? s.id,
        index: i,
      }),
    );
  }
  return list;
});

function loadRecent() {
  try {
    recent.value = JSON.parse(localStorage.getItem(RECENT_KEY) ?? '[]').slice(0, MAX_RECENT);
  } catch {
    recent.value = [];
  }
}

function pushRecent(term: string) {
  const t = term.trim();
  if (!t) return;
  const next = [t, ...recent.value.filter((r) => r.toLowerCase() !== t.toLowerCase())].slice(0, MAX_RECENT);
  recent.value = next;
  try {
    localStorage.setItem(RECENT_KEY, JSON.stringify(next));
  } catch { /* ignore */ }
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
    const data = await search3(q, { artistCount: 6, albumCount: 6, songCount: 8 });
    if (id !== reqId) return;
    results.value = data.searchResult3;
    favorites.seed(data.searchResult3);
  } catch {
    if (id === reqId) results.value = { artist: [], album: [], song: [] };
  } finally {
    if (id === reqId) loading.value = false;
  }
}

watch(debounced, (q) => run(q));
watch(items, () => {
  activeIndex.value = 0;
});

// Reset + focus each time the palette opens.
watch(
  () => ui.commandOpen,
  async (open) => {
    if (!open) return;
    query.value = '';
    results.value = null;
    activeIndex.value = 0;
    loadRecent();
    await nextTick();
    inputEl.value?.focus();
  },
);

function close() {
  ui.commandOpen = false;
}

function select(item: Item) {
  switch (item.kind) {
    case 'artist':
      pushRecent(query.value);
      router.push({ name: 'artist', params: { id: item.id } });
      close();
      break;
    case 'album':
      pushRecent(query.value);
      router.push({ name: 'album', params: { id: item.id } });
      close();
      break;
    case 'song': {
      pushRecent(query.value);
      const songs = results.value?.song ?? [];
      playTracks(songs, item.index);
      close();
      break;
    }
    case 'recent':
      query.value = item.term;
      inputEl.value?.focus();
      break;
    case 'search':
      pushRecent(item.term);
      router.push({ name: 'search', query: { q: item.term } });
      close();
      break;
  }
}

function move(delta: number) {
  const n = items.value.length;
  if (n === 0) return;
  activeIndex.value = (activeIndex.value + delta + n) % n;
  scrollActiveIntoView();
}

function scrollActiveIntoView() {
  nextTick(() => {
    const el = listEl.value?.querySelector<HTMLElement>(`[data-idx="${activeIndex.value}"]`);
    el?.scrollIntoView({ block: 'nearest' });
  });
}

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'ArrowDown') {
    e.preventDefault();
    move(1);
  } else if (e.key === 'ArrowUp') {
    e.preventDefault();
    move(-1);
  } else if (e.key === 'Enter') {
    e.preventDefault();
    const item = items.value[activeIndex.value];
    if (item) select(item);
  }
}

const iconFor = { artist: User, album: Disc, song: Music } as const;
</script>

<template>
  <DialogRoot v-model:open="ui.commandOpen">
    <DialogPortal>
      <DialogOverlay class="fixed inset-0 z-[70] bg-black/60" />
      <DialogContent
        class="fixed left-1/2 top-[12vh] z-[70] w-[calc(100%-2rem)] max-w-xl -translate-x-1/2 overflow-hidden rounded-control border border-border bg-surface shadow-2xl focus:outline-none"
        @open-auto-focus.prevent
      >
        <DialogTitle class="sr-only">Command palette</DialogTitle>

        <!-- search field -->
        <div class="flex items-center gap-3 border-b border-border px-4">
          <Search :size="18" :stroke-width="2" class="shrink-0 text-muted" />
          <input
            ref="inputEl"
            v-model="query"
            type="text"
            role="combobox"
            aria-expanded="true"
            aria-controls="command-list"
            :aria-activedescendant="`command-item-${activeIndex}`"
            placeholder="Search artists, albums, songs…"
            class="w-full bg-transparent py-4 text-base text-text placeholder:text-faint focus:outline-none"
            @keydown="onKeydown"
          />
          <Spinner v-if="showSpinner" size="md" class="shrink-0 text-faint" label="Searching" />
        </div>

        <!-- results -->
        <div id="command-list" ref="listEl" role="listbox" class="max-h-[52vh] overflow-y-auto p-2">
          <template v-if="items.length">
            <button
              v-for="(item, i) in items"
              :id="`command-item-${i}`"
              :key="`${item.kind}-${'id' in item ? item.id : item.term}-${i}`"
              :data-idx="i"
              type="button"
              role="option"
              :aria-selected="i === activeIndex"
              class="flex w-full items-center gap-3 rounded-control px-2 py-2 text-left transition-colors"
              :class="i === activeIndex ? 'bg-surface-2' : 'hover:bg-surface-2/60'"
              @click="select(item)"
              @mousemove="activeIndex = i"
            >
              <!-- recent / search actions -->
              <template v-if="item.kind === 'recent'">
                <span class="grid h-9 w-9 shrink-0 place-items-center rounded-control border border-border text-muted">
                  <Clock :size="16" :stroke-width="2" />
                </span>
                <span class="min-w-0 flex-1 truncate text-sm text-text">{{ item.term }}</span>
              </template>
              <template v-else-if="item.kind === 'search'">
                <span class="grid h-9 w-9 shrink-0 place-items-center rounded-control border border-border text-muted">
                  <ArrowRight :size="16" :stroke-width="2" />
                </span>
                <span class="min-w-0 flex-1 truncate text-sm text-text">
                  Search for <span class="font-medium">“{{ item.term }}”</span>
                </span>
              </template>
              <!-- entities -->
              <template v-else>
                <span
                  class="h-9 w-9 shrink-0 overflow-hidden ring-1 ring-border"
                  :class="item.kind === 'artist' ? 'rounded-full' : 'rounded-[3px]'"
                >
                  <CoverArt
                    :cover-art="item.cover"
                    :alt="item.label"
                    :size="72"
                    :shape="item.kind === 'artist' ? 'circle' : 'square'"
                  />
                </span>
                <span class="min-w-0 flex-1">
                  <span class="block truncate text-sm font-medium text-text">{{ item.label }}</span>
                  <span class="flex items-center gap-1 truncate text-xs text-muted">
                    <component :is="iconFor[item.kind]" :size="11" :stroke-width="2" />{{ item.sub }}
                  </span>
                </span>
                <CornerDownLeft
                  v-if="i === activeIndex"
                  :size="14"
                  :stroke-width="2"
                  class="shrink-0 text-faint"
                />
              </template>
            </button>
          </template>

          <p v-else-if="query.trim() && !loading" class="px-2 py-10 text-center text-sm text-muted">
            No results for “{{ query }}”.
          </p>
          <p v-else class="px-2 py-10 text-center text-sm text-muted">
            Type to search your library.
          </p>
        </div>

        <!-- footer hints -->
        <div class="flex items-center gap-4 border-t border-border px-4 py-2 font-mono text-[11px] text-faint">
          <span class="flex items-center gap-1"><kbd class="rounded-[3px] border border-border px-1">↑↓</kbd> navigate</span>
          <span class="flex items-center gap-1"><kbd class="rounded-[3px] border border-border px-1">↵</kbd> open</span>
          <span class="flex items-center gap-1"><kbd class="rounded-[3px] border border-border px-1">esc</kbd> close</span>
        </div>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
