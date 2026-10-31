<script setup lang="ts">
import { ref, computed, onMounted, watch, nextTick } from 'vue';
import { useRouter } from 'vue-router';
import { toast } from 'vue-sonner';
import { Play, Shuffle, Pencil, Trash2, MoreHorizontal, Check, X, GripVertical } from 'lucide-vue-next';
import {
  getPlaylist,
  removePlaylistEntry,
  replacePlaylistTracks,
  type PlaylistDetail,
  type PlaylistEntry,
} from '@/api/playlists';
import { usePlaylistsStore } from '@/stores/playlists';
import { usePlayback } from '@/composables/usePlayback';
import PageHeader from '@/components/ui/PageHeader.vue';
import TrackRow from '@/components/TrackRow.vue';
import ContextMenu, { type MenuItem } from '@/components/ui/ContextMenu.vue';
import EmptyState from '@/components/ui/EmptyState.vue';
import SkeletonDetailHeader from '@/components/ui/SkeletonDetailHeader.vue';
import SkeletonTrackList from '@/components/ui/SkeletonTrackList.vue';
import { useDominantColor } from '@/composables/useDominantColor';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import { countLabel, formatTotalTime } from '@/lib/format';
import type { Track } from '@/api/types';

const props = defineProps<{ id: string }>();

const router = useRouter();
const playlists = usePlaylistsStore();
const { playTracks } = usePlayback();

const playlist = ref<PlaylistDetail | null>(null);
const entries = ref<PlaylistEntry[]>([]);
const tracks = computed<Track[]>(() => entries.value.map((e) => e.track));
const loading = ref(true);
const showSkeleton = useDeferredLoading(loading);

const editing = ref(false);
const draftName = ref('');
const nameInput = ref<HTMLInputElement | null>(null);

const totalDuration = computed(() => tracks.value.reduce((s, t) => s + (t.duration ?? 0), 0));
const { tint } = useDominantColor(() => (playlist.value ? playlist.value.coverArt ?? playlist.value.id : null));

// --- drag reorder (mirrors QueuePanel) ---
const dragIndex = ref<number | null>(null);
const overIndex = ref<number | null>(null);

function onDragStart(i: number, e: DragEvent) {
  dragIndex.value = i;
  if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move';
}
function onDragOver(i: number, e: DragEvent) {
  e.preventDefault();
  overIndex.value = i;
  if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
}
function onDrop(i: number) {
  const from = dragIndex.value;
  dragIndex.value = null;
  overIndex.value = null;
  if (from === null || from === i) return;
  const next = [...entries.value];
  const [moved] = next.splice(from, 1);
  next.splice(i, 0, moved);
  entries.value = next;
  persistOrder();
}
function onDragEnd() {
  dragIndex.value = null;
  overIndex.value = null;
}

/**
 * Persist an arbitrary reorder: there is no move primitive, so replace the contents
 * with the new order in one go.
 */
async function persistOrder() {
  try {
    await replacePlaylistTracks(props.id, entries.value.map((e) => e.track.id));
  } catch {
    toast.error('Could not save the new order');
    load(); // resync from server
  }
}

async function removeAt(i: number) {
  const removed = entries.value[i];
  entries.value = entries.value.filter((_, idx) => idx !== i);
  try {
    // Removal names the entry itself - a concurrent edit elsewhere cannot shift
    // which row this deletes, unlike the old position-index API.
    await removePlaylistEntry(props.id, removed.entryId);
    toast.success(`Removed “${removed.track.title}”`);
  } catch {
    toast.error('Could not remove track');
    load();
  }
}

function play(shuffle = false) {
  if (!tracks.value.length) return;
  const list = shuffle ? [...tracks.value].sort(() => Math.random() - 0.5) : tracks.value;
  playTracks(list, 0);
}

async function startRename() {
  draftName.value = playlist.value?.name ?? '';
  editing.value = true;
  await nextTick();
  nameInput.value?.focus();
  nameInput.value?.select();
}
async function saveRename() {
  const trimmed = draftName.value.trim();
  editing.value = false;
  if (!trimmed || !playlist.value || trimmed === playlist.value.name) return;
  await playlists.rename(props.id, trimmed);
  playlist.value.name = trimmed;
}

async function destroy() {
  if (!playlist.value) return;
  await playlists.remove(props.id, playlist.value.name);
  router.replace({ name: 'playlists' });
}

const menuItems = computed<MenuItem[]>(() => [
  { label: 'Rename', icon: Pencil, onSelect: startRename },
  { label: 'Delete', icon: Trash2, danger: true, onSelect: destroy },
]);

