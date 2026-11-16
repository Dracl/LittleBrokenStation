<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { requestConfirm } from '../confirmStore'
import ShareCard from './ShareCard.vue'

// ─── 看板娘坞（M1.5；旧名「分站 AI 坞」，11-16 站长拍板改口更灵动）───────────────────────────────────────────────────
// 平时是颗 44px 黑玻璃气泡钮（与同族三钮同径同步——11-16 站长终稿：大小对齐，图标由星改气泡），
// 靠粉晕与玫红描边以示身份；点开在钮列上方弹悬浮聊天卡；悬浮坞不记展开态，每次从气泡钮起步。
// 聊天卡仍是 Runo 式：上方消息区（成员右玫红气泡、AI 左浅灰气泡带 ✨ 头像；AI 文案走 renderMd 消毒版 markdown—先转义后注入，v-html 只吃自家标签），
// 下方 textarea 自增高（3-8 行，11-16 二修放宽）+ 底部工具条（模型/思考档/上下文用量图标钮，右 40px 圆形发送钮，流式中换停止钮）。
// 闸门听后端的：GET /api/agent/status 的 visible 不放行就整块不进 DOM（连统计请求也不发）。
// 没接线的席位点钮直接带去 /me 接线（ready=false 时展开输入框也是死路，不如指对门）；钮描琥珀边自报待接线。ESC 可收面板。
const router = useRouter()
// 聊天走 SSE：fetch + getReader + TextDecoder 按行切，只吃 data: 帧——delta 追加进气泡，
// done 收尾，error 气泡内红字；HTTP 非 200 读 json.message 原样贴（401/403/429 同一张嘴）。
// 历史与展开态都存本机 localStorage（读写一律 try/catch，与 utils/studioPrefs 同一脾气）：
// 服务器不记一笔，每位成员各拿各的；历史留最近 50 条，上下文用量按服务端那把尺在本机粗估。
const HIST_KEY = 'lbs.agent.hist'
const REASON_KEY = 'lbs.agent.reasoning' // 本机记住这位席位选的思考档（选型变化时自动回落默认）
const HIST_MAX = 50
const LINE_H = 22 // textarea 的行高（14.5px 字 × 1.5 折整），自增高的上限按它算
const MIN_ROWS = 3
const MAX_ROWS = 8 // 11-16 站长二修：展开态要大一点，输入区自增高上限跟着放宽

function lsGet(key, fallback) {
  try {
    const raw = localStorage.getItem(key)
    return raw == null ? fallback : JSON.parse(raw)
  } catch {
    return fallback // 手改坏 JSON / 隐私模式：当没存过，继续跑
  }
}
function lsSet(key, val) {
  try {
    localStorage.setItem(key, JSON.stringify(val))
  } catch { /* 本机记不住就算了，聊天照常 */ }
}

// ─── 闸门：/api/agent/status ───────────────────────────────────────────
// null = 还没问到（或没问到过）：宁可什么都不摆，也不让一条点开是死的 AI 条糊在首页上。
const status = ref(null)
const visible = computed(() => status.value?.visible === true)

// ─── 展开态与历史 ──────────────────────────────────────────────────────
const open = ref(false)
const messages = ref([]) // {role:'user'|'assistant', content:string, error?:boolean}

// 聊天卡开在右下贴角（11-17 站长终稿：右对齐），首页那三个点（切换模式/日夜）让位——
// 展开时给 html 挂 agent-open 标记，style.css 里那族 .home-dock 淡出卸点击；收起自行回淡。
watch(open, (v) => document.documentElement.classList.toggle('agent-open', v))

// 表格撑卡令（11-17 站长：85% 气泡里的四列表格挤得看不见 UP 主）：对话里出现过表格，悬浮卡从 400px 撑到 680px——
// 左岸锚定只向右伸展不跳屏；新话题后回到 Runo 常态，纯文字会话永不占宽。
const wideCard = computed(() => messages.value.some((m) => m.role === 'assistant' && !m.error && (hasTable(m.content) || cardsOf(m).length)))
const draft = ref('')
const streaming = ref(false)

// ─── 思考档位（reasoning_effort）───────────────────────────────────────
// 选型清单吃 status.reasoningOptions（个人空间「看板娘」里配的竖线串）；清单空就整钮不摆。
// 选中值存本机（lbs.agent.reasoning），发送时递给后端——白名单最终由服务器把闸，这里只管递。
const reasoningSel = ref('') // '' = 默认（不递这字段）
const reasonMenu = ref(false)
const reasoningOptions = computed(() =>
  String(status.value?.reasoningOptions || '')
    .split('|')
    .map((s) => s.trim())
    .filter(Boolean),
)

function pickReasoning(v) {
  reasoningSel.value = v
  lsSet(REASON_KEY, v)
  reasonMenu.value = false
}

// ─── 上下文用量（11-16 美化令：条数与今日余额两枚旧 chip 已撤——自家 key 自家钱，不摆台账脸）───
// 上限吃个人空间配的「上下文大小（k）」；已用 = status 带的 system prompt 粗估 + 本机历史按服务端那把尺粗估
//（最近 20 条、单条掐 4000 字、总量 20000 超了从最旧丢；estTokens 与后端 EstimateTokens 同尺：CJK 每字计 1，其余 4 字折 1）。
// 工具执行卡的短名：后端回 search_videos 这类大名，卡面说人话
function toolLabel(name) {
  return { search_videos: '查片', list_arrivals: '到货', site_stats: '站况', member_activity: '我的账' }[name] || name
}

