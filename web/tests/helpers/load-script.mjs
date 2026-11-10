// 把 .vue 的 <script setup> 原样抽成 ES 模块装载：执行的是源码真实函数，不镜像、不改写逻辑。
// 做法：剥掉原 import 行（模板组件与 store 在 node 里装不动），冠上测试注入的 stub/prelude import，
// 尾部追加测试取用句柄（__t：状态与函数；__peek：模块内 let 的只读快照；__stubs：stub 模块本体）。
// 每次调用生成唯一文件对，绕开 ESM 模块缓存，做到「每个用例一份全新页面实例状态」。
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const HERE = fileURLToPath(import.meta.url)
const TMP = join(tmpdir(), 'lbs-web-race-tests')
const HOIST = pathToFileURL(join(HERE, '..', 'vue-hoist.mjs')).href
// 生成物住在临时目录，裸 'vue' 向上找不着 node_modules：从 helper 自身位置解析成绝对 URL 再注入
const VUE = import.meta.resolve('vue')

export async function loadVueScriptSetup(vueFile, { stubsSource, stubImports, exportNames = [], peekExprs = '' }) {
  const src = await readFile(vueFile, 'utf8')
  const m = src.match(/<script setup>([\s\S]*?)<\/script>/)
  if (!m) throw new Error(`missing <script setup> in ${vueFile}`)
  const body = m[1]
    .split('\n')
    .filter((line) => !/^import\s/.test(line))
    .join('\n')
  await mkdir(TMP, { recursive: true })
  const id = `${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`
  const stubPath = join(TMP, `stubs-${id}.mjs`)
  await writeFile(stubPath, String(stubsSource).replaceAll("from 'vue'", `from '${VUE}'`))
  const modPath = join(TMP, `mod-${id}.mjs`)
  const prelude = [
    `import { computed, ref, nextTick } from '${VUE}'`,
    `import { onMounted, onBeforeUnmount, watch } from '${HOIST}'`,
    `import * as __stubs from '${pathToFileURL(stubPath).href}'`,
    `const { ${stubImports.join(', ')} } = __stubs`,
    '',
  ].join('\n')
  const tail = [
    '',
    `export const __t = { ${exportNames.join(', ')} }`,
    `export const __peek = () => ({ ${peekExprs} })`,
    `export { __stubs }`,
    '',
  ].join('\n')
  await writeFile(modPath, prelude + body + tail)
  return import(pathToFileURL(modPath).href)
}

// fetch 桩：按注册顺序消耗规则；gate 模拟回包迟到，jsonGate 模拟「fetch 先到、body 解析迟到」。
// 未注册的路径（/api/events/ingest、progress 等）给通用兜底，只记不拦。
export function makeNet() {
  const net = []
  const plans = []
  function plan({ match, ok = true, status = 200, body = {}, gate = null, jsonGate = null }) {
    plans.push({ match, ok, status, body, gate, jsonGate, used: false })
  }
  async function fetchStub(url) {
    const p = plans.find((x) => !x.used && String(url).includes(x.match)) || null
    net.push({ url: String(url), hit: !!p })
    if (!p) return { ok: true, status: 200, json: async () => ({}) }
    p.used = true
    if (p.gate) await p.gate
    return {
      ok: p.ok,
      status: p.status,
      json: async () => {
        if (p.jsonGate) await p.jsonGate
        return p.body
      },
    }
  }
  function deferred() {
    let resolve
    const promise = new Promise((r) => {
      resolve = r
    })
    return { promise, resolve }
  }
  const flush = () => new Promise((r) => setImmediate(r))
  return { net, plan, fetchStub, deferred, flush }
}
