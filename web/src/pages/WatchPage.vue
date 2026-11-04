<script setup>
import { mentionHtml } from '../utils/mention'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import TopBar from '../components/TopBar.vue'
import VideoPlayer from '../components/VideoPlayer.vue'
import CommentSection from '../components/CommentSection.vue'
import UpCard from '../components/UpCard.vue'
import CoverPeek from '../components/CoverPeek.vue'
import SideHandle from '../components/SideHandle.vue'
import VideoSettingsDialog from '../components/VideoSettingsDialog.vue'
import PageList from '../components/PageList.vue'
import VideoActions from '../components/VideoActions.vue'
import ShareDialog from '../components/ShareDialog.vue'
import SharePosterStudio from '../components/SharePosterStudio.vue'
import DanmakuList from '../components/DanmakuList.vue'
import UpNextList from '../components/UpNextList.vue'
import EmptyTv from '../components/EmptyTv.vue'
import VisibilityPill from '../components/VisibilityPill.vue'
import PlayCountIcon from '../components/PlayCountIcon.vue'
import DanmakuCountIcon from '../components/DanmakuCountIcon.vue'
import { member, loginDialogOpen } from '../memberStore'
import { submitDialogOpen } from '../submitStore'
import { HINT_DELETE_VIDEO, HINT_FETCH_COMMENTS } from '../confirmStore'
import { refetchArchive, removeVideoEntry } from '../utils/videoActions'

const route = useRoute()
const router = useRouter()
const data = ref(null)
const gateState = ref(null) // null=正常 | 'login'=访客挡在放映厅门外 | 'missing'=馆藏确无此片
const loading = ref(true)
const activeIndex = ref(0)

// 可见性那枚小徽标抽成了 VisibilityPill（放映厅与合集页头共用一份形状），这里只管递档位。

const currentPage = computed(() => {
  const pages = data.value?.pages ?? []
  return pages.find((p) => p.index === activeIndex.value) ?? pages[0] ?? null
})

const hasNextPage = computed(() => {
  const pages = data.value?.pages ?? []
  return pages.some((p) => p.index > activeIndex.value && p.videoUrl)
})

function playNextPage() {
  const pages = data.value?.pages ?? []
  const next = pages.find((p) => p.index > activeIndex.value && p.videoUrl)
  if (next) activeIndex.value = next.index
}

// withRelated：「接下来播放」「本地弹幕」这类进页才该动一次的区块，只跟进页 / 换 BV 那一刻走。
// 编码轮询（silent）要的只是 pages.codecVersions 那一份：连推荐流一起拽回来，层内随机就变成
// 每 5 秒重抽一批面孔（肉眼上是整个模块在抽风），弹幕列表也跟着白刷一趟。
// 轮询本身不能停：加档行从 Downloading 变定档得靠这一发把面板催回来。
async function loadWatch(silent = false, withRelated = !silent) {
  if (!silent) {
    loading.value = true
    gateState.value = null
  }
  try {
    const res = await fetch(`/api/watch/${route.params.bvid}`)
    if (res.ok) {
      data.value = await res.json()
      if (withRelated) {
        loadRelated(data.value.id)
        loadLocalDanmaku()
      }
      ensureCodecPoll() // 上一轮还没跑完的加档（含刷新前点的那些）：服务端有货就接着刷，不冻在快照上
      if (!openReported) {
        // 进入并加载成功：报一枪 watch.open（同一 BV 一次，换 P 不重报）
        openReported = true
        reportEvent('watch.open')
      }
      if (!silent) {
        const firstPlayable = data.value.pages.find((p) => p.videoUrl) ?? data.value.pages[0]
        activeIndex.value = firstPlayable ? firstPlayable.index : 0
        // 续播只在进页（含换 BV）这一刻问一次：静默刷新（编码轮询这些）不能把正在看的位子抓走。
        loadResumePoint(data.value.id, activeIndex.value)
      }
    } else if (!silent) {
      data.value = null
      // 门禁与查无此片是两回事：401=还没站籍，别拿「不存在」糊弄门外的客人
      gateState.value = res.status === 401 ? 'login' : 'missing'
    }
  } catch {
    if (!silent) {
      data.value = null
      gateState.value = 'missing'
    }
  } finally {
    if (!silent) loading.value = false
  }
}

function selectPage(index) {
  activeIndex.value = index
  // 换源后等 autoplay 无望，交给用户按播放；这里只把弹幕时间归零自然发生
}

// ─── 续播：进页问一次「上次看到哪」，够条件就把播放器丢过去并在播放器下缘浮一枚小泡 ───
// 判据都在这一层：刚开头（≤10 秒）与接近片尾（≥95%）都不跳；位置不在落地那一分 P 上也不跳
// （手动切选集是另一件事，不该被续播插一脚）。真正的 seek 由播放器在元数据就位后落。
const resumeSec = ref(0)
const resumeTip = ref(null) // { sec }：null = 不摆泡
const resumeTipFading = ref(false)
let resumeTipFadeTimer = 0
let resumeTipGoneTimer = 0

function clearResumeTip() {
  if (resumeTipFadeTimer) clearTimeout(resumeTipFadeTimer)
  if (resumeTipGoneTimer) clearTimeout(resumeTipGoneTimer)
  resumeTipFadeTimer = 0
  resumeTipGoneTimer = 0
  resumeTip.value = null
  resumeTipFading.value = false
}

// 续播泡上的反悔钮：报一个计数给播放器，它收到就跳 0 接着播；泡同时收掉，不留下次的悬念
const restartTick = ref(0)
function restartFromStart() {
  restartTick.value += 1
  clearResumeTip()
}

