import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath, URL } from 'node:url'

export default defineConfig({
  plugins: [
    vue(),
    tailwindcss(),
  ],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:5222',
        changeOrigin: true,
      },
      '/playback': {
        target: 'http://localhost:5222',
        changeOrigin: true,
      },
      '/auth': {
        target: 'http://localhost:5222',
        changeOrigin: false,
      },
      '/hubs': {
        target: 'http://localhost:5222',
        changeOrigin: true,
        ws: true,
      },
      '/health': {
        target: 'http://localhost:5222',
        changeOrigin: true,
      },
    },
  },
})
