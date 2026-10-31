<script setup lang="ts">
import { Toaster } from 'vue-sonner';
import { CircleAlert, CircleCheck, Info, LoaderCircle, TriangleAlert } from 'lucide-vue-next';
</script>

<template>
  <router-view />
  <!--
    Visual dress lives in style.css (LIBERTAE toast section); --toast-offset is
    computed there too, lifting toasts above the now-playing bar / mobile tab bar
    so they never sit on the transport controls. Lucide icons per the design
    system - never sonner's built-ins.
  -->
  <Toaster
    theme="dark"
    position="bottom-center"
    :offset="{ bottom: 'var(--toast-offset)' }"
    :mobile-offset="{ bottom: 'var(--toast-offset)' }"
    :icons="{
      success: CircleCheck,
      error: CircleAlert,
      warning: TriangleAlert,
      info: Info,
      loading: LoaderCircle,
    }"
  />
</template>
