<script setup lang="ts">
import type { Component } from 'vue';

defineProps<{
  icon?: Component;
  title: string;
  description?: string;
}>();
</script>

<template>
  <div class="flex flex-col items-center justify-center px-6 py-20 text-center">
    <div
      v-if="icon"
      class="mb-5 grid h-16 w-16 place-items-center rounded-full border border-border bg-surface text-muted"
    >
      <component :is="icon" :size="28" :stroke-width="1.5" />
    </div>
    <h3 class="font-display text-title font-semibold tracking-tight text-text">
      {{ title }}
    </h3>
    <p v-if="description" class="mt-2 max-w-sm text-sm text-muted">
      {{ description }}
    </p>
    <div class="mt-6 empty:hidden">
      <slot />
    </div>
  </div>
</template>