async function load() {
  loading.value = true;
  try {
    playlist.value = await getPlaylist(props.id);
    entries.value = [...playlist.value.entries];
  } catch {
    playlist.value = null;
  } finally {
    loading.value = false;
  }
}

onMounted(load);
watch(() => props.id, load);
</script>

<template>
  <div v-if="showSkeleton">
    <SkeletonDetailHeader :actions="3" />
    <div class="mx-auto max-w-screen-2xl px-6 pb-16 pt-4">
      <SkeletonTrackList :count="10" show-art />
    </div>
  </div>

  <!-- blank until the skeleton's delay elapses; see useDeferredLoading -->
  <template v-else-if="loading" />

  <div v-else-if="playlist" class="reveal">
    <ContextMenu :items="menuItems" context-menu>
    <PageHeader kicker="Playlist" :title="playlist.name" :cover-art="playlist.coverArt ?? playlist.id" :tint="tint">
      <template #meta>
        <span v-if="playlist.owner">{{ playlist.owner }}</span>
        <span>· {{ countLabel(tracks.length, 'track') }}</span>
        <span>· {{ formatTotalTime(totalDuration) }}</span>
      </template>
      <template #actions>
        <button
          type="button"
          class="flex items-center gap-2 rounded-control bg-accent px-5 py-2.5 font-medium text-accent-ink transition-transform hover:scale-[1.02] active:scale-95 disabled:opacity-40"
          :disabled="!tracks.length"
          @click="play(false)"
        >
          <Play :size="18" :stroke-width="2" fill="currentColor" /> Play
        </button>
        <button
          type="button"
          class="flex items-center gap-2 rounded-control border border-border px-4 py-2.5 font-medium text-text transition-colors hover:bg-surface-2 disabled:opacity-40"
          :disabled="!tracks.length"
          @click="play(true)"
        >
          <Shuffle :size="18" :stroke-width="2" /> Shuffle
        </button>
        <ContextMenu :items="menuItems">
          <button
            type="button"
            class="grid h-10 w-10 place-items-center rounded-control border border-border text-muted transition-colors hover:bg-surface-2 hover:text-text"
            aria-label="More options"
          >
            <MoreHorizontal :size="20" :stroke-width="2" />
          </button>
        </ContextMenu>
      </template>
    </PageHeader>
    </ContextMenu>

    <div class="mx-auto max-w-screen-2xl px-6 pb-16 pt-4">
      <!-- inline rename -->
      <div v-if="editing" class="mb-4 flex items-center gap-2">
        <input
          ref="nameInput"
          v-model="draftName"
          type="text"
          maxlength="120"
          class="w-full max-w-sm rounded-control border border-border bg-surface-2 px-3 py-2 text-sm text-text outline-none focus:border-faint"
          @keydown.enter="saveRename"
          @keydown.esc="editing = false"
        />
        <button type="button" class="grid h-9 w-9 place-items-center rounded-control bg-accent text-accent-ink" aria-label="Save name" @click="saveRename">
          <Check :size="18" :stroke-width="2" />
        </button>
        <button type="button" class="grid h-9 w-9 place-items-center rounded-control border border-border text-muted hover:text-text" aria-label="Cancel" @click="editing = false">
          <X :size="18" :stroke-width="2" />
        </button>
      </div>

      <div v-if="tracks.length" class="flex flex-col">
        <div
          v-for="(track, i) in tracks"
          :key="`${track.id}-${i}`"
          draggable="true"
          class="group/plrow flex items-center gap-1 rounded-control transition-colors"
          :class="[
            overIndex === i && dragIndex !== null ? 'ring-1 ring-accent/60' : '',
            dragIndex === i ? 'opacity-40' : '',
          ]"
          @dragstart="onDragStart(i, $event)"
          @dragover="onDragOver(i, $event)"
          @drop="onDrop(i)"
          @dragend="onDragEnd"
        >
          <span class="grid w-5 shrink-0 cursor-grab place-items-center text-faint opacity-0 transition-opacity group-hover/plrow:opacity-100 active:cursor-grabbing">
            <GripVertical :size="16" :stroke-width="2" />
          </span>
          <div class="min-w-0 flex-1">
            <TrackRow
              :track="track"
              :queue="tracks"
              :index="i + 1"
              show-art
              show-album
              :menu-extra="[{ label: 'Remove from playlist', icon: Trash2, danger: true, onSelect: () => removeAt(i) }]"
            />
          </div>
        </div>
      </div>

      <EmptyState
        v-else
        :icon="Trash2"
        title="This playlist is empty"
        description="Add tracks from any album, artist, or search result via the ⋯ menu."
      />
    </div>
  </div>

  <div v-else class="mx-auto max-w-screen-2xl px-6 py-20 text-center text-muted">Playlist not found.</div>
</template>
