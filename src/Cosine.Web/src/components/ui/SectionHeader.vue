<script setup lang="ts">
import { ArrowRight } from 'lucide-vue-next';
import type { RouteLocationRaw } from 'vue-router';

defineProps<{
  title: string;
  seeAll?: RouteLocationRaw;
}>();
</script>

<template>
  <div class="mb-4 flex items-end justify-between gap-4">
    <h2 class="font-display text-title font-semibold tracking-tight text-text">
      {{ title }}
    </h2>
    <RouterLink
      v-if="seeAll"
      :to="seeAll"
      class="group flex shrink-0 items-center gap-1 text-xs font-medium uppercase tracking-widest text-muted transition-colors hover:text-text"
    >
      See all
      <ArrowRight :size="14" :stroke-width="2" class="transition-transform group-hover:translate-x-0.5" />
    </RouterLink>
  </div>
</template>
