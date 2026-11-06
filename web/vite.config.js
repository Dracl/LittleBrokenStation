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
      // 封面缩略档 / 头像 / UP 主脸（含历史脸）/ 表情定格 / 评论图：与 /media 同一批后端静态盘，
      // 不代理的话 dev 页里这些全是碎图
      '/thumbs': {
        target: 'http://localhost:8090',
        changeOrigin: true,
      },
      '/avatars': {
        target: 'http://localhost:8090',
        changeOrigin: true,
      },
      '/emotes': {
        target: 'http://localhost:8090',
        changeOrigin: true,
      },
      '/cmt-img': {
        target: 'http://localhost:8090',
        changeOrigin: true,
      },
      '/up_faces': {
        target: 'http://localhost:8090',
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: false,
    rollupOptions: {
      output: {
        // 分块原则：三方库按稳/业分家（vue 系几乎不动，吃长缓存），
        // 再把两个大块从主入口摘出——站长室（142KB 源件、冷门口）与图标字典
        // （50KB 纯静态 path 数据、叶子模块），两者都是单向依赖，不跟业务模块绕环。
        manualChunks(id) {
          if (!id.includes('node_modules')) {
            const file = id.replace(/\\/g, '/')
            if (file.includes('/src/pages/AdminPage.vue')) return 'page-admin'
            if (file.includes('/src/assets/bicons.js')) return 'icons'
            return
          }
          if (id.includes('vue-router')) return 'vendor-vue-router'
          if (id.includes('@vue/') || /[\\/]node_modules[\\/]vue[\\/]/.test(id)) return 'vendor-vue'
          return 'vendor'
        },
      },
    },
  },
})
