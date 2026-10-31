import { ref } from 'vue';

/**
 * Drag-to-reorder state for a queue list. Held here rather than in the row component because a
 * row cannot know what it is being dropped onto - only the list has the indices - and because
 * both panels that render the queue need exactly this and nothing more.
 *
 * @param move Applies the reorder, i.e. the playback store's `moveInQueue`.
 */
export function useQueueDrag(move: (from: number, to: number) => void) {
  const dragIndex = ref<number | null>(null);
  const overIndex = ref<number | null>(null);

  function onDragStart(index: number, e: DragEvent): void {
    dragIndex.value = index;
    if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move';
  }

  function onDragOver(index: number, e: DragEvent): void {
    e.preventDefault();
    overIndex.value = index;
    if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
  }

  function onDrop(index: number): void {
    if (dragIndex.value !== null && dragIndex.value !== index) move(dragIndex.value, index);
    onDragEnd();
  }

  function onDragEnd(): void {
    dragIndex.value = null;
    overIndex.value = null;
  }

  return { dragIndex, overIndex, onDragStart, onDragOver, onDrop, onDragEnd };
}
