<script setup lang="ts">
import type { Component } from 'vue';
import {
  DropdownMenuRoot,
  DropdownMenuTrigger,
  DropdownMenuPortal,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuSub,
  DropdownMenuSubTrigger,
  DropdownMenuSubContent,
  ContextMenuRoot,
  ContextMenuTrigger,
  ContextMenuPortal,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuSub,
  ContextMenuSubTrigger,
  ContextMenuSubContent,
} from 'reka-ui';
import MenuItemList from './MenuItemList.vue';

export interface MenuItem {
  label: string;
  icon?: Component;
  onSelect?: () => void;
  danger?: boolean;
  separator?: boolean;
  disabled?: boolean;
  children?: MenuItem[];
}

defineProps<{
  items: MenuItem[];
  /** Open on right-click over the slotted element instead of on click. */
  contextMenu?: boolean;
}>();

const contentClass =
  'z-50 min-w-52 rounded-control border border-border bg-surface p-1 text-sm text-text shadow-xl shadow-black/40 focus:outline-none';
</script>

<template>
  <!-- Right-click menu: the slot is the trigger area -->
  <ContextMenuRoot v-if="contextMenu">
    <ContextMenuTrigger as-child>
      <slot />
    </ContextMenuTrigger>
    <ContextMenuPortal>
      <ContextMenuContent :class="contentClass">
        <MenuItemList
          :items="items"
          :item-comp="ContextMenuItem"
          :separator-comp="ContextMenuSeparator"
          :sub-comp="ContextMenuSub"
          :sub-trigger-comp="ContextMenuSubTrigger"
          :sub-content-comp="ContextMenuSubContent"
          :portal-comp="ContextMenuPortal"
        />
      </ContextMenuContent>
    </ContextMenuPortal>
  </ContextMenuRoot>

  <!-- Click menu: the slot is the button trigger -->
  <DropdownMenuRoot v-else>
    <DropdownMenuTrigger as-child>
      <slot />
    </DropdownMenuTrigger>
    <DropdownMenuPortal>
      <DropdownMenuContent :side-offset="6" align="end" :class="contentClass">
        <MenuItemList
          :items="items"
          :item-comp="DropdownMenuItem"
          :separator-comp="DropdownMenuSeparator"
          :sub-comp="DropdownMenuSub"
          :sub-trigger-comp="DropdownMenuSubTrigger"
          :sub-content-comp="DropdownMenuSubContent"
          :portal-comp="DropdownMenuPortal"
        />
      </DropdownMenuContent>
    </DropdownMenuPortal>
  </DropdownMenuRoot>
</template>