const ctxPop = ref(false)
const modelPop = ref(false)
const ctxK = computed(() => (Number.isFinite(status.value?.contextK) && status.value.contextK > 0 ? status.value.contextK : null))
function estTokens(text) {
  let units = 0
  for (const ch of String(text)) {
    if (/\s/.test(ch)) continue
    units += ch.codePointAt(0) >= 0x2e80 ? 1 : 0.25
  }
  return Math.ceil(units)
}
const usedTokens = computed(() => {
  const kept = messages.value
    .filter((m) => !m.error && m.content)
    .slice(-20)
    .map((m) => ({ role: m.role, content: String(m.content).slice(0, 4000) }))
  let sum = kept.reduce((a, m) => a + m.content.length, 0)
  while (kept.length > 1 && sum > 20_000) sum -= kept.shift().content.length
  return (Number.isFinite(status.value?.contextTokens) ? status.value.contextTokens : 0) + kept.reduce((a, m) => a + estTokens(m.content), 0)
})
const ctxPct = computed(() => (ctxK.value ? Math.min(100, Math.max(1, Math.round((usedTokens.value / (ctxK.value * 1000)) * 100))) : 0))
// 圈环几何与配色（11-16 站长三修：用量改成一枚圈，瞦一眼就有数）：r=6 周长约 37.7，虚线位移即水位；
// 轨道走 currentColor 淡淡一层，进度弧平时站内粉，过六成新转琥珀、过九成新转玫红报急。
const RING_C = 2 * Math.PI * 6
const ringOffset = computed(() => RING_C * (1 - ctxPct.value / 100))
const ringColor = computed(() => (ctxPct.value >= 90 ? '#f43f5e' : ctxPct.value >= 60 ? '#f59e0b' : '#fb7299'))
const usedLabel = computed(() => {
  const t = usedTokens.value
  if (t < 1000) return `${t} tokens`
  const k = t / 1000
  return `${k < 10 ? k.toFixed(1) : Math.round(k)}k`
})
function ctxChipClick() {
  if (!ctxK.value) {
    router.push('/me') // 没配窗口就带去补配置，对着一个永远空白的百分比发懵没意义
    return
  }
  ctxPop.value = !ctxPop.value
}

onMounted(async () => {
  try {
    const res = await fetch('/api/agent/status')
    status.value = res.ok ? await res.json().catch(() => null) : null
  } catch {
    status.value = null
  }
  if (!visible.value) return
  // 本机存过的思考档要还在现行选型里才继续用：配置页改过档位，旧的自动回落「默认」
  const savedReason = String(lsGet(REASON_KEY, '') ?? '')
  reasoningSel.value = reasoningOptions.value.some((o) => o.toLowerCase() === savedReason.toLowerCase()) ? savedReason : ''
  const hist = lsGet(HIST_KEY, [])
  messages.value = Array.isArray(hist)
    ? hist
        .filter((m) => m && (m.role === 'user' || m.role === 'assistant') && typeof m.content === 'string')
        .map((m) => ({ role: m.role, content: m.content }))
        .slice(-HIST_MAX)
    : []
  if (open.value) nextTick(scrollBottom)
})

function setOpen(v) {
  open.value = v
  if (v) nextTick(scrollBottom)
}

// ESC 收面板：只认 open 时的 Escape，输入框里写一半的字不受打扰（收起不丢草稿，也不停流）
function onKeydown(e) {
  if (e.key === 'Escape' && open.value) setOpen(false)
}
onMounted(() => window.addEventListener('keydown', onKeydown))
onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKeydown)
  document.documentElement.classList.remove('agent-open') // 卸载兼摘标记，不给别的页面留幽灵隐藏
})

// 点气泡钮：没接线（ready=false）不带进输入框——那只会对着 403 发懵，直接带去个人空间接线；
// 问到 ready=true 才弹聊天卡，没问到（status null）也先弹让聊天自己说话。
function tapBar() {
  if (status.value?.ready === false) {
    router.push('/me')
    return
  }
  setOpen(true)
}

function saveHist() {
  // note/工具碎屑不存本机（重开只留干净的问答正文），坏气泡与空包也不存
  lsSet(HIST_KEY, messages.value.filter((m) => (m.role === 'user' || m.role === 'assistant') && !m.error && m.content).map((m) => ({ role: m.role, content: m.content })).slice(-HIST_MAX))
}

// 新话题（11-17 站长令：与「清空」合并成一钮——两个按钮一个意思，留一个就好）：
// 历史存本机，开新局就是把本机这本抹了（连输入框里写了一半的字）——重确认防手滑；
// 站里的台账（站长看得见的调用笔数）不归这个钮管。
async function newChat() {
  if (!messages.value.length && !draft.value.trim()) return
  if (streaming.value) stop()
  const yes = await requestConfirm({
    title: '开个新话题？',
    message: '本屏问答会从界面上抹掉，看板娘把这段忘干净；输入框里写了一半的字也一并清。想续旧话就取消。',
    confirmText: '开新的',
  })
  if (!yes) return
  messages.value = []
  lsSet(HIST_KEY, [])
  draft.value = ''
  nextTick(autoGrow)
}

