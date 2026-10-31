<script setup lang="ts">
import { computed } from 'vue';
import Skeleton from '@/components/ui/Skeleton.vue';
import { shimmerDelay, barWidth } from '@/lib/skeleton';

/**
 * Row placeholder for the admin tables.
 *
 * Column headers are static text the page already knows, so they are rendered
 * for real rather than blocked out - passing them keeps the header row present
 * throughout and stops the body dropping 40px when the data lands. The row
 * chrome (`p-3`, hairline dividers, `text-sm` line box) is the admin tables'.
 */
const props = withDefaults(
  defineProps<{
    rows?: number;
    /** The table's real column headings. Falls back to `cols` blank columns. */
    headers?: string[];
    cols?: number;
  }>(),
  { rows: 5, cols: 4 },
);

const columnCount = computed(() => props.headers?.length ?? props.cols);
</script>

<template>
  <div class="overflow-hidden rounded-control border border-border bg-surface">
    <div class="overflow-x-auto">
      <table class="w-full text-sm">
        <thead v-if="headers">
          <tr class="border-b border-border text-left">
            <th v-for="h in headers" :key="h" class="label p-3">{{ h }}</th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="r in rows"
            :key="r"
            class="border-b border-border/60 last:border-0"
            :style="shimmerDelay(r)"
          >
            <td v-for="c in columnCount" :key="c" class="p-3">
              <!-- h-5 matches the text-sm line box so rows keep their height -->
              <div class="flex h-5 items-center">
                <Skeleton class="h-4 w-full" :style="{ maxWidth: barWidth(r + c * 3, 30, 85) }" />
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
