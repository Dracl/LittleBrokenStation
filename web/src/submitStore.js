import { ref } from 'vue'

/// 全站共享的「投稿」弹窗开关：TopBar、首页空态等都能唤起，App.vue 统一挂载。
export const submitDialogOpen = ref(false)
