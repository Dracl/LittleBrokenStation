// 老放映厅门牌 /watch/:bvid → /video/:bvid 的永久 redirect 契约（11-15 观映族改名）。
// 已发出去的朋友聊天记录与库内老私信卡全挂 /watch/ 串，这条兜底断了就是集体死链，所以钉成正式测格。
// 装载真实 router.js 建路由表：页面组件在 node 里装不动，换空壳——测的是路由表与 redirect 函数，组件内容无关。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFile, mkdir, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const ROOT = fileURLToPath(new URL('.', import.meta.url))
const ROUTER_SRC = join(ROOT, '..', 'src', 'router.js')
const VUE_ROUTER = import.meta.resolve('vue-router')

// createWebHistory 在 node 里要的最小台面：它构造时只摸 window.location / window.history / addEventListener。
globalThis.window = {
  location: { pathname: '/', search: '', hash: '', origin: 'http://localhost:8090' },
  history: { state: null, pushState() {}, replaceState() {}, go() {} },
  addEventListener() {},
  removeEventListener() {},
}
globalThis.document = { querySelector: () => null, createElement: () => ({ setAttribute() {}, style: {}, appendChild() {} }), addEventListener() {}, removeEventListener() {} }
// vue-router 认的是裸全局标识符（location/history），不是 window.xxx——三件套都给同一对象
globalThis.location = globalThis.window.location
globalThis.history = globalThis.window.history
globalThis.window.pageYOffset = 0
globalThis.window.pageXOffset = 0

async function bootRouter() {
  let src = await readFile(ROUTER_SRC, 'utf8')
  src = src.replace(/^import\s+(\w+)\s+from\s+'\.\.?\/pages\/\w+\.vue'\s*$/gm,
    (_, name) => `const ${name} = { name: ${JSON.stringify(name)} }`)
  src = src.replace(/^import \{[^}]+\} from '\.\/memberStore'\s*$/m,
    'const member = { value: null }; const loaded = { value: true }; const loadMember = async () => {}')
  src = src.replace("from 'vue-router'", `from '${VUE_ROUTER}'`)
  const dir = join(tmpdir(), 'lbs-router-tests')
  await mkdir(dir, { recursive: true })
  const p = join(dir, `router-${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}.mjs`)
  await writeFile(p, src)
  const mod = await import(pathToFileURL(p).href)
  return mod.default ?? mod.router
}

test('老门牌 /watch 落进 /video：params 换名、query 与 hash 原样透传', async () => {
  const router = await bootRouter()
  // redirect 发生在真导航（push）而非 resolve；currentRoute 是落点的权威态，
  // 比抓假环境里拼了 base 残渣的 pushState URL 更硬。
  await router.push('/watch/BV1gEps62EQY?t=83#p3')
  assert.equal(router.currentRoute.value.fullPath, '/video/BV1gEps62EQY?t=83#p3') // 续播 ?t= 与 hash 不能丢在 redirect 里
  assert.equal(router.currentRoute.value.name, 'video') // 落定的是新正主，不再回 /watch 匹配
})

test('正主 /video/:bvid 在册且名为 video；/watch 不再是可解析的名字路由', async () => {
  const router = await bootRouter()
  assert.equal(router.resolve('/video/BV1gEps62EQY').name, 'video')
  // /watch（不带 bvid）从来不是路由；改名后也不许有名为 watch 的具名路由（防回退）
  assert.throws(() => router.resolve({ name: 'watch' }))
})
