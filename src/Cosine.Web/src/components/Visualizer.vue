<script setup lang="ts">
import { computed } from 'vue';
import VisualizerCanvas from './VisualizerCanvas.vue';
import { resolveVisualizer } from '@/visualizers/registry';

const props = withDefaults(defineProps<{
  tint: string | null;
  trackId?: string | null;
  active: boolean;
  visualizer?: string;
  /** External audio provider for party guests; avoids creating the library player store. */
  analyser?: () => AnalyserNode | null;
  playing?: boolean;
}>(), { visualizer: 'spectrum' });
const definition = computed(() => resolveVisualizer(props.visualizer));
</script>

<template>
  <!-- A fresh canvas lets renderers switch freely between 2D and WebGL contexts. -->
  <VisualizerCanvas
    :key="definition.id"
    :definition="definition"
    :tint="tint"
    :track-id="trackId"
    :active="active"
    :analyser="analyser"
    :playing="playing"
  />
</template>
