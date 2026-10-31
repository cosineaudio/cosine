import { ListPlus, ListMusic, Plus } from 'lucide-vue-next';
import type { MenuItem } from '@/components/ui/ContextMenu.vue';
import { usePlaylistsStore } from '@/stores/playlists';
import { useUiStore } from '@/stores/ui';
import { isSystemPlaylist } from '@/api/playlists';

/**
 * Builds the reusable "Add to playlist" context-menu submenu shared by track
 * rows and cards. Each existing playlist adds the given songs; "New playlist…"
 * opens the global create dialog seeded with them.
 *
 * `resolveIds` may be sync (a track already in hand) or async (an album whose
 * songs must be fetched first), so the same helper serves rows and cards.
 */
export function usePlaylistMenu() {
  const playlists = usePlaylistsStore();
  const ui = useUiStore();
  // Warm the list so the submenu is populated by the time it's opened.
  playlists.load();

  function addToPlaylistItem(resolveIds: () => string[] | Promise<string[]>): MenuItem {
    return {
      label: 'Add to playlist',
      icon: ListPlus,
      children: [
        {
          label: 'New playlist…',
          icon: Plus,
          onSelect: async () => {
            ui.openCreatePlaylist(await resolveIds());
          },
        },
        // System playlists (Release Radar) are job-managed and not editable.
        ...playlists.items.filter((p) => !isSystemPlaylist(p)).map((p) => ({
          label: p.name,
          icon: ListMusic,
          onSelect: async () => {
            playlists.addSongs(p.id, await resolveIds(), p.name);
          },
        })),
      ],
    };
  }

  return { addToPlaylistItem };
}
