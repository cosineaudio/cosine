<script setup lang="ts">
import {
  DialogRoot, DialogPortal, DialogOverlay, DialogContent, DialogTitle, DialogClose,
} from 'reka-ui';
import { X } from 'lucide-vue-next';
import { useUiStore } from '@/stores/ui';
import { SHORTCUTS } from '@/composables/useKeyboardShortcuts';

const ui = useUiStore();
</script>

<template>
  <DialogRoot v-model:open="ui.shortcutsOpen">
    <DialogPortal>
      <DialogOverlay class="fixed inset-0 z-[60] bg-black/60 data-[state=open]:animate-in" />
      <DialogContent
        class="fixed left-1/2 top-1/2 z-[60] w-[calc(100%-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-control border border-border bg-surface p-5 shadow-2xl focus:outline-none"
      >
        <div class="mb-4 flex items-center justify-between">
          <DialogTitle class="font-display text-lg font-semibold text-text">Keyboard shortcuts</DialogTitle>
          <DialogClose
            class="grid h-8 w-8 place-items-center rounded-control text-muted transition-colors hover:text-text"
            aria-label="Close"
          >
            <X :size="18" :stroke-width="2" />
          </DialogClose>
        </div>
        <ul class="flex flex-col divide-y divide-border">
          <li v-for="(s, i) in SHORTCUTS" :key="i" class="flex items-center justify-between py-2">
            <span class="text-sm text-muted">{{ s.label }}</span>
            <span class="flex items-center gap-1">
              <kbd
                v-for="(k, ki) in s.keys"
                :key="ki"
                class="min-w-6 rounded-[3px] border border-border bg-surface-2 px-1.5 py-0.5 text-center font-mono text-[11px] text-text"
              >{{ k }}</kbd>
            </span>
          </li>
        </ul>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
