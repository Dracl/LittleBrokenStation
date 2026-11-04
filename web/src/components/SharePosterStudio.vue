<script setup>
// 视频分享海报的调参台（观看页那颗「分享海报」开的）。
// 形态照周报调参台（WeeklyPage 11-04 版）：左边拧参数、右边看实况，预览与导出跑同一支笔。
// 这张卡的颜色从封面抽（utils/videoPoster.js），调参台给的是「拧 knobs」不是「重画」：
// 覆写文案、开关画面元素、恒浅/恒深、自选主色、落款、导出倍率 —— 全走同一套画笔 options。
// 全程零 fetch 上送、零后端写盘。
import { nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import {
  POSTER_EXPORT_SCALE,
  POSTER_WIDTH,
  loadPosterAssets,
  posterThemeOf,
  renderVideoPoster,
  rgbCss,
} from '../utils/videoPoster.js'
// qrcode.min.js 是 davidshimjs qrcodejs 原版经典脚本（没有模块导出），按资源 URL 注入 classic script
// 让它把 QRCode 落到 window 上（与 LoginDialog 同一套路、同一份 vendored 文件）。
import qrcodeScriptUrl from '../libs/qrcode.min.js?url'

const props = defineProps({
  // 海报数据：{ title, description, coverUrl, upName, upFace, upMid, bvid,
  //             viewCountText, danmakuCount, statLike, durationText }
  model: { type: Object, required: true },
})
const emit = defineEmits(['close'])

const canvasEl = ref(null)
const qrBox = ref(null)
const assets = ref({ cover: null, face: null, qr: null })
const busy = ref(true)
const size = ref({ w: 0, h: 0 })
// 左栏默认收起（站长 11-05 点单）：开箱即看大预览，想拧再点 footer 的「调参」展开
const panelOpen = ref(false)

// ── 调参状态（站长 11-04 点单：能自定义的全给拧上）──
const DEFAULT_SHOW = { qr: true, badge: true, duration: true, views: true, danmaku: true, likes: true, title: true, desc: true }
const SHOW_LABELS = [
  ['qr', '二维码'], ['badge', '角标'], ['duration', '时长'], ['views', '播放数'],
  ['danmaku', '弹幕数'], ['likes', '点赞数'], ['title', '标题'], ['desc', '简介'],
]
const MODES = [
  { key: 'auto', label: '自动', hint: '封面抽色定明暗' },
  { key: 'light', label: '恒浅', hint: '糖果底黑字' },
  { key: 'dark', label: '恒深', hint: '深底白字' },
]
const ACCENTS = [
  { key: '#fb7299', name: '小破站粉' },
  { key: '#00a1d6', name: '小破站蓝' },
  { key: '#22d3ee', name: '青' },
  { key: '#ffb020', name: '橙' },
  { key: '#9b6bff', name: '紫' },
]
const SCALES = [
  { key: 2, label: '标准 2x', hint: '宽 2000 px' },
  { key: 3, label: '高清 3x', hint: '宽 3000 px' },
]
const defaults = () => ({
  titleOverride: '',
  descOverride: '',
  badgeText: '',
  show: { ...DEFAULT_SHOW },
  mode: 'auto',
  accent: null,
  sign: '',
  scale: 2,
  descMaxLines: 3,
})
const opts = reactive(defaults())

function resetOpts() {
  Object.assign(opts, defaults())
}

/** 送给画笔的 payload：画笔侧 normalizeOpts 会浅拷 show，这里直接给活体即可。 */
function optsPayload() {
  return {
    titleOverride: opts.titleOverride,
    descOverride: opts.descOverride,
    badgeText: opts.badgeText,
    show: opts.show,
    mode: opts.mode,
    accent: opts.accent,
    sign: opts.sign,
    descMaxLines: opts.descMaxLines,
  }
}

// 换片 / 重开会连着起几轮异步：只认最后一轮的结果，旧图不许回头糊新海报
let token = 0
// 拨杆防抖：打字/拨开关都跟 150ms 再重画（照周报调参台的规矩）
let renderTimer = null

async function paint() {
  const el = canvasEl.value
  if (!el) return
  const mine = token
  // renderVideoPoster 开画前自己 await 海报字体（模块级单例，只下载一次）；拿不到就降级链照画
  const h = await renderVideoPoster(el, props.model, assets.value, POSTER_EXPORT_SCALE, optsPayload())
  if (mine !== token) return
  size.value = { w: POSTER_WIDTH * opts.scale, h: Math.round(h * opts.scale) }
}

function schedulePaint() {
  clearTimeout(renderTimer)
  renderTimer = setTimeout(paint, 150)
}

let qrScript = null
function ensureQRLib() {
  if (window.QRCode) return Promise.resolve()
  if (!qrScript) {
    qrScript = new Promise((resolve, reject) => {
      const el = document.createElement('script')
      el.src = qrcodeScriptUrl
      el.onload = resolve
      el.onerror = () => {
        qrScript = null
        reject(new Error('二维码组件没到位'))
      }
      document.head.appendChild(el)
    })
  }
  return qrScript
}

/**
 * 出码：300px 生码、画布上贴成 150dp（导出 2x 时正好 1:1，点阵不发虚）。
 * correctLevel H、点阵色用抽出来的 qrPoint（白底对比不足时画笔已经把它压暗）、底透明。
 * 出码失败静默跳过：底条照常，只是中间那块留白。
 */
async function makeQr(theme) {
  const el = qrBox.value
  const bvid = String(props.model?.bvid ?? '').trim()
  if (!el || !bvid) return null
  try {
    await ensureQRLib()
    el.innerHTML = ''
    // eslint-disable-next-line no-new
    new window.QRCode(el, {
      text: `${window.location.origin}/watch/${encodeURIComponent(bvid)}`,
      width: 300,
      height: 300,
      colorDark: rgbCss(theme.qrPoint),
      colorLight: '#00000000',
      correctLevel: window.QRCode.CorrectLevel.H,
    })
    return el.querySelector('canvas')
  } catch {
    return null
  }
}

// 上次出码用的点阵色：只有配色相关旋钮真挪了才重出码，省得白忙
let lastQrHex = ''
async function refreshQr() {
  if (!opts.show.qr) return
  const theme = posterThemeOf(assets.value.cover, optsPayload())
  const hex = rgbCss(theme.qrPoint)
  if (hex === lastQrHex && assets.value.qr) return
  const qr = await makeQr(theme)
  if (qr) assets.value = { ...assets.value, qr }
  lastQrHex = hex
}

async function boot() {
  const mine = ++token
  busy.value = true
  assets.value = { cover: null, face: null, qr: null }
  lastQrHex = ''
  await nextTick()
  if (mine !== token) return
  await paint() // 打开即渲染：先拿 fallback 色画一张，别对着空画布等封面
  const imgs = await loadPosterAssets(props.model)
  if (mine !== token) return
  assets.value = { ...imgs, qr: null }
  await refreshQr()
  if (mine !== token) return
  busy.value = false
  await paint()
}

/** 导出图（复制/下载共用）：分辨率跟调参台选的倍率走。 */
async function exportCanvas() {
  const cv = document.createElement('canvas')
  await renderVideoPoster(cv, props.model, assets.value, opts.scale, optsPayload())
  return cv
}

async function download() {
  try {
    const cv = await exportCanvas()
    const a = document.createElement('a')
    a.href = cv.toDataURL('image/png')
    a.download = `小破分站-${props.model?.bvid || 'poster'}.png`
    a.click()
  } catch {
    alert('海报没画出来，换个浏览器再试')
  }
}

/** 导出图进剪贴板：同一支笔重画一张的，toBlob 后走 ClipboardItem。
 * 浏览器不支持（老 Firefox 等）不打断站长：钮上给一句「复制失败」，下载钮照旧能走。 */
const copyState = ref('idle') // idle | ok | fail
let copyTimer = null
function flashCopy(stateRef) {
  clearTimeout(copyTimer)
  copyTimer = setTimeout(() => {
    stateRef.value = 'idle'
  }, 1600)
}

async function copyToClipboard() {
  try {
    if (!navigator.clipboard || typeof window.ClipboardItem !== 'function') throw new Error('unsupported')
    const cv = await exportCanvas()
    const blob = await new Promise((resolve, reject) => {
      cv.toBlob((b) => (b ? resolve(b) : reject(new Error('toBlob 空'))), 'image/png')
    })
    await navigator.clipboard.write([new window.ClipboardItem({ 'image/png': blob })])
    copyState.value = 'ok'
  } catch {
    copyState.value = 'fail'
  }
  flashCopy(copyState)
}

/** 图 + 链接同剪：站长 11-05 贴板实验室实测定格 —— 只交两副面孔：
 * text/html（海报原尺寸 jpeg base64 内联 <img> + 标题 + 链接）+ text/plain（标题+链接），
 * 绝不带 image/png：QQ 见位图只吞图丢 HTML。
 * 探针实测 R2000（2x 原尺寸）照显，故不缩图；固定 2x 尺寸（与导出倍率钮解耦，
 * 聊天窗用不着 3x，下载钮才走 3x）；jpeg 0.85（PNG base64 原尺寸 ~4MB 会显不出）。 */
const copyBothState = ref('idle')
async function copyImageAndLink() {
  const bvid = String(props.model?.bvid ?? '').trim()
  const url = `${window.location.origin}/watch/${encodeURIComponent(bvid)}`
  try {
    if (!navigator.clipboard || typeof window.ClipboardItem !== 'function') throw new Error('unsupported')
    const cv = document.createElement('canvas')
    await renderVideoPoster(cv, props.model, assets.value, POSTER_EXPORT_SCALE, optsPayload())
    // 站长 11-05 定案：文字只要链接本身，不要标题也不要换行分隔
    const dataUrl = cv.toDataURL('image/jpeg', 0.85)
    const html = `<p><img src="${dataUrl}"></p><p><a href="${url}">${url}</a></p>`
    await navigator.clipboard.write([new window.ClipboardItem({
      'text/plain': new Blob([url], { type: 'text/plain' }),
      'text/html': new Blob([html], { type: 'text/html' }),
    })])
    copyBothState.value = 'ok'
  } catch {
    copyBothState.value = 'fail'
  }
  flashCopy(copyBothState)
}

function onKey(e) {
  if (e.key === 'Escape') emit('close')
}

onMounted(() => {
  document.addEventListener('keydown', onKey)
  boot()
})
onBeforeUnmount(() => {
  document.removeEventListener('keydown', onKey)
  clearTimeout(copyTimer)
  clearTimeout(renderTimer)
  token += 1 // 已经卸架了，晚到的封面不许再往已销毁的画布上画
})
watch(() => props.model, () => boot())
// 任一旋钮挪动：QR 点阵色可能跟着变（accent/mode），先补码再防抖重画
watch(opts, () => {
  refreshQr().then(schedulePaint)
}, { deep: true })
</script>

<template>
  <Teleport to="body">
    <div class="fixed inset-0 z-[70] flex items-center justify-center bg-black/55 p-4" @click.self="emit('close')">
      <div class="flex max-h-[92vh] w-full max-w-[758px] overflow-hidden rounded-3xl bg-white shadow-2xl ring-1 ring-black/[.06] dark:bg-[#1a1e27] dark:ring-white/10">
        <!-- 左栏：调参台（照周报那台的控件族；滚动条按站长要求隐身，能滚就行；默认收起，footer「调参」开合） -->
        <div v-if="panelOpen" class="no-scrollbar w-[232px] shrink-0 overflow-y-auto border-r border-lineSoft px-4 py-4">
          <div class="flex items-baseline justify-between">
            <h3 class="text-[15px] font-semibold text-inkMain">海报调参台</h3>
            <span class="rounded-full bg-[#f1f2f4] px-2 py-0.5 text-[10px] font-medium text-inkFaint dark:bg-white/10">{{ size.w }} × {{ size.h }}</span>
          </div>
          <p class="mt-1.5 text-[11px] leading-[17px] text-inkFaint">左边拧参数，右边就是成品。预览与导出跑同一支笔。</p>

          <p class="mt-5 text-[11px] font-medium text-inkFaint">内容</p>
          <label class="mt-2 block text-[12px] text-inkSub">标题覆写</label>
          <input v-model="opts.titleOverride" maxlength="60" :placeholder="model?.title || '留空用原标题'" class="mt-1 w-full rounded-xl border border-lineSoft bg-white px-3 py-2 text-[13px] text-inkMain outline-none transition focus:border-stationPink dark:bg-white/5" />
          <label class="mt-2.5 block text-[12px] text-inkSub">简介覆写</label>
          <textarea v-model="opts.descOverride" rows="3" maxlength="120" :placeholder="model?.description || '留空用原简介'" class="mt-1 w-full resize-none rounded-xl border border-lineSoft bg-white px-3 py-2 text-[13px] leading-[18px] text-inkMain outline-none transition focus:border-stationPink dark:bg-white/5"></textarea>
          <label class="mt-2.5 block text-[12px] text-inkSub">角标文字</label>
          <input v-model="opts.badgeText" maxlength="4" placeholder="视频" class="mt-1 w-full rounded-xl border border-lineSoft bg-white px-3 py-2 text-[13px] text-inkMain outline-none transition focus:border-stationPink dark:bg-white/5" />

          <label class="mt-2.5 block text-[12px] text-inkSub">简介行数</label>
          <div class="mt-1 flex gap-1.5">
            <button
              v-for="n in [1, 2, 3, 4]"
              :key="n"
              type="button"
              class="flex-1 rounded-xl border py-1.5 text-[12px] transition"
              :class="opts.descMaxLines === n ? 'border-stationPink bg-stationPink/[.07] font-medium text-inkMain' : 'border-lineSoft text-inkSub hover:bg-[#f5f6f7] dark:hover:bg-white/5'"
              :title="n === 3 ? '默认三行' : n === 4 ? '多给一行，末尾仍带省略号' : `只留 ${n} 行`"
              @click="opts.descMaxLines = n"
            >
              {{ n }} 行
            </button>
          </div>
          <!-- 自由拉条：分段钮之外自己拉，1~10 行随心（卡高自动伸缩） -->
          <div class="mt-2 flex items-center gap-2">
            <input v-model.number="opts.descMaxLines" type="range" min="1" max="10" step="1" class="h-1.5 flex-1 cursor-pointer accent-stationPink" title="简介行数：拖到几行就显几行" />
            <span class="w-10 shrink-0 text-right text-[11px] tabular-nums text-inkFaint">{{ opts.descMaxLines }} 行</span>
          </div>

          <p class="mt-5 text-[11px] font-medium text-inkFaint">画面元素</p>
          <div class="mt-1.5 space-y-0.5">
            <div v-for="[k, label] in SHOW_LABELS" :key="k" class="flex items-center justify-between py-1">
              <span class="text-[12px] text-inkSub">{{ label }}</span>
              <button type="button" class="relative h-5 w-9 rounded-full transition" :class="opts.show[k] ? 'bg-stationPink' : 'bg-[#d9dde3]'" @click="opts.show[k] = !opts.show[k]">
                <span class="absolute top-0.5 h-4 w-4 rounded-full bg-white shadow transition-all" :style="{ left: opts.show[k] ? '18px' : '2px' }"></span>
              </button>
            </div>
          </div>

          <p class="mt-5 text-[11px] font-medium text-inkFaint">主题模式</p>
          <div class="mt-2 flex gap-1.5">
            <button
              v-for="m in MODES"
              :key="m.key"
              type="button"
              class="flex flex-1 flex-col items-start gap-0.5 rounded-xl border px-1.5 py-2 text-left transition"
              :class="opts.mode === m.key ? 'border-stationPink bg-stationPink/[.07]' : 'border-lineSoft hover:bg-[#f5f6f7] dark:hover:bg-white/5'"
              :title="m.hint"
              @click="opts.mode = m.key"
            >
              <span class="text-[12px]" :class="opts.mode === m.key ? 'font-medium text-inkMain' : 'text-inkSub'">{{ m.label }}</span>
              <span class="text-[9px] leading-[12px] text-inkFaint">{{ m.hint }}</span>
            </button>
          </div>

          <p class="mt-5 text-[11px] font-medium text-inkFaint">主色</p>
          <div class="mt-2 flex items-center gap-1.5">
            <button
              type="button"
              class="flex h-6 shrink-0 items-center rounded-full border px-2 text-[10px] transition"
              :class="!opts.accent ? 'border-stationPink font-medium text-stationPink bg-stationPink/[.07]' : 'border-lineSoft text-inkSub hover:bg-[#f5f6f7] dark:hover:bg-white/5'"
              title="跟随封面抽色"
              @click="opts.accent = null"
            >
              自动
            </button>
            <button
              v-for="a in ACCENTS"
              :key="a.key"
              type="button"
              class="h-6 w-6 shrink-0 rounded-full transition hover:scale-105"
              :class="{ 'ring-2 ring-offset-2 ring-offset-white dark:ring-offset-[#1a1e27]': opts.accent === a.key }"
              :style="{ background: a.key, boxShadow: opts.accent === a.key ? `0 0 0 2px ${a.key}` : '' }"
              :title="a.name"
              @click="opts.accent = a.key"
            ></button>
          </div>
          <p class="mt-1.5 text-[10px] leading-4 text-inkFaint/80">自选主色时角标、遮罩、二维码、渐变整条随它派生；明暗仍看封面整体（除非你按了恒浅/恒深）。</p>

          <p class="mt-5 text-[11px] font-medium text-inkFaint">落款</p>
          <input v-model="opts.sign" maxlength="16" placeholder="留空不画" class="mt-2 w-full rounded-xl border border-lineSoft bg-white px-3 py-2 text-[13px] text-inkMain outline-none transition focus:border-stationPink dark:bg-white/5" />
          <p class="mt-1.5 text-[10px] leading-4 text-inkFaint/80">一行小字落在卡外右下，比如「via 旅人四季」。</p>

          <p class="mt-5 text-[11px] font-medium text-inkFaint">导出</p>
          <div class="mt-2 flex gap-1.5">
            <button
              v-for="s in SCALES"
              :key="s.key"
              type="button"
              class="flex flex-1 flex-col items-start gap-0.5 rounded-xl border px-2.5 py-2 text-left transition"
              :class="opts.scale === s.key ? 'border-stationPink bg-stationPink/[.07]' : 'border-lineSoft hover:bg-[#f5f6f7] dark:hover:bg-white/5'"
              :title="s.hint"
              @click="opts.scale = s.key"
            >
              <span class="text-[12px]" :class="opts.scale === s.key ? 'font-medium text-inkMain' : 'text-inkSub'">{{ s.label }}</span>
              <span class="text-[10px] leading-4 text-inkFaint">{{ s.hint }}</span>
            </button>
          </div>

          <button
            type="button"
            class="mt-5 w-full rounded-xl border border-lineSoft py-1.5 text-[12px] text-inkSub transition hover:border-stationPink hover:text-stationPink"
            @click="resetOpts"
          >
            全部复位
          </button>
        </div>

        <!-- 右栏：实况 -->
        <div class="flex min-w-0 flex-1 flex-col bg-[#eceef1] dark:bg-[#12151c]">
          <div class="no-scrollbar flex min-h-0 flex-1 items-start justify-center overflow-y-auto px-5 py-5">
            <div class="relative">
              <div class="overflow-hidden rounded-xl shadow-[0_18px_48px_-12px_rgba(15,18,25,.45)] ring-1 ring-black/[.07] dark:ring-white/10">
                <!-- 画布按 1000dp 基准 ×2 落像素，这里只做 CSS 缩放：看到的像素就是导出的像素 -->
                <canvas ref="canvasEl" class="block w-[min(400px,80vw)]" :aria-label="`海报预览 ${model?.title || ''}`"></canvas>
              </div>
              <div v-if="busy" class="absolute inset-0 flex items-center justify-center rounded-xl bg-white/70 text-[12px] text-inkFaint">
                字体与封面备料中…
              </div>
            </div>
          </div>
          <div class="flex items-center justify-between gap-3 border-t border-black/[.06] bg-white/70 px-6 py-3 dark:border-white/10 dark:bg-white/[.03]">
            <p v-if="copyBothState === 'ok'" class="min-w-0 truncate text-[11px] text-stationPink">图与链接都已入剪贴板 · 粘聊天窗＝图，粘文本框＝链接</p>
            <p v-else class="min-w-0 truncate text-[11px] text-inkFaint">预览即成品 · 导出 {{ size.w || POSTER_WIDTH * opts.scale }} × {{ size.h || '…' }} px</p>
            <div class="flex shrink-0 items-center gap-2">
              <button
                type="button"
                class="whitespace-nowrap rounded-full border px-4 py-1.5 text-sm transition" :class="panelOpen ? 'border-stationPink bg-stationPink/[.07] text-stationPink' : 'border-lineSoft bg-white text-inkSub hover:border-stationPink hover:text-stationPink dark:bg-white/5'"
                @click="panelOpen = !panelOpen"
              >
                {{ panelOpen ? '收起设置' : '设置' }}
              </button>
              <button
                type="button"
                class="whitespace-nowrap rounded-full border border-lineSoft bg-white px-4 py-1.5 text-sm text-inkSub transition hover:border-stationPink hover:text-stationPink dark:bg-white/5"
                @click="emit('close')"
              >
                关闭
              </button>
              <button
                type="button"
                class="whitespace-nowrap rounded-full border border-lineSoft bg-white px-4 py-1.5 text-sm text-inkSub transition hover:border-stationPink hover:text-stationPink dark:bg-white/5"
                @click="copyToClipboard"
              >
                {{ copyState === 'ok' ? '已复制 ✓' : copyState === 'fail' ? '复制失败，请走下载' : '复制图片' }}
              </button>
              <button
                type="button"
                class="whitespace-nowrap rounded-full border border-lineSoft bg-white px-4 py-1.5 text-sm text-inkSub transition hover:border-stationPink hover:text-stationPink dark:bg-white/5"
                title="图片与链接一并入剪贴板：粘聊天窗出图，粘输入框出链接"
                @click="copyImageAndLink"
              >
                {{ copyBothState === 'ok' ? '图链已复制 ✓' : copyBothState === 'fail' ? '复制失败，请走下载' : '复制图链' }}
              </button>
              <button
                type="button"
                class="whitespace-nowrap rounded-full bg-gradient-to-r from-stationPink to-[#f994b6] px-5 py-1.5 text-sm font-medium text-white shadow-stationPink/30 transition hover:opacity-90 hover:shadow-stationPink/40"
                @click="download"
              >
                下载 PNG
              </button>
            </div>
          </div>
        </div>
      </div>

      <!-- 出码容器：离屏摆着（canvas 不参与布局也能画），出完取里面的 canvas 贴进海报 -->
      <div ref="qrBox" class="pointer-events-none fixed left-[-9999px] top-0 h-[300px] w-[300px] opacity-0" aria-hidden="true"></div>
    </div>
  </Teleport>
</template>
