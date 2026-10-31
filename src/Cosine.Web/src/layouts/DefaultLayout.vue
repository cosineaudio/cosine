<script setup lang="ts">
import { computed, onMounted, watch } from 'vue';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import { toast } from 'vue-sonner';
import { useAuthStore } from '@/stores/auth';
import { useUiStore } from '@/stores/ui';
import { usePlaybackStore } from '@/stores/playback';
import { usePartyStore } from '@/stores/party';
import AppSidebar from '@/components/layout/AppSidebar.vue';
import AppTopbar from '@/components/layout/AppTopbar.vue';
import MobileTabBar from '@/components/layout/MobileTabBar.vue';
import NowPlayingBar from '@/components/NowPlayingBar.vue';
import FullScreenPlayer from '@/components/FullScreenPlayer.vue';
import QueuePanel from '@/components/QueuePanel.vue';
import ShortcutsDialog from '@/components/ShortcutsDialog.vue';
import CreatePlaylistDialog from '@/components/CreatePlaylistDialog.vue';
import CommandSearch from '@/components/CommandSearch.vue';
import { useKeyboardShortcuts } from '@/composables/useKeyboardShortcuts';

const auth = useAuthStore();
const ui = useUiStore();
const player = usePlaybackStore();
const initializing = computed(() => !auth.initialized);
/**
 * The boot wordmark. A warm reload has the session back in a few milliseconds,
 * so without the gate the splash appears and vanishes on every single load -
 * the one place in the app guaranteed to flash.
 */
const showSplash = useDeferredLoading(initializing);

// Screen-reader announcement of the current track (updated on track change).
const nowPlayingLabel = computed(() => {
  const t = player.currentTrack;
  if (!t) return '';
  return `Now playing: ${t.title}${t.artist ? ` by ${t.artist}` : ''}`;
});

useKeyboardShortcuts();

// A remote session can stop being ours to drive without anyone in this tab touching anything -
// someone picks the speaker up in its own app, or it drops off the network. The transport buttons
// would simply stop working, so say what happened instead of letting it read as a broken UI.
watch(() => player.remoteInterruption, (interruption) => {
  if (interruption === 'hijacked') toast.warning(`${player.outputName} is being controlled by something else`);
  else if (interruption === 'offline') toast.error(`${player.outputName} stopped responding`);
});

watch(() => player.remoteError, (message) => {
  if (message) toast.error(message);
});

// Restore the cross-device queue and start persisting changes once the shell mounts.
onMounted(() => {
  player.initPersistence();
  // A speaker keeps playing after the tab is closed, so reopening it reattaches rather than
  // showing an idle browser next to music that is very much still going.
  void player.restoreSession();
  // Same idea for a listening party: reloading the host's tab must not strand the guests.
  void usePartyStore().init();
});
</script>

<template>
  <div v-if="showSplash" class="grid h-viewport place-items-center bg-bg">
    <div class="animate-pulse font-display text-2xl font-bold lowercase tracking-tight text-faint">cosine</div>
  </div>

  <!-- blank in the gap before the splash earns its place; see useDeferredLoading -->
  <div v-else-if="initializing" class="h-viewport bg-bg" />

  <div v-else class="flex h-viewport flex-col overflow-hidden bg-bg text-text">
    <div class="flex min-h-0 flex-1">
      <AppSidebar class="hidden lg:flex" :collapsed="ui.sidebarCollapsed" />
      <div class="flex min-w-0 flex-1 flex-col">
        <AppTopbar />
        <main class="flex-1 overflow-y-auto">
          <router-view v-slot="{ Component }">
            <transition name="page" mode="out-in">
              <component :is="Component" />
            </transition>
          </router-view>
        </main>
      </div>
    </div>

    <!-- persistent now-playing bar -->
    <NowPlayingBar />

    <!-- mobile navigation -->
    <div class="lg:hidden">
      <MobileTabBar />
    </div>

    <!-- overlays -->
    <FullScreenPlayer />
    <QueuePanel />
    <ShortcutsDialog />
    <CreatePlaylistDialog />
    <CommandSearch />

    <!-- polite now-playing announcement for assistive tech -->
    <p class="sr-only" role="status" aria-live="polite">{{ nowPlayingLabel }}</p>
  </div>
</template>

<style scoped>
.page-enter-active,
.page-leave-active {
  transition: opacity 0.18s ease, transform 0.18s ease;
}
.page-enter-from {
  opacity: 0;
  transform: translateY(6px);
}
.page-leave-to {
  opacity: 0;
}
</style>
