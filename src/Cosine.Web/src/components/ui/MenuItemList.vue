<script setup lang="ts">
import type { Component } from 'vue';
import { ChevronRight } from 'lucide-vue-next';
import type { MenuItem } from './ContextMenu.vue';

/**
 * Renders a MenuItem[] using the supplied reka-ui primitives, so the same
 * markup backs both the click DropdownMenu and the right-click ContextMenu.
 */
defineProps<{
  items: MenuItem[];
  itemComp: Component;
  separatorComp: Component;
  subComp: Component;
  subTriggerComp: Component;
  subContentComp: Component;
  portalComp: Component;
}>();

const contentClass =
  'z-50 min-w-52 rounded-control border border-border bg-surface p-1 text-sm text-text shadow-xl shadow-black/40 focus:outline-none';
const itemClass =
  'flex cursor-pointer select-none items-center gap-2.5 rounded-[3px] px-2.5 py-2 outline-none data-[highlighted]:bg-surface-2 data-[disabled]:pointer-events-none data-[disabled]:opacity-40';
</script>

<template>
  <template v-for="(item, i) in items" :key="i">
    <component :is="separatorComp" v-if="item.separator" class="my-1 h-px bg-border" />

    <component :is="subComp" v-else-if="item.children">
      <component :is="subTriggerComp" :class="[itemClass, 'data-[state=open]:bg-surface-2']">
        <component :is="item.icon" v-if="item.icon" :size="16" :stroke-width="1.75" class="text-muted" />
        <span class="flex-1">{{ item.label }}</span>
        <ChevronRight :size="14" :stroke-width="2" class="text-muted" />
      </component>
      <component :is="portalComp">
        <component :is="subContentComp" :side-offset="4" :class="[contentClass, 'max-h-72 overflow-y-auto']">
          <component
            :is="itemComp"
            v-for="(child, j) in item.children"
            :key="j"
            :disabled="child.disabled"
            :class="[itemClass, child.danger ? 'text-danger' : '']"
            @select="child.onSelect?.()"
          >
            <component :is="child.icon" v-if="child.icon" :size="16" :stroke-width="1.75" class="text-muted" />
            <span class="flex-1 truncate">{{ child.label }}</span>
          </component>
        </component>
      </component>
    </component>

    <component
      :is="itemComp"
      v-else
      :disabled="item.disabled"
      :class="[itemClass, item.danger ? 'text-danger' : '']"
      @select="item.onSelect?.()"
    >
      <component :is="item.icon" v-if="item.icon" :size="16" :stroke-width="1.75" :class="item.danger ? '' : 'text-muted'" />
      <span class="flex-1">{{ item.label }}</span>
    </component>
  </template>
</template>