// 收起条不摆统计 chip（11-16 站长二修：今天 +N / 馆藏 N 两枚撤下，不必天天噮）；
// 数仍由后端 status 顺手算着（todayCount/totalCount），只是屏上不给位了。

// ─── 收发 ──────────────────────────────────────────────────────────────
// ─── AI 气泡的轻量 markdown 渲染（11-16 站长美化令）────────────────
// 立场：不引 markdown 库，模型的话一个字也不配直接当 HTML——escapeHtml 先把 & < > " ' 全缴械，
// 之后注入的每个标签都是自家模板写的；链接只放行 http/https，javascript: 之类连 href 都摸不到。
// 支持的子集：围栏代码块、行内码、粗/斜体、链接、#~#### 标题、- 与 1. 列表、> 引用、--- 分割线、段落分行、GFM 管道表格（11-17 站长令：模型报清单爱摆表，裸奔全家丑）。
// md-render-start（这对锚是给 node 抽取单测用的，改名要两边一起改）
const MD_CODE = 'my-1.5 overflow-x-auto rounded-lg bg-black/[0.06] px-3 py-2 font-mono text-[12px] leading-[18px] dark:bg-white/10'
const MD_INLINE_CODE = 'rounded bg-black/[0.06] px-1 py-px font-mono text-[12px] dark:bg-white/10'
const MD_LINK = 'text-stationPink underline decoration-stationPink/40 underline-offset-2 hover:decoration-stationPink'
function escapeHtml(s) {
  return String(s)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;')
}
function inlineMd(esc) {
  let s = esc.replace(/`([^`\n]+)`/g, (_, c) => `<code class="${MD_INLINE_CODE}">${c}</code>`)
  s = s.replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>')
  s = s.replace(/(^|[^*\w])\*([^*\n]+)\*(?!\*)/g, '$1<em>$2</em>')
  s = s.replace(/\[([^\]\n]+)\]\((https?:\/\/[^\s()<>]+)\)/g, `<a href="$2" target="_blank" rel="noopener noreferrer" class="${MD_LINK}">$1</a>`)
  return s
}
function renderMd(text) {
  const lines = escapeHtml(text).split(/\r?\n/)
  const out = []
  let para = []
  let list = null
  let quote = null
  let fence = null
  let table = null // { head: string[], align: (string|null)[], rows: string[][] }
  let tablePending = null // 等分隔行的候选表头行（只存一行，没等到就回归正文）
  const flushPara = () => {
    if (para.length) { out.push(`<p class="mb-1.5 last:mb-0">${para.map(inlineMd).join('<br>')}</p>`); para = [] }
  }
  const flushList = () => {
    if (!list) return
    const t = list.type
    out.push(`<${t} class="mb-1.5 space-y-1 pl-4 marker:text-inkFaint ${t === 'ul' ? 'list-disc' : 'list-decimal'}">${list.items.map((it) => `<li>${inlineMd(it)}</li>`).join('')}</${t}>`)
    list = null
  }
  const flushQuote = () => {
    if (!quote) return
    out.push(`<div class="mb-1.5 border-l-2 border-stationPink/40 pl-2.5 text-inkSub">${quote.map((q) => `<div>${inlineMd(q)}</div>`).join('')}</div>`)
    quote = null
  }
  // GFM 管道表格：只认「表头行 + |---| 分隔行」开头，没等到分隔行的管道行原样回正文——宁缺勿滥；
  // 对齐符 :--- ---: :---: 吃，宽度以表头为准（长行截短行补）；单元格内容同走 inlineMd，转义早已做完。
  const isRow = (t) => t.startsWith('|') && t.lastIndexOf('|') > 0
  const isSep = (t) => t.includes('-') && t.includes('|') && /^\|?[\s:|-]+\|?$/.test(t)
  const tableCells = (t) => t.replace(/^\|/, '').replace(/\|$/, '').split('|').map((c) => c.trim())
  const tableAligns = (t) => tableCells(t).map((c) => (/^:.*:$/.test(c) ? 'center' : /^:/.test(c) ? 'left' : /:$/.test(c) ? 'right' : null))
  const flushTable = () => {
    if (!table) return
    const sty = (i) => (table.align[i] ? ` style="text-align:${table.align[i]}"` : '')
    const th = table.head.map((c, i) => `<th class="border border-lineSoft bg-black/[0.04] px-2 py-1 font-semibold dark:bg-white/10"${sty(i)}>${inlineMd(c)}</th>`).join('')
    const tb = table.rows.map((r) => `<tr>${table.head.map((_, i) => `<td class="border border-lineSoft px-2 py-1"${sty(i)}>${inlineMd(r[i] ?? '')}</td>`).join('')}</tr>`).join('')
    out.push(`<div class="my-1.5 overflow-x-auto"><table class="w-full border-collapse text-[12.5px] leading-[18px]"><thead><tr>${th}</tr></thead><tbody>${tb}</tbody></table></div>`)
    table = null
  }
  const flushAll = () => {
    flushPara(); flushList(); flushQuote(); flushTable()
    if (tablePending) { para.push(tablePending); tablePending = null; flushPara() }
  }
  for (const line of lines) {
    if (fence !== null) {
      if (/^`{3,}\s*$/.test(line.trim())) { out.push(`<pre class="${MD_CODE}"><code>${fence.join('\n')}</code></pre>`); fence = null }
      else fence.push(line)
      continue
    }
    const t = line.trim()
    if (/^`{3,}\s*/.test(t)) { flushAll(); fence = []; continue }
    if (!t) { flushAll(); continue }
    if (table) {
      if (isRow(t)) { table.rows.push(tableCells(t)); continue }
      flushTable() // 这行不再是表格行：收表落定，本行继续走寻常分支
    }
    if (tablePending) {
      if (isSep(t)) { table = { head: tableCells(tablePending), align: tableAligns(t), rows: [] }; tablePending = null; continue }
      para.push(tablePending); tablePending = null // 候选表头等来分隔行：原行回正文流，维持旧行为
    }
    if (/^(-{3,}|\*{3,}|_{3,})$/.test(t)) { flushAll(); out.push('<div class="my-2 border-t border-lineSoft"></div>'); continue }
    const h = t.match(/^(#{1,4})\s+(.*)$/)
    if (h) { flushAll(); out.push(`<div class="mb-1 mt-2 font-semibold ${h[1].length <= 2 ? 'text-[14.5px]' : 'text-[13.5px]'}">${inlineMd(h[2])}</div>`); continue }
    const bq = t.match(/^&gt;\s?(.*)$/)
    if (bq) { flushPara(); flushList(); (quote ||= []).push(bq[1]); continue }
    const ul = t.match(/^[-*+]\s+(.*)$/)
    if (ul) { flushPara(); flushQuote(); if (list && list.type !== 'ul') flushList(); (list ||= { type: 'ul', items: [] }).items.push(ul[1]); continue }
    const ol = t.match(/^\d+[.)]\s+(.*)$/)
    if (ol) { flushPara(); flushQuote(); if (list && list.type !== 'ol') flushList(); (list ||= { type: 'ol', items: [] }).items.push(ol[1]); continue }
    if (isRow(t)) { flushPara(); flushList(); flushQuote(); tablePending = t; continue } // 管道行先存着：等到分隔行成表，等不到回归正文
    flushList(); flushQuote(); para.push(t)
  }
  flushAll()
  if (fence !== null) out.push(`<pre class="${MD_CODE}"><code>${fence.join('\n')}</code></pre>`) // 模型流到一半断了，围栏没合上也得摆出来
  return out.join('')
}
// md-render-end

// 这条回答里有没有表格？（全宽判定专用，与 renderMd 同一套开桌凭证）：
// 带表格的气泡吃满卡宽（11-17 站长令：85% 封顶把四列表格挤成横滚，UP 主列都看不见），纯文字气泡维持 85% 不占满排。
function hasTable(md) {
  const ls = (md || '').split(/\r?\n/).map((s) => s.trim())
  return ls.some((s, i) => /^\|.*\|/.test(s) && /\|/.test(ls[i + 1] || '') && (ls[i + 1] || '').includes('-') && /^\|?[\s:|-]+\|?$/.test(ls[i + 1]))
}

// ─── 荐片视频卡协议（11-17 站长令：她荐片发私信同款 ShareCard）───
// prompt 规矩 6 约定 ```video 围栏、块内一行一个 BV 号（只准本轮工具回过的号）。这里把围栏从正文拆走、
// 卡摆气泡底下；流式里没闭合的围栏不拆（renderMd 当未闭代码块暂画），闭合那一刻才成卡。
const BVID_RE = /^BV[0-9A-Za-z]{10}$/
function splitVideoCards(text) {
  const bvids = []
  const body = (text || '').replace(/^[ \t]*```(?:video|videocard)[ \t]*\r?\n([\s\S]*?)^[ \t]*```[ \t]*$/gim, (_m, block) => {
    for (const line of String(block).split(/\r?\n/)) {
      const t = line.trim()
      if (BVID_RE.test(t) && !bvids.includes(t)) bvids.push(t)
    }
    return ''
  })
  return { body, bvids }
}
function cardsOf(m) { return m.role === 'assistant' && !m.error ? splitVideoCards(m.content).bvids : [] }

// 卡面数据面：把消息里出现的 BV 攒一批去领取，领回即点亮；拉失败不写缓存，下轮消息变化再试。
const cardCache = ref({})
const cardPending = new Set()
async function ensureCards(bvids) {
  const missing = bvids.filter((b) => !(b in cardCache.value) && !cardPending.has(b))
  if (!missing.length) return
  missing.forEach((b) => cardPending.add(b))
  try {
    const res = await fetch('/api/agent/videocards', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ bvids: missing }),
    })
    if (res.ok) {
      const j = await res.json()
      for (const c of j?.cards ?? []) if (c?.bvid) cardCache.value = { ...cardCache.value, [c.bvid]: c }
    }
  } catch { /* 卡面没取着：占位静候，不在对话里闹 */ } finally {
    missing.forEach((b) => cardPending.delete(b))
  }
}
watch(messages, () => {
  const all = new Set()
  for (const m of messages.value) for (const b of cardsOf(m)) all.add(b)
  if (all.size) ensureCards([...all])
}, { deep: true })

