<script setup lang="ts">
import {
  DropdownMenuRoot, DropdownMenuTrigger, DropdownMenuPortal, DropdownMenuContent,
  DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuSeparator,
} from 'reka-ui';
import { AudioLines } from 'lucide-vue-next';
import { visualizers } from '@/visualizers/registry';

const selection = defineModel<string>({ required: true });
const rowClass = 'flex cursor-pointer select-none items-center rounded-[3px] px-2.5 py-2 outline-none data-[state=checked]:bg-surface-2 data-[state=checked]:font-medium data-[highlighted]:bg-surface-2';
</script>

<template>
  <DropdownMenuRoot>
    <DropdownMenuTrigger as-child>
      <button
        type="button"
        class="justify-self-end text-white transition-opacity hover:opacity-100"
        :class="selection === 'off' ? 'opacity-50' : 'opacity-100'"
        aria-label="Choose visualizer"
      >
        <AudioLines :size="18" :stroke-width="2" />
      </button>
    </DropdownMenuTrigger>
    <DropdownMenuPortal>
      <DropdownMenuContent
        side="top" align="end" :side-offset="6" :collision-padding="12"
        class="z-[100] max-h-[var(--reka-dropdown-menu-content-available-height)] min-w-52 overflow-y-auto rounded-control border border-border bg-surface p-1 text-sm text-text shadow-xl shadow-black/40 focus:outline-none"
        aria-label="Visualizer"
      >
        <DropdownMenuRadioGroup v-model="selection">
          <DropdownMenuRadioItem value="off" :class="rowClass">Off</DropdownMenuRadioItem>
          <DropdownMenuSeparator class="my-1 h-px bg-border" />
          <DropdownMenuRadioItem v-for="item in visualizers" :key="item.id" :value="item.id" :class="rowClass">
            {{ item.name }}
          </DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenuPortal>
  </DropdownMenuRoot>
</template>