async function loadResumePoint(videoId, pageIndex) {
  resumeSec.value = 0
  clearResumeTip()
  try {
    const res = await fetch(`/api/videos/${videoId}/progress`)
    const p = res.ok ? await res.json() : null
    if (!p) return // 没看过（或后端没应答）：从头播就是正确答案
    if (Number(p.pageIdx) !== pageIndex) return
    const sec = Number(p.positionSec)
    const dur = Number(p.durationSec)
    if (!Number.isFinite(sec) || sec <= 10) return
    if (Number.isFinite(dur) && dur > 0 && sec >= dur * 0.95) return
    resumeSec.value = sec
    resumeTip.value = { sec }
    // 五秒自 fade：先淡出去再从 DOM 里拿掉，不引新依赖。
    resumeTipFadeTimer = setTimeout(() => {
      resumeTipFading.value = true
    }, 4500)
    resumeTipGoneTimer = setTimeout(() => {
      clearResumeTip()
    }, 5000)
  } catch {
    /* 后端打盹：不弹不闹，从头播 */
  }
}

// 泡上那个时间：分:秒，过小时补时（与播放器里的口径一致）。
function clockOf(sec) {
  const n = Math.max(0, Math.floor(Number(sec) || 0))
  const pad = (x) => String(x).padStart(2, '0')
  const h = Math.floor(n / 3600)
  return h > 0 ? `${h}:${pad(Math.floor((n % 3600) / 60))}:${pad(n % 60)}` : `${pad(Math.floor(n / 60))}:${pad(n % 60)}`
}

// ─── 加入合集：成员待遇。面板里勾一勾就进出抽屉；没抽屉当场建。
// 后端 /api/member/collections?bvid= 回我的抽屉并带 contains 标记，勾选即反向操作。
const collectionsOpen = ref(false)
const myCollections = ref(null) // null = 没拉到（在飞）；[] = 确实没建过
const collBusy = ref(false)
const collNotice = ref(null)
const newCollName = ref('')

async function toggleCollections() {
  collectionsOpen.value = !collectionsOpen.value
  if (collectionsOpen.value && myCollections.value === null) await loadMyCollections()
}

async function loadMyCollections() {
  collBusy.value = true
  try {
    const bvid = encodeURIComponent(data.value?.bvid || '')
    const res = await fetch(`/api/member/collections?bvid=${bvid}`)
    const d = res.ok ? await res.json().catch(() => null) : null
    myCollections.value = Array.isArray(d?.collections) ? d.collections : []
  } catch {
    myCollections.value = []
  } finally {
    collBusy.value = false
  }
}

async function toggleCollection(c) {
  if (collBusy.value || !data.value?.bvid) return
  collBusy.value = true
  collNotice.value = null
  try {
    const bvid = encodeURIComponent(data.value.bvid)
    let res
    if (c.contains) {
      res = await fetch(`/api/member/collections/${c.id}/items/${bvid}`, { method: 'DELETE' })
    } else {
      res = await fetch(`/api/member/collections/${c.id}/items`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ bvid: data.value.bvid }),
      })
    }
    const d = await res.json().catch(() => null)
    if (!res.ok) collNotice.value = { kind: 'err', text: d?.message || '抽屉没应' }
    await loadMyCollections()
  } catch {
    collNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    collBusy.value = false
  }
}

async function createAndAdd() {
  const name = newCollName.value.trim()
  if (!name || collBusy.value || !data.value?.bvid) return
  collBusy.value = true
  collNotice.value = null
  try {
    const res = await fetch('/api/member/collections', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name }),
    })
    const d = await res.json().catch(() => null)
    if (!res.ok) {
      collNotice.value = { kind: 'err', text: d?.message || '抽屉建不起来' }
      return
    }
    newCollName.value = ''
    const add = await fetch(`/api/member/collections/${d.id}/items`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ bvid: data.value.bvid }),
    })
    if (!add.ok) {
      const ad = await add.json().catch(() => null)
      collNotice.value = { kind: 'err', text: ad?.message || '抽屉建好了，但这部没进去' }
    }
    await loadMyCollections()
  } catch {
    collNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    collBusy.value = false
  }
}

// ─── 访客下载：播放器报什么编码就下什么编码，主版本缺省 ───
const playerCodec = ref(null)

const downloadInfo = computed(() => {
  const p = currentPage.value
  if (!p) return null
  const codec = playerCodec.value ?? p.codec ?? null
  if (codec && codec !== p.codec) {
    const row = (p.codecVersions ?? []).find((c) => c.codec === codec && c.status === 'Arrived')
    return row ? { codec, bytes: row.bytes ?? null } : null
  }
  return { codec, bytes: p.mainBytes ?? null }
})

function fmtDlBytes(b) {
  if (!b) return ''
  const units = ['B', 'KB', 'MB', 'GB']
  let v = b
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  return `${v >= 100 || i === 0 ? Math.round(v) : v.toFixed(1)}${units[i]}`
}

function downloadCurrent() {
  const p = currentPage.value
  if (!p || !downloadInfo.value) return
  const params = new URLSearchParams({ pageIdx: String(p.index) })
  if (downloadInfo.value.codec && downloadInfo.value.codec !== p.codec) {
    params.set('codec', downloadInfo.value.codec)
  }
  const a = document.createElement('a')
  a.href = `/api/videos/${data.value.id}/download?${params}`
  a.download = ''
  document.body.appendChild(a)
  a.click()
  a.remove()
}

// ─── 接下来播放：同 UP 优先的站内推荐，随 BV 切换刷新 ───
const related = ref([])