const taEl = ref(null)
const msgsEl = ref(null)
let ctrl = null

function autoGrow() {
  const el = taEl.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = `${Math.min(el.scrollHeight, LINE_H * MAX_ROWS)}px`
}

function scrollBottom() {
  const el = msgsEl.value
  if (el) el.scrollTop = el.scrollHeight
}

function stop() {
  ctrl?.abort()
}

// 一行 SSE 文本 → 增量。只认 data: 前缀，坏帧（parse 不动）整行丢掉，不拿半截 JSON 去糊气泡。
function eatFrame(line, index) {
  const t = line.trim()
  if (!t.startsWith('data:')) return
  const body = t.slice(5).trim()
  if (!body) return
  let frame = null
  try {
    frame = JSON.parse(body)
  } catch {
    return
  }
  if (typeof frame?.delta === 'string') {
    messages.value[index].content += frame.delta
  } else if (typeof frame?.note === 'string') {
    // 裁剪/降级这类旁白：挂在当轮 AI 气泡顶上，不另开气泡——它们是旁注不是台词
    if (messages.value[index]) messages.value[index].notice = frame.note
  } else if (typeof frame?.tool === 'string') {
    const m = messages.value[index]
    if (m) {
      if (!Array.isArray(m.tools)) m.tools = []
      m.tools.push({ name: frame.tool, status: frame.status === 'err' ? 'err' : 'done', summary: typeof frame.summary === 'string' ? frame.summary : '' })
    }
  } else if (typeof frame?.error === 'string') {
    messages.value[index].error = true
    messages.value[index].content = frame.error
  }
}

