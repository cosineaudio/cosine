import { defineStore } from 'pinia';
import { ref } from 'vue';

/** Global shell UI state: queue drawer, full-screen player, palettes, etc. */
export const useUiStore = defineStore('ui', () => {
  const queueOpen = ref(false);
  const fullScreenOpen = ref(false);
  const commandOpen = ref(false);
  const shortcutsOpen = ref(false);
  const sidebarCollapsed = ref(false);
  /** Create-playlist dialog, optionally seeded with songs to add on creation. */
  const createPlaylistOpen = ref(false);
  const pendingSongIds = ref<string[]>([]);

  function toggleQueue() {
    queueOpen.value = !queueOpen.value;
  }
  function openFullScreen() {
    fullScreenOpen.value = true;
  }
  function closeFullScreen() {
    fullScreenOpen.value = false;
  }
  function toggleCommand() {
    commandOpen.value = !commandOpen.value;
  }
  function toggleShortcuts() {
    shortcutsOpen.value = !shortcutsOpen.value;
  }
  function toggleSidebar() {
    sidebarCollapsed.value = !sidebarCollapsed.value;
  }
  /** Open the create-playlist dialog, optionally pre-loading songs to add. */
  function openCreatePlaylist(songIds: string[] = []) {
    pendingSongIds.value = songIds;
    createPlaylistOpen.value = true;
  }
  function closeCreatePlaylist() {
    createPlaylistOpen.value = false;
    pendingSongIds.value = [];
  }

  return {
    queueOpen,
    fullScreenOpen,
    commandOpen,
    shortcutsOpen,
    sidebarCollapsed,
    createPlaylistOpen,
    pendingSongIds,
    toggleQueue,
    openFullScreen,
    closeFullScreen,
    toggleCommand,
    toggleShortcuts,
    toggleSidebar,
    openCreatePlaylist,
    closeCreatePlaylist,
  };
});