// 自动连播：本页（含全部 P）播完自动接接下来播放第一条；开关记忆在本地
const autoRelated = ref(localStorage.getItem('lbs.autoRelated') !== '0')
watch(autoRelated, (v) => localStorage.setItem('lbs.autoRelated', v ? '1' : '0'))

function playNextVideo() {
  const next = related.value.find((v) => v.bvid && v.bvid !== route.params.bvid)
  if (next) router.push(`/watch/${next.bvid}`)
}

// 代次护城：现在每 BV 只取一次，但换片时旧请求迟到落地会把上一支的推荐流盖到新页上
// （自动连播取的就是 related[0]，盖一下就会接错片），所以谁最新谁写。
let relatedSeq = 0

async function loadRelated(id) {
  const seq = ++relatedSeq
  try {
    const res = await fetch(`/api/videos/${id}/related`)
    const body = res.ok ? await res.json() : []
    if (seq === relatedSeq) related.value = body
  } catch {
    if (seq === relatedSeq) related.value = []
  }
}

// ─── 站内弹幕：存分站库，刷新不丢；自己发的点一下可删，别人发的点一下举报 ───
const localDanmaku = ref([])
const reportedDmIds = ref(new Set()) // 本页已经举报过的弹幕：重拉列表也不让钮变回「举报这条」
const reportingDmIds = ref(new Set()) // 在飞的那几发：网络没回来前连点也不重发

async function loadLocalDanmaku() {
  if (!data.value) return
  try {
    const res = await fetch(`/api/videos/${data.value.id}/danmaku`)
    localDanmaku.value = res.ok ? await res.json() : []
    // 服务端不存举报态（举报进的是 danmaku_reports），这一页的举报印记得自己贴回去。
    for (const d of localDanmaku.value) {
      if (reportedDmIds.value.has(d.id)) d.reported = true
    }
  } catch {
    localDanmaku.value = []
  }
}

const localForPage = computed(() => {
  const idx = currentPage.value?.index ?? 0
  return localDanmaku.value.filter((d) => d.pageIdx === idx)
})

async function onSendDanmaku({ text, pageIdx, t }) {
  if (!data.value) return
  try {
    const res = await fetch(`/api/videos/${data.value.id}/danmaku`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pageIdx, t, text }),
    })
    const body = await res.json().catch(() => null)
    if (res.ok && body?.id) {
      // 服务端把身份一并算好回过来：刚发的这条立刻挂自己的脸与冠，不用等下一轮重拉。
      localDanmaku.value.push({
        id: body.id,
        pageIdx,
        t,
        text,
        fromOwner: body.fromOwner === true,
        face: typeof body.face === 'string' && body.face ? body.face : null,
        canDelete: true,
      })
    }
  } catch {
    /* 后端没应答就静默：本地体验优先，不弹窗打扰看片 */
  }
}

async function onDeleteDanmaku(id) {
  localDanmaku.value = localDanmaku.value.filter((d) => d.id !== id)
  try {
    await fetch(`/api/videos/danmaku/${id}`, { method: 'DELETE' })
  } catch {
    /* 删不动拉倒，重进页面会自愈 */
  }
}

// 举报只递单给站长：不预先藏掉这条（pending 的弹幕照旧飘），也不弹 alert。
async function onReportDanmaku(id) {
  if (!data.value || reportedDmIds.value.has(id) || reportingDmIds.value.has(id)) return
  const item = localDanmaku.value.find((d) => d.id === id)
  if (!item) return
  reportingDmIds.value = new Set([...reportingDmIds.value, id])
  try {
    const res = await fetch(`/api/videos/${data.value.id}/danmaku/${id}/report`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({}), // 最小闭环不填理由（reason 可选，上限 50 字由服务端把关）
    })
    if (res.ok) {
      item.reported = true
      reportedDmIds.value = new Set([...reportedDmIds.value, id])
    }
  } catch {
    /* 后端没应答就静默：与删除的乐观风格一致，不弹窗打扰看片 */
  } finally {
    reportingDmIds.value = new Set([...reportingDmIds.value].filter((v) => v !== id))
  }
}

// ─── 投稿人自管：重拉 B 站快照（评论区 + 弹幕）与删自己的稿 ───
// 可见性由服务端算好的 data.canManage 说了算（投稿人本人 ∥ 站长），前端只是藏钮，真门在端点里。
const manageBusy = ref(false)
const removing = ref(false)
const commentsRev = ref(0)
const notice = ref(null)
let noticeTimer = null

function showNotice(kind, text) {
  notice.value = { kind, text }
  if (noticeTimer) clearTimeout(noticeTimer)
  noticeTimer = setTimeout(() => (notice.value = null), 7000)
}

// ─── 安利这个片子：与动态橱窗共用一副 ShareDialog（视频形态），登录门槛在钮上就把关。
// 封面拿馆藏本地图优先，本地没图才退热链（与卡阵同一口径）；站内安利不动 B 站分享计数（那数是 view 接口真采快照）。
const shareFor = ref(null)

function openShare() {
  if (!member.value) {
    loginDialogOpen.value = true
    return
  }
  if (!data.value) return
  shareFor.value = {
    type: 'video',
    bvid: data.value.bvid,
    title: data.value.title || data.value.bvid,
    cover: coverSrc.value,
    upName: data.value.upName,
  }
}

function onShared(message) {
  shareFor.value = null
  showNotice('ok', message ?? '已塞进对方信箱')
}

// ─── 分享海报：从现成 watch 数据组一张海报的 model，画笔（utils/videoPoster.js）自己判同源。
// 登录闸与 openShare 同一个口径：没站籍先弹登录窗，不把调参台递到门外人手上。全程零上送。
const posterFor = ref(null)