async function send() {
  const text = draft.value.trim()
  if (!text || streaming.value) return
  // 上下文 = 本机账上的最近 HIST_MAX 条（坏气泡不进），加刚这句新的一发；空 AI 占位不递。
  const ctx = messages.value.filter((m) => !m.error && m.content).slice(-HIST_MAX).map((m) => ({ role: m.role, content: m.content }))
  ctx.push({ role: 'user', content: text })
  messages.value.push({ role: 'user', content: text })
  const index = messages.value.push({ role: 'assistant', content: '', error: false }) - 1
  draft.value = ''
  nextTick(autoGrow)
  streaming.value = true
  ctrl = new AbortController()
  scrollBottom()
  try {
    // 思考档只在命中现行选型时才递；选型被改过就不拿旧值烧下一发（服务器白名单是最后一道闸）
    const wantReason = reasoningOptions.value.some((o) => o.toLowerCase() === reasoningSel.value.toLowerCase()) ? reasoningSel.value : ''
    const res = await fetch('/api/agent/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ messages: ctx, reasoning: wantReason || null }),
      signal: ctrl.signal,
    })
    if (!res.ok) {
      // 拒客分支（401/403/429……）后端回的是普通 json {message}：原样贴，不改写、不润色。
      let msg = `AI 没接上话（HTTP ${res.status}）`
      try {
        const j = await res.json()
        if (typeof j?.message === 'string' && j.message) msg = j.message
      } catch { /* 连句整话都没给，用兜底句 */ }
      messages.value[index].error = true
      messages.value[index].content = msg
    } else if (res.body) {
      const reader = res.body.getReader()
      const dec = new TextDecoder()
      let buf = ''
      for (;;) {
        const { done, value } = await reader.read()
        if (done) break
        buf += dec.decode(value, { stream: true })
        const lines = buf.split('\n')
        buf = lines.pop() ?? '' // 末段可能只到半行，留到下轮再切
        for (const line of lines) eatFrame(line, index)
        scrollBottom()
      }
      buf += dec.decode()
      if (buf) eatFrame(buf, index)
      if (!messages.value[index].content && !messages.value[index].error) {
        messages.value[index].error = true
        messages.value[index].content = 'AI 空手而归'
      }
    }
  } catch (err) {
    if (err?.name !== 'AbortError') {
      messages.value[index].error = true
      if (!messages.value[index].content) messages.value[index].content = '和分站失联了，稍后再试'
    }
  } finally {
    // 按下停止又一个字没吐：把这发空占位撤了，账上不留空气泡
    const last = messages.value[index]
    if (last && !last.content && !last.error) messages.value.splice(index, 1)
    streaming.value = false
    ctrl = null
    saveHist()
    scrollBottom()
  }
}

function onEnter(e) {
  if (e.isComposing) return // 拼音选字那一下回车不该把半截话递出去
  e.preventDefault()
  send()
}
</script>

