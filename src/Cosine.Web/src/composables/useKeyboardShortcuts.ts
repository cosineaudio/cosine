import { useMagicKeys } from '@vueuse/core';
import { usePlaybackStore } from '@/stores/playback';
import { useUiStore } from '@/stores/ui';
import { useFavoritesStore } from '@/stores/favorites';

/** Global media/navigation hotkeys. Mount once (in the app shell). */
export function useKeyboardShortcuts() {
  const player = usePlaybackStore();
  const ui = useUiStore();
  const favorites = useFavoritesStore();

  function isTypingTarget(el: EventTarget | null): boolean {
    const node = el as HTMLElement | null;
    if (!node || !node.tagName) return false;
    const tag = node.tagName;
    return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || node.isContentEditable;
  }

  function focusSearch() {
    const input = document.getElementById('global-search') as HTMLInputElement | null;
    input?.focus();
    input?.select();
  }

  function closeTopOverlay(): boolean {
    if (ui.commandOpen) { ui.commandOpen = false; return true; }
    if (ui.shortcutsOpen) { ui.shortcutsOpen = false; return true; }
    if (ui.fullScreenOpen) { ui.closeFullScreen(); return true; }
    if (ui.queueOpen) { ui.queueOpen = false; return true; }
    return false;
  }

  useMagicKeys({
    passive: false,
    onEventFired(e) {
      if (e.type !== 'keydown') return;

      // ⌘K / Ctrl+K - command palette (works even inside inputs)
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        ui.toggleCommand();
        return;
      }
      // leave other modifier combos to the browser
      if (e.metaKey || e.ctrlKey || e.altKey) return;

      // Escape closes overlays regardless of focus
      if (e.key === 'Escape') {
        if (closeTopOverlay()) e.preventDefault();
        return;
      }

      if (isTypingTarget(e.target)) return;

      switch (e.key) {
        case ' ':
          e.preventDefault();
          player.togglePlay();
          break;
        case 'ArrowLeft':
          if (player.hasTrack) { e.preventDefault(); player.seek(Math.max(0, player.currentTime - 5)); }
          break;
        case 'ArrowRight':
          if (player.hasTrack && player.duration > 0) {
            e.preventDefault();
            player.seek(Math.min(player.duration, player.currentTime + 5));
          }
          break;
        case 'ArrowUp':
          e.preventDefault();
          player.setVolume(player.volume + 0.05);
          break;
        case 'ArrowDown':
          e.preventDefault();
          player.setVolume(player.volume - 0.05);
          break;
        case 'n': case 'N':
          player.next();
          break;
        case 'p': case 'P':
          player.previous();
          break;
        case 'f': case 'F':
          if (player.currentTrack) favorites.toggle('song', player.currentTrack.id, player.currentTrack.title);
          break;
        case 'q': case 'Q':
          ui.toggleQueue();
          break;
        case '/':
          e.preventDefault();
          focusSearch();
          break;
        case '?':
          ui.toggleShortcuts();
          break;
      }
    },
  });
}

/** For the cheatsheet dialog. */
export const SHORTCUTS: { keys: string[]; label: string }[] = [
  { keys: ['Space'], label: 'Play / pause' },
  { keys: ['←', '→'], label: 'Seek 5s' },
  { keys: ['↑', '↓'], label: 'Volume' },
  { keys: ['N'], label: 'Next track' },
  { keys: ['P'], label: 'Previous track' },
  { keys: ['F'], label: 'Favorite current track' },
  { keys: ['Q'], label: 'Toggle queue' },
  { keys: ['/'], label: 'Focus search' },
  { keys: ['⌘', 'K'], label: 'Command palette' },
  { keys: ['?'], label: 'This cheatsheet' },
  { keys: ['Esc'], label: 'Close overlays' },
];