// 时长优先吃 payload：watch 载荷顶层没 durationText（那是 VideoEntry / 分 P 上的字段），
// 就拿当前分 P 那一份；还没有就问播放器里的 <video>（已元数据就绪才有值），拿不到就不画时长片。
function posterDuration(d) {
  const pages = d.pages ?? []
  const page = pages.find((p) => p.index === activeIndex.value) ?? pages[0]
  const fromPayload = String(page?.durationText ?? '').trim()
  if (fromPayload) return fromPayload
  const sec = Number(document.querySelector('video')?.duration)
  if (!Number.isFinite(sec) || sec <= 0) return ''
  const total = Math.round(sec)
  const pad = (n) => String(n).padStart(2, '0')
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = total % 60
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`
}

function openPosterStudio() {
  if (!member.value) {
    loginDialogOpen.value = true
    return
  }
  if (!data.value) return
  const d = data.value
  posterFor.value = {
    bvid: d.bvid,
    title: d.title || d.bvid,
    description: d.description || '',
    coverUrl: d.coverUrl || '', // 原样递：画笔里 sameOriginSrc 把关，热链不画
    upName: d.upName || '',
    upFace: d.upFace || '',
    upMid: d.upMid ?? null,
    viewCountText: d.viewCountText || '',
    danmakuCount: d.danmakuCount ?? null,
    statLike: d.statLike ?? null,
    durationText: posterDuration(d),
  }
}

onBeforeUnmount(() => {
  if (noticeTimer) clearTimeout(noticeTimer)
})

// 弹幕覆写在同一路径，DanmakuLayer 只认 src 变化；换个 ?v= 既顶它重取，又绕过 /media 的 ETag 缓存。
function bumpDanmaku() {
  const page = data.value?.pages?.find((p) => p.index === activeIndex.value)
  if (page?.danmakuUrl) {
    page.danmakuUrl = `${String(page.danmakuUrl).split('?')[0]}?v=${Date.now()}`
  }
}

async function refreshSnapshots() {
  if (manageBusy.value || !data.value) return
  manageBusy.value = true
  // 两个动作都挤在这枚钮上（补采评论 + 重拉弹幕）。逻辑住在 utils/videoActions.js 的 refetchArchive，
  // 稿件设置面板里拆成两颗各点一样，走的也是那一份 —— 一处逻辑、两处入口，不抄第二遍。
  const res = await refetchArchive(data.value.id)
  if (res.commentsOk) commentsRev.value += 1
  if (res.danmakuOk) bumpDanmaku()
  showNotice(res.kind, res.text)
  manageBusy.value = false
}

async function removeSubmission() {
  if (!data.value || removing.value) return
  removing.value = true
  // 问那一句（confirmStore 的措辞单一出处）+ DELETE 都在 removeVideoEntry 里，与设置面板同一条
  const res = await removeVideoEntry(data.value.id, data.value.title || data.value.bvid)
  if (res.outcome === 'removed') {
    // 这条馆藏已经不存在了，把用户送回首页卡阵，别留在空页面上。
    router.push('/')
    return
  }
  removing.value = false
  if (res.outcome === 'failed') showNotice('err', res.text)
}

// ─── 稿件设置窗：左缘第二颗把手开的（面板本体在 components/VideoSettingsDialog.vue）。
// 摆不摆这颗钮看服务端算好的 data.canManage（与那两枚自管钮同一把钥匙），真门在端点里。
const settingsOpen = ref(false)

function openSettings() {
  settingsOpen.value = !settingsOpen.value
}

// 面板里存好了 / 动作跑完了：重拉 /api/watch/{bvid}，改过的栏一律以服务端为准（前端不自改本地对象）。
// 顺序要紧：bumpDanmaku 改的就是 data.pages 里那份 danmakuUrl，loadWatch 会把整页换掉它，得排在后面。
async function onSettingsSaved(res) {
  await loadWatch(true)
  if (res?.commentsOk) commentsRev.value += 1
  if (res?.danmakuOk) bumpDanmaku()
  // 重拉落地之后才打戳：拿服务端那份新 coverUrl 拼，不拿旧的拼一半。
  // 只有 media 那一发真把封面换掉了才打（mediaReplaced 是共享层按 body.cover === 'replaced' 算好的）；
  // kept / failed / 补采评论 / 重拉弹幕 / 存好 —— 一律不打，不该白刷一次图。
  if (res?.mediaReplaced) coverStamp.value = Date.now()
}

// 窗开着的时候钥匙被服务端收走（重拉发现 canManage 变假：稿件转了归属、席位改了）：窗跟着关，
// 不留在里按什都吃 403 的一窗灰。左缘那颗把手本来就随 canManage 收起，两处得同进退 ——
// 同脾气的先例是加档被驳回时 revokePendingCodec 把乐观态催回来（以服务端那份为准）。
// 只认 `=== false`：data 换血中途是 undefined（未载入 / 吃门禁），那时不该动手。
watch(
  () => data.value?.canManage,
  (can) => {
    if (settingsOpen.value && can === false) {
      settingsOpen.value = false
      showNotice('warn', '这篇稿子已经不归你管了，设置窗先收起来')
    }
  },
)

// ─── 编码加档：点单后轮询 watch，直到该编码所有分 P 都脱离 Downloading ───
let codecPoll = null

// 播放器点加档时已本地显了「进站中」；这一发要是被驳回，得靠这个 prop 把乐观态催回来。
// 连着整个对象 + 带序号：同一编码连着驳回两次也要能再触发一次子组件的清理。
const pendingRevoke = ref(null)
let pendingRevokeSeq = 0

function revokePendingCodec(codec) {
  pendingRevoke.value = { codec, seq: ++pendingRevokeSeq }
}

async function requestCodec(codec) {
  if (!data.value) return
  let res
  try {
    res = await fetch(`/api/videos/${data.value.id}/codecs`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ codec }),
    })
  } catch {
    // 后端没应答：把刚许出去的「进站中」收回来，不启动轮询，也不能静悄悄
    revokePendingCodec(codec)
    showNotice('err', '分站后端没有应答')
    return
  }
  const body = await res.json().catch(() => null)
  if (!res.ok) {
    // 409「已在站或已在途」这类拒绝以前被丢掉了，用户只看到钮没反应；现在照实报，同时撤乐观态
    revokePendingCodec(codec)
    showNotice('err', body?.message ?? `加档没排上（HTTP ${res.status}）`)
    return
  }
  if (body?.message) showNotice('ok', body.message) // 后端本身就是「AVC 版本已排队进站（1 个分 P）」这种人话
  startCodecPoll(codec)
}

// codec 为 null = 不限编码：只要还有在途的加档行就一直刷（刷新页面后自动继上用的就是这一档）
function startCodecPoll(codec = null) {
  if (codecPoll) clearInterval(codecPoll)
  let ticks = 0
  const tick = async () => {
    ticks += 1
    await loadWatch(true)
    const rows = (data.value?.pages ?? []).flatMap((p) => p.codecVersions ?? []).filter((c) => !codec || c.codec === codec)
    const settled = rows.length > 0 && rows.every((c) => c.status !== 'Downloading')
    if (settled || ticks > 240) {
      clearInterval(codecPoll)
      codecPoll = null
    }
  }
  codecPoll = setInterval(tick, 5000)
  tick() // 点完立刻跑第一发：排上队就该在一秒内看到动静，不是干等 5 秒
}

// 刷新页面会把轮询连同这份记忆一并丢掉：只要服务端还挂着 Downloading 的加档行就自动继上，
// 否则状态会冻在刷新那一刻，直到用户再点一次。单例 codecPoll 守住不会开出第二个 interval。
function ensureCodecPoll() {
  if (codecPoll) return
  const inflight = (data.value?.pages ?? []).some((p) => (p.codecVersions ?? []).some((c) => c.status === 'Downloading'))
  if (inflight) startCodecPoll()
}

// ─── 播放行为上报：只走白名单事件，失败一律静默（看片体验优先）───
let openReported = false
let heartTimer = null

function reportEvent(name, payload = null) {
  const bvid = route.params.bvid
  if (!bvid) return
  fetch('/api/events/ingest', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ events: [{ name, subjectType: 'video', subjectId: String(bvid), payload }] }),
  }).catch(() => {})
}

// play/pause 不冒泡，挂在 document 上用捕获监听兜住播放器内部的 <video>
function onMediaPlay() {
  reportEvent('watch.play')
}

function onMediaPause() {
  reportEvent('watch.pause')
}

onBeforeUnmount(() => {
  if (codecPoll) clearInterval(codecPoll)
  clearResumeTip()
  document.removeEventListener('play', onMediaPlay, true)
  document.removeEventListener('pause', onMediaPause, true)
  if (heartTimer) {
    clearInterval(heartTimer)
    heartTimer = null
  }
  reportEvent('watch.leave')
})

onMounted(() => {
  loadWatch()
  document.addEventListener('play', onMediaPlay, true)
  document.addEventListener('pause', onMediaPause, true)
  // 播放中每 30 秒一条心跳，带当前秒数；没在播就不报，不充数
  heartTimer = setInterval(() => {
    const v = document.querySelector('video')
    if (v && !v.paused && !v.ended) reportEvent('watch.heartbeat', { t: Math.floor(v.currentTime) })
  }, 30000)
})
watch(() => route.params.bvid, () => {
  openReported = false
  coverStamp.value = 0 // 换一部片子：上一部的戳不该跟着新的走（普通浏览就该是条干净的 URL）
  loadWatch()
})

// 播放器壳宽（VideoPlayer 量好的）：两栏内容用它封顶，保证不超出视频卡片
const playerShellW = ref(0)

// 封面唯一取数口：watch 载荷里的 coverUrl 已是读侧拼好的「本地原图优先」成品（/media/… 或 B 站直链），
// 播放器开场、封面浮窗、安利卡三处共用这一份。曾经这里写的是 data.localCover || data.coverUrl，
// 但 watch 载荷压根不下发 localCover（那是列表用的 VideoEntry 字段），前半支永远轮不到。
const coverSrc = computed(() => data.value?.coverUrl || '')

// ─── 换完封面得真让屏上那张重请一次 ───
// 封面件是原地覆盖同一个 rel：/media/...jpg 这个 URL 一个字不变，data.coverUrl 字符串也没变，
// Vue 就不会重设 <img> 的 src —— 元素根本不重新发请求，后端那个 /media 的 no-cache 也就无从生效
// （no-cache 管的是「下次发了要不要问服务器」，不是「逼它发」）。于是明明换掉了、屏上还是旧图，
// 非得 F5。所以只给「这一发真换掉了封面」那一回打一枚时间戳，让 src 换个字串、逼它复核一次。
// stamp 为 0（从没见过 cover === 'replaced'）时不拼查询串：URL 与改动前逐字节相同，普通浏览不带查询串。
// 只喂上屏的那两处（播放器开场图与封面浮窗）；安利卡与钮在不在那些判断仍走 coverSrc 那份干净 URL，
// 不往人家分享与下载的字串里掺 ?r=。
const coverStamp = ref(0)
const coverFresh = computed(() => {
  const src = coverSrc.value
  if (!src || !coverStamp.value) return src // 没换过（或没封面）：原 URL 递出去，一个字节不多
  return `${String(src).split('?')[0]}?r=${coverStamp.value}` // 口径照 bumpDanmaku：先把旧查询串剔干再贴新的
})
</script>

<template>
  <div class="min-h-screen">
    <TopBar />

    <main class="mx-auto max-w-[1600px] px-6 py-5">
      <p v-if="loading" class="py-20 text-center text-sm text-inkSub">正在打开馆藏……</p>

      <!-- 访客吃门禁：B 站式迷路小电视，指路去站籍 -->
      <EmptyTv
        v-else-if="gateState === 'login'"
        title="啊叻？想看视频？"
        subtitle="看片、弹幕、评论区都是分站成员的待遇——花两秒领个席位，或直接把门钥匙输进来"
      >
        <button
          class="rounded-lg bg-gradient-to-r from-stationPink to-[#f994b6] px-6 py-2 text-sm font-medium text-white shadow-md shadow-stationPink/30 transition hover:-translate-y-px hover:shadow-lg hover:shadow-stationPink/40 active:translate-y-0"
          @click="loginDialogOpen = true"
        >
          快速登录
        </button>
        <RouterLink
          to="/register"
          class="rounded-lg border border-lineSoft px-6 py-2 text-sm text-inkSub transition hover:border-stationPink hover:text-stationPink"
        >
          注册个站籍
        </RouterLink>
      </EmptyTv>

      <!-- 馆藏确无此片：同款小电视，给出路与投稿两条路 -->
      <EmptyTv
        v-else-if="gateState === 'missing'"
        title="啊叻？找不到这部视频"
        subtitle="馆藏里没有这个 BV 号，或者已经被删了"
      >
        <button
          class="rounded-lg bg-gradient-to-r from-stationPink to-[#f994b6] px-6 py-2 text-sm font-medium text-white shadow-md shadow-stationPink/30 transition hover:-translate-y-px hover:shadow-lg hover:shadow-stationPink/40 active:translate-y-0"
          @click="router.push('/')"
        >
          回分站大厅
        </button>
        <button
          v-if="member"
          class="rounded-lg border border-lineSoft px-6 py-2 text-sm text-inkSub transition hover:border-stationPink hover:text-stationPink"
          @click="submitDialogOpen = true"
        >
          投稿这条视频
        </button>
      </EmptyTv>

      <template v-else-if="data">
        <!-- B 站宽屏模式同款：播放器单独通栏，其下才分两栏。
             小泡的锚定要贴着播放器：单独给播放器+泡套一层 tight relative，
             外层相对定位包住整段时 bottom 会从页底算，泡会飞出去看不见。 -->
        <div>
          <div class="relative">
          <VideoPlayer
            :page="currentPage"
            :status="data.status"
            :title="data.title"
            :bvid="data.bvid"
            :cover="coverFresh"
            :aid="data.aid"
            :video-id="data.id"
            :resume-sec="resumeSec"
            :restart-tick="restartTick"
            :danmaku-count="data.danmakuCount"
            :has-next-page="hasNextPage"
            :auto-related="autoRelated"
            :local-items="localForPage"
            :pending-revoke="pendingRevoke"
            @next="playNextPage"
            @next-video="playNextVideo"
            @request-codec="requestCodec"
            @shell-width="playerShellW = $event"
            @codec-change="playerCodec = $event"
            @codec-arrived="loadWatch(true)"
            @send-danmaku="onSendDanmaku"
            @delete-danmaku="onDeleteDanmaku"
            @report-danmaku="onReportDanmaku"
          />

          <!-- 左缘那颗「看封面」浮窗：贴视口左缘、平时只露右半颗（藏法照 B 站那两枚立方体）。
               挂在包 VideoPlayer 的那层 relative 里而不是它肚子里：VideoPlayer 根带 isolate，子树里的 z 出不去，
               而这张卡要 z-[60] 压弹窗。 -->
          <CoverPeek
            :cover="coverFresh"
            :bvid="data.bvid"
            :title="data.title"
          />

          <!-- 左缘第二颗把手：稿件设置。与「看封面」共用 SideHandle（那颗钮的样式全站只有一份），
               靠 offset 往下面错开一格（一颗钮的直径 36 + 12 呼吸），两颗不打架；
               没封面时第一颗不出现，这一颗就自己归位居中。只在 data.canManage 为真时摆 —— 
               与后端那把自管钥匙同一个判据，前端只是藏钮，真门在端点里。 -->
          <SideHandle
            v-if="data.canManage"
            label="稿件设置"
            :offset="coverSrc ? 48 : 0"
            :active="settingsOpen"
            :on-click="openSettings"
          >
            <template #icon>
              <!-- 调校图标（三道杆两枚旋钮）：描边 svg 直接内联，stroke 走 currentColor 跟着钮色走 -->
              <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
                <path d="M4 7h8M17 7h3M4 17h4M13 17h7" />
                <circle cx="14.5" cy="7" r="2.3" />
                <circle cx="10.5" cy="17" r="2.3" />
              </svg>
            </template>
          </SideHandle>

          <!-- 续播小泡：站长亲手拖过定位（EditHere 反馈）：贴播放器壳左缘、进度条上方一格。
               横向锁在壳子而非外层容器：播放器限宽居中，窗口越宽容器与壳的左缘裂缝越大，锚容器会出框；
               playerShellW 是 VideoPlayer 报来的实测壳宽，壳居中故其左缘在 50% - 壳宽/2。未报到前退 left-[15px]。10 秒嫌久、6 秒仍嫌久，现行 5 秒自 fade，pointer-events-none 不挡点画面 -->
          <div
            v-if="resumeTip"
            class="pointer-events-none absolute bottom-[122px] left-[15px] z-20 whitespace-nowrap rounded-full border border-white/15 bg-[#1b1d23]/95 px-3.5 py-1.5 text-xs text-white/85 shadow-lg backdrop-blur transition-opacity duration-500"
            :class="resumeTipFading ? 'opacity-0' : 'opacity-100'"
            :style="playerShellW ? { left: `calc(50% - ${playerShellW / 2 - 15}px)` } : undefined"
          >
            上次看到 <span class="font-mono text-white/95">{{ clockOf(resumeTip.sec) }}</span>，已为你继续
            <!-- 反悔钮：外层 pointer-events-none 不挡画面，这颗自己把手伸回来（子元素可重新开点） -->
            <button
              class="pointer-events-auto ml-2 cursor-pointer font-medium text-stationPink transition hover:opacity-80 active:scale-95"
              @click="restartFromStart"
            >
              从头开始
            </button>
          </div>
          </div>

          <div class="mt-[30px] flex justify-center">
            <div
              class="flex w-full flex-col items-start gap-6 lg:flex-row"
              :style="playerShellW ? { maxWidth: `${playerShellW}px` } : undefined"
            >
            <!-- 左栏：标题→数据行→互动栏→简介→评论 -->
            <div class="min-w-0 flex-1">
              <!-- 标题与档案：B 站同款，标题重、数据行带小图标疏朗排开；私有药丸钉在名右，标题自己换行、药丸不跟着挤没 -->
              <h1 class="flex flex-wrap items-center gap-x-2.5 text-[22px] font-semibold leading-8 text-inkMain">
                <span class="min-w-0">{{ data.title ?? data.bvid }}</span>
                <VisibilityPill :visibility="data.visibility" />
              </h1>
              <div class="mt-2 flex flex-wrap items-center gap-x-5 gap-y-1.5 text-xs text-inkFaint">
                <span v-if="data.viewCountText" class="flex items-center gap-1.5">
                  <PlayCountIcon class="h-3.5 w-3.5" />
                  {{ data.viewCountText }}
                </span>
                <span class="flex items-center gap-1.5">
                  <DanmakuCountIcon class="h-3.5 w-3.5" />
                  {{ data.danmakuCount ?? 0 }}
                </span>
                <span v-if="data.publishText">{{ data.publishText }}</span>
                <span>{{ data.bvid }}</span>
                <span v-if="data.upName">UP 主：{{ data.upName }}</span>
                <span>入站：{{ new Date(data.createdAtUtc).toLocaleString('zh-CN') }}</span>
              </div>

              <!-- B 站式互动栏：真数来自 view 接口快照，采不到显「—」；右侧访客下载 -->
              <div class="mt-6 flex items-center justify-between gap-4 border-t border-b border-black/5 dark:border-white/10 py-2">
                <VideoActions :stat="{ like: data.statLike, coin: data.statCoin, favorite: data.statFavorite, share: data.statShare, reply: data.statReply }" :store-key="data.bvid" @share="openShare" @share-poster="openPosterStudio" />
                <div class="relative flex shrink-0 items-center gap-2">
                  <!-- 投稿人自管两钮（往左提一位，让「合集」紧贴下载胶囊的左侧）：白底描边，hover 才上色，不抢下载胶囊的风头 -->
                  <template v-if="data.canManage">
                    <button
                      class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full border border-black/10 dark:border-white/10 text-inkSub transition hover:border-stationBlue hover:text-stationBlue disabled:cursor-not-allowed disabled:opacity-40"
                      :disabled="manageBusy"
                      :title="HINT_FETCH_COMMENTS"
                      @click="refreshSnapshots"
                    >
                      <svg v-if="!manageBusy" viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2">
                        <path d="M21 12a8 8 0 0 1-11.6 7.1L4 21l1.9-5.4A8 8 0 1 1 21 12z" stroke-linecap="round" stroke-linejoin="round" />
                      </svg>
                      <svg v-else viewBox="0 0 24 24" class="h-4 w-4 animate-spin" fill="none" stroke="currentColor" stroke-width="2">
                        <path d="M12 3a9 9 0 1 0 9 9" stroke-linecap="round" />
                      </svg>
                    </button>
                    <button
                      class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full border border-black/10 dark:border-white/10 text-inkSub transition hover:border-rose-500 hover:text-rose-500 disabled:cursor-not-allowed disabled:opacity-40"
                      :disabled="removing"
                      :title="HINT_DELETE_VIDEO"
                      @click="removeSubmission"
                    >
                      <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2">
                        <path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2m3 0-1 13a1 1 0 0 1-1 1H8a1 1 0 0 1-1-1L6 7" stroke-linecap="round" stroke-linejoin="round" />
                      </svg>
                    </button>
                  </template>
                  <!-- 加入合集：成员就地收纳，抽屉不够当场建一个 -->
                  <div v-if="member" class="relative">
                    <button
                      class="flex h-9 items-center gap-1.5 rounded-full border px-3 text-xs transition"
                      :class="collectionsOpen ? 'border-stationPink text-stationPink' : 'border-black/10 dark:border-white/10 text-inkSub hover:border-stationPink hover:text-stationPink'"
                      title="把这部收进我的合集"
                      @click="toggleCollections"
                    >
                      <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
                        <path d="M4 6h16M4 12h10M4 18h10" />
                        <path d="M17 14v6M14 17h6" />
                      </svg>
                      合集
                    </button>
                    <div v-if="collectionsOpen" class="absolute right-0 top-11 z-30 w-64 rounded-xl border border-black/10 dark:border-white/10 bg-cardBg p-3 shadow-xl">
                      <p v-if="collBusy && !myCollections?.length" class="py-4 text-center text-[12px] text-inkFaint">正在翻你的抽屉……</p>
                      <p v-else-if="!myCollections?.length" class="py-4 text-center text-[12px] text-inkFaint">你还没有抽屉，下面建一个</p>
                      <label
                        v-for="c in myCollections"
                        :key="c.id"
                        class="flex cursor-pointer items-center gap-2 rounded-lg px-2 py-1.5 text-[12.5px] text-inkMain transition hover:bg-stationPink/5"
                      >
                        <input type="checkbox" :checked="c.contains" :disabled="collBusy" class="accent-[#fb7299]" @change="toggleCollection(c)" />
                        <span class="min-w-0 flex-1 truncate">{{ c.name }}</span>
                        <span class="font-mono text-[10px] text-inkFaint">{{ c.itemCount }}</span>
                      </label>
                      <div class="mt-2 flex items-center gap-1.5 border-t border-black/5 dark:border-white/10 pt-2">
                        <input
                          v-model="newCollName"
                          class="min-w-0 flex-1 rounded-lg border border-black/15 dark:border-white/10 px-2 py-1.5 text-[12px] outline-none focus:border-stationPink"
                          placeholder="新抽屉名字（30 字内）"
                          maxlength="30"
                          @keyup.enter="createAndAdd"
                        />
                        <button class="shrink-0 rounded-lg bg-stationPink px-2.5 py-1.5 text-[12px] text-white transition hover:opacity-90 disabled:opacity-50" :disabled="collBusy" @click="createAndAdd">建</button>
                      </div>
                      <p v-if="collNotice" :class="collNotice.kind === 'ok' ? 'text-emerald-600' : 'text-rose-500'" class="mt-1.5 text-[11px]">{{ collNotice.text }}</p>
                    </div>
                  </div>
                  <button
                    class="flex shrink-0 items-center gap-1.5 rounded-full border border-stationPink px-4 py-1.5 text-sm text-stationPink transition hover:bg-stationPink hover:text-white disabled:cursor-not-allowed disabled:border-black/10 dark:disabled:border-white/10 disabled:text-inkFaint disabled:hover:bg-transparent"
                    :disabled="!currentPage?.videoUrl || !downloadInfo"
                    :title="downloadInfo ? `把正在看的版本（${(downloadInfo.codec ?? '主版本').toUpperCase()}）存到本地` : '这一 P 还没下载，没东西可拿'"
                    @click="downloadCurrent"
                  >
                    <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                      <path d="M12 4v11m0 0 4-4m-4 4-4-4M4 19h16" />
                    </svg>
                    <span>
                      下载{{ downloadInfo?.codec ? ' · ' + downloadInfo.codec.toUpperCase() : '' }}{{ downloadInfo?.bytes ? ' · ' + fmtDlBytes(downloadInfo.bytes) : '' }}
                    </span>
                  </button>
                </div>
              </div>

              <p
                v-if="data.description"
                class="mt-5 whitespace-pre-line text-sm leading-7 text-inkSub"
              >
                <span v-html="mentionHtml(data.description)"></span>
              </p>

              <CommentSection :key="commentsRev" class="mt-5 border-t border-black/5 dark:border-white/10 pt-5" :src="data.commentsUrl" :video-id="data.id" :up-mid="data.upMid" />
            </div>

            <!-- 右栏（不吸顶，随页滚动）：UP 卡、弹幕列表、分 P 列表、接下来播放 -->
            <aside class="w-full shrink-0 space-y-3 lg:-mt-3 lg:w-[374px] xl:w-[374px]">
              <UpCard
                v-if="data.upName || data.staff?.length"
                :up-name="data.upName"
                :up-mid="data.upMid"
                :up-face="data.upFace"
                :up-sign="data.upSign"
                :up-fans="data.upFans"
                :staff="data.staff"
              />
              <DanmakuList v-if="currentPage?.danmakuUrl" :src="currentPage.danmakuUrl" :count="data.danmakuCount" />
              <PageList
                v-if="data.pages.length > 1"
                :pages="data.pages"
                :active-index="activeIndex"
                @select="selectPage"
              />
              <UpNextList v-if="related.length" :items="related" :autoplay="autoRelated" @update:autoplay="autoRelated = $event" />
            </aside>
            </div>
          </div>
        </div>
      </template>

      <ShareDialog v-if="shareFor" :target="shareFor" @close="shareFor = null" @done="onShared" />

      <!-- 视频分享海报调参台：与安利卡同区，弹层自己 Teleport 到 body -->
      <SharePosterStudio v-if="posterFor" :model="posterFor" @close="posterFor = null" />

      <!-- 稿件设置：左缘第二颗把手开的。存好 / 档案动作跑完都走 onSettingsSaved 重拉 watch，
           面板不自己改本地对象（免得两处真相），改过的字与那几面覆写标一律等服务端那份回来 -->
      <VideoSettingsDialog
        v-if="settingsOpen && data"
        :data="data"
        @saved="onSettingsSaved"
        @close="settingsOpen = false"
      />
      <p
        v-if="notice"
        class="fixed bottom-6 left-1/2 z-50 max-w-[80vw] -translate-x-1/2 truncate rounded-full px-4 py-2 text-xs text-white shadow-lg"
        :class="notice.kind === 'err' ? 'bg-rose-500/90' : notice.kind === 'warn' ? 'bg-amber-500/90' : 'bg-inkMain/85'"
        :title="notice.text"
      >
        {{ notice.text }}
      </p>
    </main>
  </div>
</template>
