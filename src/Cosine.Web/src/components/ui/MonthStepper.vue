<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { PopoverRoot, PopoverTrigger, PopoverPortal, PopoverContent } from 'reka-ui';
import { CalendarDays, ChevronDown, ChevronLeft, ChevronRight } from 'lucide-vue-next';

const month = defineModel<string>({ required: true });
const props = withDefaults(defineProps<{ min?: string; max: string }>(), { min: '1970-01' });
const open = ref(false);
const pickerYear = ref(Number(month.value.slice(0, 4)));
const label = computed(() => new Intl.DateTimeFormat(undefined, { month: 'long', year: 'numeric', timeZone: 'UTC' })
  .format(new Date(`${month.value}-01T00:00:00Z`)));
const months = Array.from({ length: 12 }, (_, i) => ({
  number: String(i + 1).padStart(2, '0'),
  label: new Intl.DateTimeFormat(undefined, { month: 'short', timeZone: 'UTC' }).format(new Date(Date.UTC(2000, i, 1))),
  full: new Intl.DateTimeFormat(undefined, { month: 'long', timeZone: 'UTC' }).format(new Date(Date.UTC(2000, i, 1))),
}));
watch(open, value => { if (value) pickerYear.value = Number(month.value.slice(0, 4)); });

function step(direction: number) {
  const date = new Date(`${month.value}-01T00:00:00Z`);
  date.setUTCMonth(date.getUTCMonth() + direction);
  select(date.toISOString().slice(0, 7));
}
function select(value: string) {
  if (value < props.min || value > props.max) return;
  month.value = value;
  open.value = false;
}
</script>

<template>
  <div class="flex items-center rounded-control border border-border bg-surface" role="group" aria-label="Listening month">
    <button type="button" aria-label="Previous month" :disabled="month <= min" class="grid h-10 w-10 shrink-0 place-items-center rounded-l-control text-muted transition-colors hover:bg-surface-2 hover:text-text disabled:pointer-events-none disabled:opacity-25" @click="step(-1)">
      <ChevronLeft :size="17" :stroke-width="1.75" />
    </button>
    <PopoverRoot v-model:open="open">
      <PopoverTrigger as-child>
        <button type="button" :aria-label="`Choose month, ${label}`" class="flex h-10 min-w-44 items-center justify-center gap-2.5 border-x border-border px-3 text-sm font-medium text-text transition-colors hover:bg-surface-2">
          <CalendarDays :size="15" :stroke-width="1.75" class="text-muted" />
          <span class="flex-1 text-center" aria-live="polite">{{ label }}</span>
          <ChevronDown :size="13" :stroke-width="1.75" class="text-muted transition-transform" :class="{ 'rotate-180': open }" />
        </button>
      </PopoverTrigger>
      <PopoverPortal>
        <PopoverContent align="end" :side-offset="8" :collision-padding="16" aria-label="Choose listening month" class="z-50 w-72 max-w-[calc(100vw-2rem)] rounded-control border border-border bg-surface p-3 text-text shadow-xl shadow-black/40 focus:outline-none">
          <div class="mb-3 flex items-center justify-between">
            <button type="button" aria-label="Previous year" :disabled="pickerYear <= Number(min.slice(0, 4))" class="grid h-8 w-8 place-items-center rounded-control text-muted hover:bg-surface-2 hover:text-text disabled:pointer-events-none disabled:opacity-25" @click="pickerYear--"><ChevronLeft :size="16" /></button>
            <span class="font-display text-lg font-semibold tabular-nums" aria-live="polite">{{ pickerYear }}</span>
            <button type="button" aria-label="Next year" :disabled="pickerYear >= Number(max.slice(0, 4))" class="grid h-8 w-8 place-items-center rounded-control text-muted hover:bg-surface-2 hover:text-text disabled:pointer-events-none disabled:opacity-25" @click="pickerYear++"><ChevronRight :size="16" /></button>
          </div>
          <div class="grid grid-cols-3 gap-1.5" role="group" :aria-label="`Months in ${pickerYear}`">
            <button v-for="item in months" :key="item.number" type="button" :aria-label="`${item.full} ${pickerYear}`" :aria-pressed="month === `${pickerYear}-${item.number}`" :disabled="`${pickerYear}-${item.number}` < min || `${pickerYear}-${item.number}` > max" class="rounded-control py-2.5 text-sm transition-colors disabled:pointer-events-none disabled:opacity-25" :class="month === `${pickerYear}-${item.number}` ? 'bg-accent font-semibold text-accent-ink' : 'text-muted hover:bg-surface-2 hover:text-text'" @click="select(`${pickerYear}-${item.number}`)">{{ item.label }}</button>
          </div>
          <div class="mt-3 flex items-center justify-between border-t border-border pt-3">
            <span class="font-mono text-[10px] uppercase tracking-wider text-faint">UTC</span>
            <button type="button" class="rounded-control px-2 py-1 text-xs font-medium text-muted transition-colors hover:bg-surface-2 hover:text-text" @click="select(max)">This month</button>
          </div>
        </PopoverContent>
      </PopoverPortal>
    </PopoverRoot>
    <button type="button" aria-label="Next month" :disabled="month >= max" class="grid h-10 w-10 shrink-0 place-items-center rounded-r-control text-muted transition-colors hover:bg-surface-2 hover:text-text disabled:pointer-events-none disabled:opacity-25" @click="step(1)">
      <ChevronRight :size="17" :stroke-width="1.75" />
    </button>
  </div>
</template>
