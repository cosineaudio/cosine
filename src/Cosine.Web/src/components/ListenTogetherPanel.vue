<script setup lang="ts">
import { ref } from 'vue';
import {
  PopoverRoot, PopoverTrigger, PopoverPortal, PopoverContent,
} from 'reka-ui';
import { Copy, Check, Crown, Users } from 'lucide-vue-next';
import { toast } from 'vue-sonner';
import { usePartyStore } from '@/stores/party';
import Spinner from '@/components/ui/Spinner.vue';

const party = usePartyStore();

const copied = ref(false);

async function start() {
  try {
    await party.start();
    toast.success('Listening party started - share the link');
  } catch {
    toast.error('Could not start the party');
  }
}

async function end() {
  try {
    await party.end();
    toast('Listening party ended');
  } catch {
    toast.error('Could not end the party');
  }
}

async function copyLink() {
  if (!party.shareLink) return;
  try {
    await navigator.clipboard.writeText(party.shareLink);
    copied.value = true;
    setTimeout(() => { copied.value = false; }, 2000);
  } catch {
    toast.error('Could not copy - copy the link from the box instead');
  }
}
</script>

<template>
  <PopoverRoot>
    <PopoverTrigger as-child>
      <button
        type="button"
        class="grid h-9 w-9 place-items-center rounded-control transition-colors"
        :class="party.isHosting ? 'text-accent' : 'text-muted hover:text-text'"
        aria-label="Listen together"
        :title="party.isHosting ? 'Hosting a listening party' : 'Listen together'"
      >
        <Users :size="18" :stroke-width="2" />
      </button>
    </PopoverTrigger>

    <PopoverPortal>
      <PopoverContent
        :side-offset="6"
        align="end"
        class="z-50 w-80 rounded-control border border-border bg-surface p-3 text-sm text-text shadow-xl shadow-black/40 focus:outline-none"
      >
        <div class="mb-2 flex items-center justify-between">
          <span class="label">Listen together</span>
          <span
            v-if="party.isHosting"
            class="flex items-center gap-1.5 font-mono text-[10px] uppercase tracking-widest text-accent"
          >
            <span class="h-1.5 w-1.5 animate-pulse rounded-full bg-accent" />
            live
          </span>
        </div>

        <template v-if="!party.isHosting">
          <p class="mb-3 text-xs leading-relaxed text-muted">
            Start a party and share the link. Anyone with it can listen along and add songs to
            the queue - no account needed. You keep the controls.
          </p>
          <button
            type="button"
            class="flex w-full items-center justify-center gap-2 rounded-control bg-accent px-3 py-2 text-sm font-medium text-accent-ink transition-transform hover:scale-[1.01] active:scale-[0.99] disabled:opacity-50"
            :disabled="party.starting"
            @click="start"
          >
            <Spinner v-if="party.starting" size="sm" />
            <span>Start listening party</span>
          </button>
        </template>

        <template v-else>
          <div class="mb-3 flex items-center gap-1.5">
            <input
              readonly
              :value="party.shareLink"
              class="min-w-0 flex-1 rounded-control border border-border bg-surface-2 px-2 py-1.5 font-mono text-[11px] text-muted focus:outline-none"
              @focus="($event.target as HTMLInputElement).select()"
            />
            <button
              type="button"
              class="grid h-8 w-8 shrink-0 place-items-center rounded-control border border-border text-muted transition hover:text-text"
              aria-label="Copy link"
              @click="copyLink"
            >
              <Check v-if="copied" :size="15" :stroke-width="2.5" class="text-accent" />
              <Copy v-else :size="15" :stroke-width="2" />
            </button>
          </div>

          <div class="label mb-1">In the party</div>
          <ul class="mb-3 max-h-40 space-y-1 overflow-y-auto">
            <li
              v-for="member in party.members"
              :key="member.id"
              class="flex items-center gap-2 rounded-control px-1 py-0.5"
            >
              <Crown v-if="member.isOwner" :size="13" :stroke-width="2" class="shrink-0 text-accent" />
              <span v-else class="inline-block h-1.5 w-1.5 shrink-0 rounded-full bg-muted" />
              <span class="truncate text-xs" :class="member.isOwner ? 'text-text' : 'text-muted'">
                {{ member.displayName }}
              </span>
            </li>
          </ul>

          <button
            type="button"
            class="w-full rounded-control border border-border px-3 py-1.5 text-xs text-muted transition hover:border-faint hover:text-text"
            @click="end"
          >
            End party
          </button>
        </template>
      </PopoverContent>
    </PopoverPortal>
  </PopoverRoot>
</template>
