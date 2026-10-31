<script setup lang="ts">
import { Heart } from 'lucide-vue-next';

withDefaults(
  defineProps<{
    active?: boolean;
    size?: number;
  }>(),
  { active: false, size: 18 },
);

defineEmits<{ toggle: [] }>();
</script>

<template>
  <button
    type="button"
    :aria-label="active ? 'Remove from favorites' : 'Add to favorites'"
    :aria-pressed="active"
    class="grid place-items-center rounded-control p-1 transition-colors duration-150"
    :class="active ? 'text-accent' : 'text-muted hover:text-text'"
    @click.stop="$emit('toggle')"
  >
    <Heart :size="size" :stroke-width="2" :fill="active ? 'currentColor' : 'none'" />
  </button>
</template>
