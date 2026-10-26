import { createRouter, createWebHistory } from 'vue-router'
import HomePage from './pages/HomePage.vue'
import AdminPage from './pages/AdminPage.vue'
import WatchPage from './pages/WatchPage.vue'
import SearchPage from './pages/SearchPage.vue'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'home', component: HomePage, meta: { title: '分站首页 - 小破分站' } },
    { path: '/watch/:bvid', name: 'watch', component: WatchPage, meta: { title: '在线观看 - 小破分站' } },
    { path: '/search', name: 'search', component: SearchPage, meta: { title: '站内搜索 - 小破分站' } },
    { path: '/admin', name: 'admin', component: AdminPage, meta: { title: '站长室 - 小破分站' } },
  ],
})

router.afterEach((to) => {
  document.title = to.meta.title ?? '小破分站'
})

export default router
