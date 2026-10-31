<script setup lang="ts">
import { computed, ref } from 'vue';
import {
  DropdownMenuRoot, DropdownMenuTrigger, DropdownMenuPortal,
  DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator,
} from 'reka-ui';
import { Cast, Check, Laptop, Speaker } from 'lucide-vue-next';
import { toast } from 'vue-sonner';
import { usePlaybackStore } from '@/stores/playback';
import { useAuthStore } from '@/stores/auth';
import Spinner from '@/components/ui/Spinner.vue';
import type { PlaybackDevice } from '@/api/playback';

const props = withDefaults(defineProps<{ size?: 'sm' | 'md' }>(), { size: 'sm' });

const playback = usePlaybackStore();
const auth = useAuthStore();

const open = ref(false);
const switching = ref(false);

const iconSize = computed(() => (props.size === 'md' ? 20 : 18));

/**
 * The popover never waits on the network. Persisted rows render the moment it opens and live
 * discovery fills in around them, because SSDP's `M-SEARCH` has a multi-second wait built into
 * the protocol (plan §4.8 item 4).
 */
function onOpenChange(value: boolean) {
  open.value = value;
  if (value) void playback.loadDevices();
}

function subtitle(device: PlaybackDevice): string {
  if (!device.enabled) return 'Disabled';
  if (!device.online) return 'Offline';
  if (device.activeSessionId && device.id !== currentDeviceId.value) {
    return `In use by ${device.activeSessionOwner ?? 'someone else'}`;
  }
  return device.driverId.toUpperCase();
}

const currentDeviceId = computed(() => playback.outputDeviceId);

/**
 * A device held by someone else is still selectable for an admin - they can take it, which is the
 * point of having an owner at all. For everyone else it is listed and inert, so "who has it" is
 * visible rather than a mysterious error.
 */
function selectable(device: PlaybackDevice): boolean {
  if (!device.enabled || !device.online) return false;
  if (!device.activeSessionId || device.id === currentDeviceId.value) return true;
  return auth.isAdmin;
}

/**
 * Handing playback to a speaker is a network handshake, not a local toggle, and
 * picking an item closes the menu straight away - so the row that was clicked is
 * gone before the request returns. Feedback goes two places that survive the
 * close: a toast that starts as "Switching…" and is replaced in place by the
 * outcome, and a spinner on the trigger button, which stays on screen.
 */
async function pick(device: PlaybackDevice) {
  if (!selectable(device) || switching.value) return;
  const takeOver = !!device.activeSessionId && device.id !== currentDeviceId.value;
  switching.value = true;
  const id = toast.loading(`Switching to ${device.displayName}…`);
  try {
    await playback.selectDevice(device, takeOver);
    toast.success(`Playing on ${device.displayName}`, { id });
  } catch (err: unknown) {
    toast.error(err instanceof Error ? err.message : `Could not play on ${device.displayName}`, { id });
  } finally {
    switching.value = false;
  }
}

async function pickLocal() {
  if (!playback.isRemote || switching.value) return;
  switching.value = true;
  const id = toast.loading('Switching to this browser…');
  try {
    await playback.selectLocal();
    toast.success('Playing in this browser', { id });
  } catch (err: unknown) {
    toast.error(err instanceof Error ? err.message : 'Could not switch back to this browser', { id });
  } finally {
    switching.value = false;
  }
}
</script>

<template>
  <DropdownMenuRoot :open="open" @update:open="onOpenChange">
    <DropdownMenuTrigger as-child>
      <button
        type="button"
        class="grid h-9 w-9 place-items-center rounded-control transition-colors"
        :class="playback.isRemote ? 'text-accent' : 'text-muted hover:text-text'"
        :aria-label="`Output: ${playback.outputName}`"
        :title="`Output: ${playback.outputName}`"
      >
        <Spinner v-if="switching" :size="size === 'md' ? 'md' : 'sm'" />
        <Cast v-else :size="iconSize" :stroke-width="2" />
      </button>
    </DropdownMenuTrigger>

    <DropdownMenuPortal>
      <DropdownMenuContent
        :side-offset="6"
        align="end"
        class="z-50 min-w-64 rounded-control border border-border bg-surface p-1 text-sm text-text shadow-xl shadow-black/40 focus:outline-none"
      >
        <div class="label px-2 py-1.5">Play on</div>

        <DropdownMenuItem
          class="flex cursor-pointer items-center gap-2.5 rounded-control px-2 py-2 outline-none data-[highlighted]:bg-surface-2"
          @select="pickLocal"
        >
          <Laptop :size="16" :stroke-width="2" class="shrink-0 text-muted" />
          <span class="min-w-0 flex-1">
            <span class="block truncate">This browser</span>
          </span>
          <Check v-if="!playback.isRemote" :size="15" :stroke-width="2.5" class="shrink-0 text-accent" />
        </DropdownMenuItem>

        <DropdownMenuSeparator class="my-1 h-px bg-border" />

        <!-- SSDP's M-SEARCH has a multi-second wait built into the protocol, so
             this one is genuinely slow and says so rather than hiding it -->
        <p v-if="playback.devices.length === 0" class="flex items-center gap-2 px-2 py-3 text-xs text-muted">
          <Spinner v-if="playback.devicesLoading" />
          {{ playback.devicesLoading ? 'Looking for speakers…' : 'No speakers found on the network.' }}
        </p>

        <DropdownMenuItem
          v-for="device in playback.devices"
          :key="device.id"
          :disabled="!selectable(device)"
          class="flex cursor-pointer items-center gap-2.5 rounded-control px-2 py-2 outline-none data-[highlighted]:bg-surface-2 data-[disabled]:cursor-default data-[disabled]:opacity-40"
          @select="pick(device)"
        >
          <Speaker :size="16" :stroke-width="2" class="shrink-0 text-muted" />
          <span class="min-w-0 flex-1">
            <span class="block truncate">{{ device.displayName }}</span>
            <span class="block truncate font-mono text-[10px] text-muted">{{ subtitle(device) }}</span>
          </span>
          <Check
            v-if="device.id === currentDeviceId"
            :size="15" :stroke-width="2.5"
            class="shrink-0 text-accent"
          />
        </DropdownMenuItem>

        <p v-if="playback.devicesError" class="px-2 py-2 text-xs text-danger">
          {{ playback.devicesError }}
        </p>
      </DropdownMenuContent>
    </DropdownMenuPortal>
  </DropdownMenuRoot>
</template>
