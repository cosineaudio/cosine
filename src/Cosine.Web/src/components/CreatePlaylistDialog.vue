<script setup lang="ts">
import { ref, watch, nextTick } from 'vue';
import {
  DialogRoot, DialogPortal, DialogOverlay, DialogContent, DialogTitle, DialogDescription, DialogClose,
} from 'reka-ui';
import { X } from 'lucide-vue-next';
import { useUiStore } from '@/stores/ui';
import { usePlaylistsStore } from '@/stores/playlists';
import Spinner from '@/components/ui/Spinner.vue';

const ui = useUiStore();
const playlists = usePlaylistsStore();

const name = ref('');
const submitting = ref(false);
const input = ref<HTMLInputElement | null>(null);

// Reset + focus whenever the dialog opens.
watch(
  () => ui.createPlaylistOpen,
  async (open) => {
    if (open) {
      name.value = '';
      submitting.value = false;
      await nextTick();
      input.value?.focus();
    }
  },
);

async function submit() {
  const trimmed = name.value.trim();
  if (!trimmed || submitting.value) return;
  submitting.value = true;
  await playlists.create(trimmed, ui.pendingSongIds);
  ui.closeCreatePlaylist();
}
</script>

<template>
  <DialogRoot :open="ui.createPlaylistOpen" @update:open="(v) => !v && ui.closeCreatePlaylist()">
    <DialogPortal>
      <DialogOverlay class="fixed inset-0 z-[60] bg-black/60" />
      <DialogContent
        class="fixed left-1/2 top-1/2 z-[60] w-[calc(100%-2rem)] max-w-sm -translate-x-1/2 -translate-y-1/2 rounded-control border border-border bg-surface p-5 shadow-2xl focus:outline-none"
        @open-auto-focus.prevent
      >
        <div class="mb-4 flex items-center justify-between">
          <DialogTitle class="font-display text-lg font-semibold text-text">New playlist</DialogTitle>
          <DialogClose
            class="grid h-8 w-8 place-items-center rounded-control text-muted transition-colors hover:text-text"
            aria-label="Close"
          >
            <X :size="18" :stroke-width="2" />
          </DialogClose>
        </div>
        <DialogDescription v-if="ui.pendingSongIds.length" class="mb-3 text-sm text-muted">
          {{ ui.pendingSongIds.length }} track{{ ui.pendingSongIds.length === 1 ? '' : 's' }} will be added.
        </DialogDescription>
        <form @submit.prevent="submit">
          <input
            ref="input"
            v-model="name"
            type="text"
            placeholder="Playlist name"
            maxlength="120"
            class="w-full rounded-control border border-border bg-surface-2 px-3 py-2.5 text-sm text-text outline-none transition-colors placeholder:text-faint focus:border-faint"
          />
          <div class="mt-4 flex justify-end gap-2">
            <button
              type="button"
              class="rounded-control border border-border px-4 py-2 text-sm font-medium text-muted transition-colors hover:text-text"
              @click="ui.closeCreatePlaylist()"
            >
              Cancel
            </button>
            <button
              type="submit"
              class="flex items-center gap-2 rounded-control bg-accent px-4 py-2 text-sm font-medium text-accent-ink transition-transform hover:scale-[1.02] active:scale-95 disabled:opacity-40"
              :disabled="!name.trim() || submitting"
            >
              <Spinner v-if="submitting" />Create
            </button>
          </div>
        </form>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