<template>
  <!-- visible 不放行（含 status 没问到）：整个组件不进 DOM -->
  <div v-if="visible">
    <!-- ═══ 悬浮气泡钮（唯一入口，11-16 重排令·终稿）：压在日夜钮正上方，与同族三钮同径（44px）同右锚，
         靠粉晕与玫红描边以示身份；没接线描琥珀边自报待接线；流式中角落粉点呼吸报信 ═══ -->
    <button
      type="button"
      class="home-dock fixed bottom-[9rem] right-6 z-40 flex h-11 w-11 items-center justify-center rounded-full border shadow-[0_6px_20px_rgba(251,114,153,0.25)] backdrop-blur transition hover:scale-105 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-stationPink/40"
      :class="status?.ready === false
        ? 'border-amber-400/70 bg-[radial-gradient(circle_at_30%_25%,#33291b,#171207_72%)]'
        : 'border-stationPink/55 bg-[radial-gradient(circle_at_30%_25%,#3a2130,#171016_72%)]'"
      :style="open ? { boxShadow: '0 0 0 3px rgba(251,114,153,0.25), 0 6px 20px rgba(251,114,153,0.35)' } : null"
      :title="status?.ready === false ? '看板娘：还没接线，点开个人空间' : '看板娘：聊两句（ESC 收起）'"
      aria-label="看板娘"
      @click="tapBar"
    >
      <svg viewBox="0 0 24 24" class="h-5 w-5 text-stationPink drop-shadow-[0_0_6px_rgba(251,114,153,0.6)]" fill="currentColor" aria-hidden="true">
        <path d="M12 3.2c-5 0-9 3.5-9 7.9 0 2.2 1 4.2 2.7 5.6-.2 1-.8 2.3-2 3.3 1.8.1 3.5-.6 4.7-1.3 1.1.3 2.3.5 3.6.5 5 0 9-3.5 9-7.9s-4-8.1-9-8.1z" />
      </svg>
      <span v-if="streaming && !open" class="absolute right-1.5 top-1.5 h-2.5 w-2.5 animate-pulse rounded-full bg-stationPink" aria-hidden="true"></span>
    </button>

    <!-- ═══ 悬浮聊天卡（点开态）：右下贴角常驻（11-17 站长终稿：右对齐；展开时首页钮列淡出让位，不重叠），弹入从右滑入；内容壳仍是 Runo 式输入卡（11-16 站长样张定版） ═══ -->
    <div
      v-if="open"
      class="dock-pop fixed bottom-6 right-6 z-40 rounded-[22px] border border-lineSoft bg-cardBg shadow-[0_12px_44px_rgba(0,0,0,0.16)] transition-[width] duration-300 ease-out dark:shadow-[0_12px_44px_rgba(0,0,0,0.55)]"
      :class="wideCard ? 'w-[min(680px,calc(100vw-3rem))]' : 'w-[min(400px,calc(100vw-3rem))]'"
    >
      <!-- 卡头：新话题（已并「清空」）+ 收起两钮 -->
      <div class="flex items-center justify-between px-5 pb-1 pt-3.5">
        <span class="text-[12.5px] font-medium text-inkSub"><span class="mr-1" aria-hidden="true">✨</span>看板娘</span>
        <span class="flex items-center gap-0.5">
          <button
            v-if="messages.length || draft.trim()"
            type="button"
            class="rounded-full px-2 py-0.5 text-[11.5px] text-inkFaint transition hover:bg-black/5 hover:text-inkSub dark:hover:bg-white/10"
            @click="newChat"
          >新话题</button>
          <button
            type="button"
            class="rounded-full px-2 py-0.5 text-[11.5px] text-inkFaint transition hover:bg-black/5 hover:text-inkSub dark:hover:bg-white/10"
            @click="setOpen(false)"
          >收起</button>
        </span>
      </div>

      <!-- 消息区：有对话才占高（悬浮卡改 max-h 50vh 掐顶，不糊屏幕；美化令双气泡+消毒版 markdown 照旧）；
           成员右玫红、AI 左浅灰带 ✨ 头像；AI 文案走 renderMd：先 escapeHtml 全转义再注入自家标签，v-html 只吃消毒后的产物 -->
      <div v-if="messages.length" ref="msgsEl" class="max-h-[min(50vh,440px)] space-y-3 overflow-y-auto px-5 py-3">
        <template v-for="(m, i) in messages" :key="i">
          <div v-if="m.role === 'user'" class="flex justify-end">
            <div class="max-w-[82%] whitespace-pre-wrap break-words rounded-2xl rounded-br-md bg-stationPink/10 px-4 py-2.5 text-[13.5px] leading-[21px] text-inkMain">{{ m.content }}</div>
          </div>
          <div v-else class="flex items-start gap-2">
            <span class="mt-0.5 flex h-7 w-7 shrink-0 select-none items-center justify-center rounded-full bg-stationPink/[0.12] text-[13px]" aria-hidden="true">✨</span>
            <!-- 同列纵排（11-17 站长二令：荐片卡与私信一模一样，不当「泡中卡」塞进气泡）：
                 文字泡与片卡泡上下各自成泡，ShareCard 本体就是完整融合泡（来源行/通栏封面/粉三角/动作条）。 -->
            <div class="flex min-w-0 flex-1 flex-col items-start gap-2">
            <div v-if="m.error" class="max-w-[82%] whitespace-pre-wrap break-words rounded-2xl rounded-tl-md border border-rose-400/30 bg-rose-400/5 px-4 py-2.5 text-[13.5px] leading-[21px] text-rose-500 dark:text-rose-400">{{ m.content }}</div>
            <div v-else class="min-w-0 break-words rounded-2xl rounded-tl-md border border-lineSoft/70 bg-black/[0.03] px-4 py-2.5 text-[13.5px] leading-[21px] text-inkMain transition-[max-width] duration-300 ease-out dark:bg-white/[0.06]" :class="hasTable(m.content) ? 'max-w-full' : 'max-w-[85%]'">
              <!-- 旁注行（裁剪/降级）：淡灰小字骑在气泡顶，是旁注不是台词 -->
              <div v-if="m.notice" class="mb-1.5 text-[11px] leading-[15px] text-inkFaint">{{ m.notice }}</div>
              <!-- 工具执行卡：一枚胶囊一把工具，🔍 查片/到货… + 结果短摘要，出错转玫红✕ -->
              <div v-if="m.tools?.length" class="mb-1.5 flex flex-wrap gap-1">
                <span v-for="(t, ti) in m.tools" :key="ti" class="inline-flex items-center gap-1 rounded-full border border-lineSoft bg-cardBg px-2 py-0.5 text-[11px] text-inkSub">
                  <span :class="t.status === 'err' ? 'text-rose-500' : 'text-stationPink'">{{ t.status === 'err' ? '✕' : '🔍' }}</span>
                  {{ toolLabel(t.name) }}
                  <span v-if="t.summary" class="text-inkFaint">· {{ t.summary }}</span>
                </span>
              </div>
              <span v-if="streaming && i === messages.length - 1 && !m.content && !m.tools?.length" class="flex items-center gap-1 py-1" aria-label="看板娘在打字">
                <span class="h-1.5 w-1.5 animate-bounce rounded-full bg-stationPink/70 [animation-delay:-0.3s]"></span>
                <span class="h-1.5 w-1.5 animate-bounce rounded-full bg-stationPink/70 [animation-delay:-0.15s]"></span>
                <span class="h-1.5 w-1.5 animate-bounce rounded-full bg-stationPink/70"></span>
              </span>
              <span v-else v-html="renderMd(splitVideoCards(m.content).body)"></span>
              <span v-if="streaming && i === messages.length - 1 && m.content" class="ml-0.5 inline-block h-[14px] w-[2px] translate-y-[2px] bg-stationPink" aria-hidden="true"></span>
            </div>
            <!-- 荐片视频卡（11-17 站长令）：```video 围栏 → 私信同款 ShareCard；卡面数据从 /api/agent/videocards 领一次存本机。
                 max-w-[78%] 与私信同款；占位虚框圆角 16px 对齐卡面，取着前不抖存在感。 -->
            <div v-if="cardsOf(m).length" class="w-full max-w-[78%] space-y-2">
              <template v-for="bv in cardsOf(m)" :key="bv">
                <ShareCard v-if="cardCache[bv] && cardCache[bv].found" kind="video" :dyn="cardCache[bv]" @open="router.push(`/video/${bv}`)" />
                <span v-else-if="cardCache[bv]" class="block rounded-[16px] border border-dashed border-lineSoft px-3 py-2.5 text-[11.5px] text-inkFaint">《{{ bv }}》不在架上（可能已下架、未入库或不归你看）</span>
                <span v-else class="block animate-pulse rounded-[16px] border border-dashed border-lineSoft px-3 py-2.5 text-[11.5px] text-inkFaint">去柜台取《{{ bv }}》的片卡…</span>
              </template>
            </div>
            </div>
          </div>
        </template>
      </div>

      <!-- 消息区与输入区的分隔（11-16 站长令）：有对话才摆线，新开屏时输入区紧挨卡头不孤零零画杠 -->
      <div v-if="messages.length" class="mx-5 border-t border-lineSoft"></div>

      <!-- 输入区：textarea 无边框直接融入卡面（Runo 样张的呼吸感），自增高 2-6 行照旧 -->
      <textarea
        ref="taEl"
        v-model="draft"
        :rows="MIN_ROWS"
        placeholder="让看板娘找片、查数据、投个稿"
        class="w-full resize-none bg-transparent px-5 pt-3 text-[15px] leading-[23px] text-inkMain outline-none transition placeholder:text-inkFaint"
        @input="autoGrow"
        @keydown.enter.exact.prevent="onEnter"
      ></textarea>

      <div class="relative flex items-center gap-0.5 px-3.5 pb-3 pt-1">
      <!-- 底部工具条（11-16 站长三修再刀）：信息钮全部只摆图标，文字收进跟上下文同款的深色浮窗/title；
           思考钮选过档时闪电转粉自报现状；圈环住到发送钮旁边，水位和发射同侧一眼尽 -->
        <div class="relative shrink-0">
          <button
            type="button"
            class="flex items-center rounded-full px-2 py-1.5 text-inkSub transition hover:bg-black/5 dark:hover:bg-white/10"
            :aria-expanded="modelPop"
            :title="'模型：' + (status?.ready === false ? '未接线' : (status?.model || '默认'))"
            @click="modelPop = !modelPop"
          >
            <svg class="h-[14px] w-[14px] opacity-70" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" /><path d="M3.3 7 12 12l8.7-5" /><path d="M12 22V12" /></svg>
          </button>
          <template v-if="modelPop">
            <div class="fixed inset-0 z-10" aria-hidden="true" @click="modelPop = false"></div>
            <div class="absolute bottom-full left-0 z-20 mb-1.5 whitespace-nowrap rounded-lg bg-zinc-800 px-3 py-2 text-[12px] leading-[17px] text-white shadow-[0_8px_28px_rgba(0,0,0,0.25)]">
              <div>模型</div>
              <div>{{ status?.ready === false ? '未接线' : (status?.model || '默认') }}</div>
            </div>
          </template>
        </div>
        <div v-if="reasoningOptions.length" class="relative shrink-0">
          <button
            type="button"
            class="flex items-center rounded-full px-2 py-1.5 text-inkSub transition hover:bg-black/5 dark:hover:bg-white/10"
            :aria-expanded="reasonMenu"
            :title="'思考：' + (reasoningSel || '默认') + '（点开选档）'"
            @click="reasonMenu = !reasonMenu"
          >
            <!-- 只摆闪电不摆字：选过档闪电转粉，一眼知道现在不是默认 -->
            <svg class="h-[14px] w-[14px]" :class="reasoningSel ? 'text-stationPink' : 'opacity-70'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M13 2 3 14h9l-1 8 10-12h-9l1-8z" /></svg>
          </button>
          <template v-if="reasonMenu">
            <div class="fixed inset-0 z-10" aria-hidden="true" @click="reasonMenu = false"></div>
            <div class="absolute bottom-full left-0 z-20 mb-1.5 w-48 rounded-xl border border-lineSoft bg-cardBg p-1 shadow-[0_8px_28px_rgba(0,0,0,0.14)]">
              <button
                type="button"
                class="flex w-full items-center justify-between rounded-lg px-2.5 py-1.5 text-left text-[12.5px] text-inkSub transition hover:bg-black/5 dark:hover:bg-white/10"
                @click="pickReasoning('')"
              >
                默认（随模型）
                <svg v-if="!reasoningSel" class="h-[13px] w-[13px] text-stationPink" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5" /></svg>
              </button>
              <button
                v-for="o in reasoningOptions"
                :key="o"
                type="button"
                class="flex w-full items-center justify-between rounded-lg px-2.5 py-1.5 text-left text-[12.5px] text-inkSub transition hover:bg-black/5 dark:hover:bg-white/10"
                @click="pickReasoning(o)"
              >
                {{ o }}
                <svg v-if="reasoningSel === o" class="h-[13px] w-[13px] text-stationPink" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5" /></svg>
              </button>
            </div>
          </template>
        </div>
        <!-- 上下文用量（11-16 美化令）：点开出深色小气泡报「上下文 256k / 已用 98k (38%)」那一口气；
             没配窗口的点这颗直接带去 /me 补配置。条数与今日余额两枚旧 chip 已撤。 -->
        <div class="relative ml-auto shrink-0">
          <button
            type="button"
            class="flex items-center rounded-full px-2 py-1.5 text-inkSub transition hover:bg-black/5 dark:hover:bg-white/10"
            :aria-expanded="ctxPop"
            :aria-label="ctxK ? `上下文 ${ctxK}k，已用 ${ctxPct}%` : '上下文未配置，点击去个人空间补'"
            :title="ctxK ? `上下文 ${ctxK}k · 已用 ${ctxPct}%` : '没配上下文窗口：点开个人空间补上'"
            @click="ctxChipClick"
          >
            <!-- 只摆圈不摆字（11-16 站长三修再刀）：数字全收进点开的气泡；未配置时轨道转琥珀自报待补 -->
            <svg class="h-[15px] w-[15px] -rotate-90 shrink-0" viewBox="0 0 16 16" aria-hidden="true">
              <circle cx="8" cy="8" r="6" fill="none" :stroke="ctxK ? 'currentColor' : '#f59e0b'" stroke-width="2.5" :class="ctxK ? 'opacity-20' : 'opacity-70'" />
              <circle v-if="ctxK" cx="8" cy="8" r="6" fill="none" :stroke="ringColor" stroke-width="2.5" stroke-linecap="round" :stroke-dasharray="RING_C" :stroke-dashoffset="ringOffset" />
            </svg>
          </button>
          <template v-if="ctxPop && ctxK">
            <div class="fixed inset-0 z-10" aria-hidden="true" @click="ctxPop = false"></div>
            <div class="absolute bottom-full right-0 z-20 mb-1.5 whitespace-nowrap rounded-lg bg-zinc-800 px-3 py-2 text-[12px] leading-[17px] text-white shadow-[0_8px_28px_rgba(0,0,0,0.25)]">
              <div>上下文 {{ ctxK }}k</div>
              <div>已用 {{ usedLabel }} ({{ ctxPct }}%)</div>
            </div>
          </template>
        </div>
          <button
            type="button"
            class="ml-1 flex h-10 w-10 shrink-0 items-center justify-center rounded-full transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-stationPink/40"
          :class="streaming
            ? 'bg-inkMain/80 text-white hover:bg-inkMain dark:bg-white/70 dark:hover:bg-white'
            : draft.trim()
              ? 'bg-stationPink text-white hover:opacity-90'
              : 'cursor-not-allowed bg-inkMain/10 text-inkFaint dark:bg-white/10'"
          :disabled="!streaming && !draft.trim()"
          :aria-label="streaming ? '停止回答' : '发送'"
          @click="streaming ? stop() : send()"
        >
          <svg v-if="!streaming" viewBox="0 0 24 24" class="h-[18px] w-[18px]" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19V5" /><path d="m5 12 7-7 7 7" /></svg>
          <svg v-else viewBox="0 0 24 24" class="h-[13px] w-[13px]" fill="currentColor" aria-hidden="true"><rect x="6" y="6" width="12" height="12" rx="2.5" /></svg>
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
/* 悬浮聊天卡弹入：右岸滑入落座（11-17 终稿：面板右对齐，弹入从右回弹） */
@keyframes dock-pop {
  from { opacity: 0; transform: translateX(14px) scale(0.97); }
  to { opacity: 1; transform: none; }
}
.dock-pop { animation: dock-pop 0.22s cubic-bezier(0.34, 1.3, 0.64, 1); }
</style>
