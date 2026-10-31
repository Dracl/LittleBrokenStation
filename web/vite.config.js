import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:8090',
        changeOrigin: true,
      },
      // 本地归档封面与悬停预览都挂在后端静态盘上：不代理的话 dev 页里它们全是碎图，
      // 连 seam carving 都会被误诊成算法坏了
      '/media': {
        target: 'http://localhost:8090',
        changeOrigin: true,
      },
      '/previews': {
        target: 'http://localhost:8090',
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: false,
  },
})
