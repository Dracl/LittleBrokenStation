<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import DanmakuLayer from './DanmakuLayer.vue'
import BIcon from './BIcon.vue'
import DanmakuCountIcon from './DanmakuCountIcon.vue'
import { biPlay, biVolume, biSetting, biPip, biFull, biDmOn, biDmOff, biDmSet, biHiRes } from '../assets/bicons'
import { probeMp4 } from '../utils/mp4Meta'
import { subtitleTextToVtt } from '../utils/subtitleVtt'
import { loadAssText, buildHeat, HEAT } from '../utils/dmHeat'
import { member } from '../memberStore'
import { requestConfirm } from '../confirmStore'

const props = defineProps({
  page: { type: Object, default: null }, // { videoUrl, danmakuUrl, subtitleUrls, index }
  status: { type: Number, default: 0 },
  title: { type: String, default: '' },
  bvid: { type: String, default: '' },
  aid: { type: Number, default: 0 },
  videoId: { type: Number, default: 0 }, // 分站台账里的馆藏 id（进度上报的钥匙；没它就无处可报）
  canManage: { type: Boolean, default: false }, // 这稿的自管钥匙（服务端算好的 data.canManage：投稿人本人 ∥ 站长）：加档删钮拿它当尺
  resumeSec: { type: Number, default: 0 }, // 观看页算好的续播点（只在进页那一份，手动切 P 不再打断）
  restartTick: { type: Number, default: 0 }, // 续播泡上「从头开始」的指令计数：逢增跳 0
  hasNextPage: { type: Boolean, default: false },
  autoRelated: { type: Boolean, default: false }, // 自动连播：本页最后一 P 播完跳接下来播放首条
  danmakuCount: { type: Number, default: null }, // 全站已归档弹幕总数（watch 顶层字段）
  localItems: { type: Array, default: () => [] }, // 站内弹幕 [{id, pageIdx, t, text}]（已按当前 P 筛好）
  pendingRevoke: { type: Object, default: null }, // { codec, seq }：观看页的加档请求被驳回，来把乐观态撤回来
  cover: { type: String, default: null }, // 开场封面（观看页给的「本地原图优先」成品）：没播过之前盖在画面上，起播即淡出
})
const emit = defineEmits(['next', 'request-codec', 'shell-width', 'next-video', 'codec-change', 'send-danmaku', 'delete-danmaku', 'report-danmaku', 'codec-arrived'])

const root = ref(null)
const video = ref(null)
const trackEl = ref(null)

const playing = ref(false)
const waiting = ref(false)
// 卡顿自愈：waiting 持续不推进先自动重播一次；再不行看缓冲余量——缓冲区里有货却不走才算真死，
// 货还没到货就继续转圈（首载/大 seek 的慢缓冲不冤枉），几轮救不回才弹手动恢复钮。
// 关键：lastTickTime 是「卡住起点冻结的坐标」，只在 onMediaWait / manualUnstick 落笔，绝不在 timeupdate 刷；
// 否则正常播放时它被逐帧贴着当前进度、推进检测永远测到 ≈0，会把在放的视频也误判成停滞。
const stuckRecover = ref(false)
let stuckTimer = 0
let lastTickTime = 0
let seekTarget = null
let seekMergeTimer = 0

// 当前播放点后面还有没有 2 秒以上的缓冲存货（有货不推进才是真停滞）
function bufferHeadroom(v) {
  try {
    for (let i = 0; i < v.buffered.length; i++) {
      if (v.buffered.start(i) <= v.currentTime + 0.5 && v.buffered.end(i) >= v.currentTime + 2) return true
    }
  } catch {
    /* 老内核异常当没缓冲处理 */
  }
  return false
}

// 复审阶梯：第一轮 3 秒，后两轮各 8 秒；每轮先试一手 play()，缓冲不足就多给时间，三轮封顶才认死
function recheckStuck(roundsLeft) {
  clearTimeout(stuckTimer)
  stuckTimer = setTimeout(() => {
    const v = video.value
    if (!v || !playing.value) return
    if (v.currentTime - lastTickTime >= 0.1) {
      waiting.value = false
      return
    }
    if (bufferHeadroom(v) || roundsLeft <= 0) {
      stuckRecover.value = true
      logEvent(`停滞复审定罪 rounds=${roundsLeft} headroom=${bufferHeadroom(v)}`)
    } else {
      logEvent(`缓冲未到货，继续等 rounds=${roundsLeft}`)
      v.play().catch(() => {})
      recheckStuck(roundsLeft - 1)
    }
  }, roundsLeft === 3 ? 3000 : 8000)
}

function onMediaWait() {
  const v = video.value
  if (v) logEvent(`缓冲 rs=${v.readyState} ns=${v.networkState} seek=${v.seeking ? 1 : 0} t=${v.currentTime.toFixed(1)}`)
  // 冻结卡住基准点：往后所有「有没有推进」的判据都拿实时 currentTime 减这个点，真放了就会显著变大。
  if (v) lastTickTime = v.currentTime
  waiting.value = true
  stuckRecover.value = false
  // 素圈住在控制条里，条自隐会把圈一起藏走：真缓冲先来唤醒控制条，圈随条淡入。
  wakeControls()
  clearTimeout(stuckTimer)
  stuckTimer = setTimeout(tryUnstick, 4000)
}

function tryUnstick() {
  const v = video.value
  if (!v || !playing.value) return
  if (v.currentTime - lastTickTime > 0.3) {
    waiting.value = false
    return
  }
  logEvent('缓冲停滞，自动重试播放')
  v.play().catch(() => {})
  recheckStuck(3)
}

function onMediaPause() {
  playing.value = false
  waiting.value = false
  stuckRecover.value = false
  clearTimeout(stuckTimer)
  showControls.value = true
  reportProgress() // 一停就报：关页面前多半再没机会了，这一发最值当
}

function manualUnstick() {
  const v = video.value
  if (!v) return
  // 手动重试也重置基准点到「点击这一刻」，之后看有没有从这个点往前走。
  lastTickTime = v.currentTime
  stuckRecover.value = false
  waiting.value = true
  logEvent('手动恢复：尝试播放')
  v.play().catch(() => {
    waiting.value = false
  })
  clearTimeout(stuckTimer)
  stuckTimer = setTimeout(() => {
    const vv = video.value
    if (!vv || !playing.value) return
    if (vv.currentTime - lastTickTime >= 0.1) {
      waiting.value = false
      return
    }
    // play() 也救不活 → 媒体管线内部死锁，重载管线并回到原位。
    const t = vv.currentTime
    logEvent(`手动恢复无效，重载媒体管线 t=${t.toFixed(1)} rs=${vv.readyState} ns=${vv.networkState}`)
    vv.addEventListener('loadedmetadata', () => {
      const v3 = video.value
      if (!v3) return
      v3.currentTime = t
      v3.play().catch(() => {})
      stuckTimer = setTimeout(() => {
        const v4 = video.value
        if (v4 && playing.value && v4.currentTime - lastTickTime < 0.1) {
          stuckRecover.value = true
          logEvent('重载后仍未恢复，等待再次点击')
        }
      }, 4000)
    }, { once: true })
    vv.load()
  }, 3000)
}
// 音量记忆消毒：存过脏值（NaN/越界）就回默认 100%，记住 0 则视为静音态，两者永远一致。
const storedVolRaw = localStorage.getItem('lbs.player.volume')
const storedVol = Number(storedVolRaw)
const volume = ref(storedVolRaw !== null && Number.isFinite(storedVol) ? Math.min(1, Math.max(0, storedVol)) : 1)
const muted = ref(volume.value === 0)
const currentTime = ref(0)
const duration = ref(0)
const bufferedEnd = ref(0)

// ─── 弹幕高能条：本 P 馆藏 .ass 数桶，5s 平滑抹缓坡，轨道正上方画直方图 ───
// 数据走共享缓存每 P 只一发；切页/时长到货都重算，旧响应凭序号作废不塞新页。
const heat = ref(null)
let heatSeq = 0
async function refreshHeat() {
  const url = props.page?.danmakuUrl ?? null
  const dur = duration.value
  const seq = ++heatSeq
  if (!url || !(dur > 0) || !Number.isFinite(dur)) {
    heat.value = null
    return
  }
  const text = await loadAssText(url)
  if (seq !== heatSeq) return
  heat.value = text ? buildHeat(text, dur) : null
}
watch([() => props.page?.danmakuUrl, duration], refreshHeat)
// 直方图画成填充曲线而非逐桶矩形：稀疏归档（几十发弹幕）数值只剩几档离散值，
// 硬边矩形会搭成楼梯。笔法照官方密度线经典套路：曲线走相邻点中点、真点当控制点，
// 所有转角都圆滑；两端沿基线入出，首尾弹幕也有起落坡；控制点 y 钉 [0,100] 不穿底。
const heatPath = computed(() => {
  const v = heat.value
  if (!v || !v.length) return ''
  const n = v.length
  const clampY = (y) => Math.max(0, Math.min(100, y))
  const r2 = (x) => Math.round(x * 100) / 100
  const pts = [[0, 100]]
  for (let i = 0; i < n; i++) {
    const hgt = v[i] > 0 ? Math.max(2, v[i] * 100) : 0
    pts.push([i + 0.5, clampY(100 - hgt)])
  }
  pts.push([n, 100])
  const len = pts.length
  let d = `M${r2(pts[0][0])} ${r2(pts[0][1])}`
  for (let i = 1; i < len - 2; i++) {
    const xc = (pts[i][0] + pts[i + 1][0]) / 2
    const yc = clampY((pts[i][1] + pts[i + 1][1]) / 2)
    d += ` Q${r2(pts[i][0])} ${r2(pts[i][1])} ${r2(xc)} ${r2(yc)}`
  }
  const prev = pts[len - 2]
  const last = pts[len - 1]
  d += ` Q${r2(prev[0])} ${r2(clampY(prev[1]))} ${r2(last[0])} ${r2(last[1])} Z`
  return d
})
// 官方同款「扫过即点亮」：同一条路径 clip 到已播段再叠一层白，播放头推到哪亮到哪。
// clip 宽用 viewBox 桶单位（1 桶 = 1 秒），和路径同一坐标系，不随轨道宽度飘。
// id 带随机后缀：一页一实例够用，多实例同屏也不串台。
const heatClipId = 'lbs-heat-' + Math.random().toString(36).slice(2, 8)
const heatPlayedUnits = computed(() => {
  const n = heat.value?.length || 0
  if (!n || !(duration.value > 0)) return 0
  return Math.min(1, Math.max(0, currentTime.value / duration.value)) * n
})
const showControls = ref(true)
const isFs = ref(false)
const rate = ref(1)
const rateMenu = ref(false)
const qualityMenu = ref(false)
const videoHeight = ref(0)

// 只展示不切换：本地馆藏单档，菜单里当前档高亮、其他置灰（B 站样式）。
// 两枚 60帧 档不是摆设：引擎实发规格（qualityName）或实测帧率判到 50+ 就点亮（见 qualityLabel）。
const QUALITIES = ['8K 超高清', '4K 超高清', '1080P 60帧', '1080P 高清', '720P 60帧', '720P 准高清', '480P 标清', '360P 流畅']
const qualityLabel = computed(() => {
  // 真值优先：引擎实发档位（DownloadTask.Metadata.Resolution，如「1080P 60帧」）到站那一刻盖章，
  // 与 B 站页面菜单同源；没盖章的老货退回实测链：宽高/帧率先吃 ffprobe 回填，再退回前端实测。
  const f = activeFile.value
  if (f?.qualityName) return f.qualityName
  const h = f?.height || videoHeight.value
  const fps = f?.fps ?? measuredFps.value
  if (h >= 4320) return '8K 超高清'
  if (h >= 2160) return '4K 超高清'
  if (h >= 1080) return fps >= 50 ? '1080P 60帧' : '1080P 高清'
  if (h >= 720) return fps >= 50 ? '720P 60帧' : '720P 准高清'
  if (h >= 480) return '480P 标清'
  if (h >= 360) return '360P 流畅'
  return '清晰度'
})
const gearPanel = ref(false)
const playPanel = ref(false)
const holdingSpeed = ref(false)

// 播放设定：分站自己的精简版（开播封面/自动播放/循环/连播/比例/长按倍速/关灯）。v3 键：自动播放经站长定夺默认关，
// 旧 v2 配置除自动播放外原样搬迁（它试点期默认过开，留着会冒充站长自己的选择）。
// preCover（开播封面，站长 11-02 点名加）不进新键：旧 v3 配置里没这个字段的就吃下面的默认值 true（与开关上线前的行为一致）。
const legacyPlay = JSON.parse(localStorage.getItem('lbs.player.playPrefs.v2') || 'null') ?? {}
delete legacyPlay.autoplay
const play = reactive({ ...{ autoplay: false, loop: false, autoNext: true, fit: 'contain', holdSpeed: 1.5, lightsOff: false, preCover: true }, ...legacyPlay, ...(JSON.parse(localStorage.getItem('lbs.player.playPrefs.v3') || 'null') ?? {}) })
watch(play, (v) => localStorage.setItem('lbs.player.playPrefs.v3', JSON.stringify(v)), { deep: true })

// ─── 多编码版本（播放页点单加档）───
// 播放源 = 激活版本的文件或主版本；换源重建 video 元素，进度与播放态随 restorePlan 接力。
const activeCodec = ref(null)
let restorePlan = null

// 站长「删掉这一版」落定后这一行不消失，就地灰显成「已删除 · 站长可恢复」——文件只是进了回收站，
// 站长在站长室还整组捐得回来，所以这里不再像旧版那样本地摘行（摘了就等于前端替后端圆「真没了」那句谎）。
// 旗子按编码记，不按分 P 记：这枚钮走的是整版那口（DELETE /api/videos/{id}/codecs/{codec}，
// 一次调用 = 回收站里的一个组，跨所有分 P），同一 codec 在别的 P 的行也得跟着一起灰。
// 这一坨必须摆在 versions 之前：versions 的首评是 immediate 的（见下面 codecProg 那条「盐系少女事故」注释），
// 摆后面等于第一趟渲染就碰还没落地的 const——TDZ 自爆。
const recycledCodecs = ref(new Set()) // 已删除（文件躺在回收站里）的编码，key = codec
const deletingCodecs = ref(new Set()) // 正在删除的编码：钮按下去就锁上，防连点（删一发要等后端回话）；锁着期间行同样按已删除摆
const codecDelNote = ref(null) // { text, bad }：删完那句后端原话，成功失败都照抄不改写
const rawVersions = computed(() => props.page?.codecVersions ?? [])
// 台账行原样交给上层摆：已删除的行也留在列表里（灰显那一档），不再靠过滤藏行。
const versions = rawVersions
// 在库、且真拿来播得了的那一行：已删除（含正删着）的编码文件已经不在原位，一律不当播放源。
function arrivedRow(codec) {
  if (!codec || recycledCodecs.value.has(codec) || deletingCodecs.value.has(codec)) return null
  return versions.value.find((c) => c.codec === codec && c.status === 'Arrived') ?? null
}
const currentSrc = computed(() => {
  const hit = arrivedRow(activeCodec.value)
  return hit ? hit.relPath : props.page?.videoUrl
})

// 开场封面：这层画面从没播过之前把封面盖上去（B 站同款观感），一起播就淡出再不复返（中途暂停不翻回来）。
// 不用 <video poster>：那个属性挂不上 referrerpolicy，热链封面会被 B 站图床按 referer 挡（站内 40+ 处 img
// 都手写 no-referrer 就是这缘故）；自绘一层还顺带拿到淡出，pointer-events-none 又不挡「点画面即播」。
// 换分 P / 换清晰度会 :key 重建 video 元素，那时重新算「没播过」——第一眼仍是封面，不是黑屏。
// play.preCover（面板上的「开播封面」开关，站长 11-02 加）关掉时这层直接不亮，开播前看到的是 video 自己解码出来的首帧（没解出来就是黑底）。
const playedOnce = ref(false)
watch(currentSrc, () => { playedOnce.value = false })
watch(playing, (p) => { if (p) playedOnce.value = true })
const coverShown = computed(() => play.preCover && !!props.cover && !!props.page?.videoUrl && !playedOnce.value && !playing.value)

// 自动播放只认「还没被人为暂停过」：进页/换 P/换版本都算新开张（userPaused 清零），
// 暂停键按下去就别再自作主张续播（缓冲回调会反复报 canplay，不设这道闸会暂停后偷跑）。
let userPaused = false
watch(currentSrc, () => { userPaused = false })

// 激活文件 = 当前播放源对应的台账行（加档行或主版本页）：清晰度/徽记判据全认它，
// 切版本不许拿上一档的形态冒充（qualityName/fps/audioCodec 各归各行）。
const activeFile = computed(() => arrivedRow(activeCodec.value) || props.page)

// 前端实测帧率兜底：后端既没实发档也没实测形态（没探过的老货）才轮得到，
// 开播那一刻用 requestVideoFrameCallback 数 15 帧；暂停时数不到（回调不动），等下次开播再试，
// 一个源只试一发不反复扰盘。统计面板那发实测（openStats）另走自己的路，这里只喂清晰度判据。
const measuredFps = ref(null)
let fpsTriedSrc = null
watch(playing, (on) => {
  if (!on) return
  const src = video.value?.currentSrc
  if (!src || fpsTriedSrc === src) return
  const f = activeFile.value
  if (f?.qualityName || f?.fps != null) {
    fpsTriedSrc = src // 后端备齐了判据，不必实测
    return
  }
  fpsTriedSrc = src
  measureFps((v) => { if (v) measuredFps.value = v })
})

// Hi-Res 徽记判据（从硬到软三层，碰到哪层算哪层）：ffprobe 实测音频编码 flac →
// 引擎实发音频档 id（30251 杜比系那条线 / 31251 Hi-Res 无损，官方 PlaybackQualityCatalog 同源）→
// 前端容器 fourcc 实测兜底（fLaC）。引擎设置里勾了 Hi-Res 不算数，盘上真躺着 flac 才算数；
// 后端判据齐了就不发容器探测，一个源只探一发。官方控件同款：npd-ctrl-flac 只在 flac 流上桌时亮。
const audioFourccProbe = ref(null)
let fourccTriedSrc = null
watch(currentSrc, () => {
  audioFourccProbe.value = null
  fourccTriedSrc = null
})
watch(playing, (on) => {
  if (!on) return
  const src = video.value?.currentSrc
  if (!src || fourccTriedSrc === src) return
  const f = activeFile.value
  if (f?.audioCodec || f?.audioQualityId != null) {
    fourccTriedSrc = src // 后端备齐判据，不为徽记多发 Range
    return
  }
  fourccTriedSrc = src
  probeMp4(src).then((m) => {
    if (video.value?.currentSrc === src) audioFourccProbe.value = m?.audioFourcc ?? null
  })
})
const hiResActive = computed(() => {
  const f = activeFile.value
  if (f?.audioCodec) return f.audioCodec === 'flac'
  if (f?.audioQualityId != null) return f.audioQualityId === 30251 || f.audioQualityId === 31251
  return audioFourccProbe.value === 'fLaC'
})

// 版本展示规则（B 站风）：一个编码一行，主版本编码与加档同列；在库的直接摆出，
// 播放哪个亮哪个；从未到过的编码收进「点单加档其他版本」折叠行，不给单版本视频摆一排灰钮。
const CODEC_ROWS = [
  ['avc', 'AVC'],
  ['hevc', 'HEVC'],
  ['av1', 'AV1'],
]
const showMore = ref(false)

function versionInfo(codec) {
  const row = versions.value.find((c) => c.codec === codec) ?? null
  const isMain = props.page?.codec === codec
  // 「已删除」抢在「在库」前面判：删成那一刻父页那一发重拉还没落地，服务端数据里这一行仍是 Arrived，
  // 不抢先就会在灰下去之前又显一回大小与码率。主版本那一档例外——主件整版删也删不动（后端那道闸），照旧能播。
  if (!isMain && (recycledCodecs.value.has(codec) || deletingCodecs.value.has(codec))) {
    return { state: 'recycled', pending: deletingCodecs.value.has(codec) }
  }
  if (isMain || row?.status === 'Arrived') {
    // 主版本的大小由后端扫盘带出（mainBytes）；加档行Arrived 后自带 bytes。拿不到就显空，不编数。
    const extra = row?.status === 'Arrived'
    const bytes = extra ? row.bytes : isMain ? props.page?.mainBytes ?? null : null
    // 分轨码率只读后端落库的实测值（ffprobe 回填）：主版本在 page 上，加档行自带。前端不为此再发 Range。
    const src = extra ? row : props.page
    return {
      state: 'stock',
      fromMain: isMain,
      bytes: bytes ?? null,
      videoKbps: src?.videoKbps ?? null,
      audioKbps: src?.audioKbps ?? null,
    }
  }
  if (row?.status === 'Downloading') return { state: 'downloading' }
  if (row?.status === 'Failed') return { state: 'failed' }
  // 刚点完加档、服务端还没报回这一行：本地乐观态先顶上，免得点了像没点
  if (pendingCodecs.value.has(codec)) return { state: 'downloading' }
  return { state: 'missing' }
}

// 乐观态：从「点点单加档」到服务端报回这一行之间总有几秒到几分钟的空窗，先本地记着「进站中」，
// 这一行立刻从折叠区搬进主列表。服务端一报行就交权（下面 watch 摘除），此后一切以服务端状态为准（回 Failed 就显「失败」）。
const pendingCodecs = ref(new Set())

watch(
  versions,
  (rows) => {
    for (const codec of [...pendingCodecs.value]) {
      if (rows.some((r) => r.codec === codec)) pendingCodecs.value.delete(codec)
    }
  },
  { immediate: true },
)

// 加档请求被驳回（409 已在站/已在途、后端没应答等）：把刚许出去的「进站中」收回来，别让用户对着假状态干等
watch(
  () => props.pendingRevoke,
  (v) => {
    if (v?.codec) pendingCodecs.value.delete(v.codec)
  },
)

// 服务端又报回这一行的活档（站长从回收站整组恢复回来了、或别处重新点单把这版请回来了）：本地那面「已删除」
// 旗子交回去，行恢复成本来面目。账以服务端为准，前端这面旗子只负责撑到重拉落地那一下，不自建一套版本台账。
// 删成那次重拉带回来的是「这一行没了」，不清旗——旗不清，行才灰得住（父页 loadWatch 重拉是整包换掉 pages 的）。
watch(rawVersions, (rows) => {
  for (const r of rows) {
    if (r?.status === 'Arrived' || r?.status === 'Downloading') recycledCodecs.value.delete(r.codec)
  }
})

// ─── 站长把某一路加档版本删除（只清加档行，主版本碰不得）───
// 走整版那口：DELETE /api/videos/{id}/codecs/{codec}（不带分 P、不带请求体），一次调用 = 回收站里的一个组，
// 跨所有分 P。逐分 P 那口（/codecs/{pageIdx}/{codec}）仍在，但会把同一版劈成 N 个互不相干的组，恢复拼不回当初那一次点单。
// 真门在端点里（站长与这稿的投稿人删得动，其余 403 not-your-submission），
// 这里藏钮只是不把「按了也是白按」的钮摆给成员看（与站内弹幕 canDelete 同一范式）。
// 身份读全站那一份 memberStore（TopBar / 个人空间 / 分区都读它）：page 里根本没有 isStationOwner 那格——
// 它在 /api/watch 的顶层载荷上（分 P 行只带 codecVersions / mainBytes 那些），播放器不该自己造一个。
const isStationOwner = computed(() => member.value?.role === 'owner')

// 只给「已到货的加档行」摆这枚钮：主版本的文件记在 pages 表那一格，这口删的是 video_codecs 行，
// 把钮摆到主版本行上就是骗人以为主版本也能删。
function canDeleteRow(codec) {
  // 钥匙用服务端算好的那一份（投稿人本人 ∥ 站长，与观看页那两枚自管钮同一只眼）：
  // 后端 11-08 把这口摘给了投稿人自管，前端再只认 owner 就是「后端放开了、钮只摆给站长」那种半套放开。
  // member 那张脸只当兜底（服务端没算出 canManage 时，站长仍该按得动）。
  if (!props.canManage && !isStationOwner.value) return false
  const info = versionInfo(codec)
  // 已删除那一档（含正删着的）不再摆这枚钮：行还留在列表里灰着，钮再摆出来就是请人按第二遍。
  return info.state === 'stock' && !info.fromMain
}

async function deleteCodecRow(codec) {
  if (!props.videoId || deletingCodecs.value.has(codec)) return
  const sure = await requestConfirm({
    title: '删除这一档',
    sub: `${props.bvid} · ${codec}（整版 · 所有分 P）`,
    message: '删除后，这一档的媒体文件与台账记录一并移入回收站，主版本文件不受影响。',
    confirmText: '删除',
    cancelText: '再想想',
    // 不进 danger 那一档：danger 是留给「彻底销毁」那种断后路的活的（见 confirmStore）。这一下有后路，别用血色吓人。
    danger: false,
    icon: 'trash',
  })
  if (!sure) return
  // 正播着这一路就先切回主版本再动手：文件被播放器攥着，Windows 上根本删不掉
  // （后端那个 catch (IOException) 只是兜底，账销了文件却留在盘上，看着就脏）。
  // setCodec(null) 自带播放点接力，不会把人扔回片头重看一遍。
  if (activeCodec.value === codec) setCodec(null)
  const target = String(codec).trim().toLowerCase() // 后端那口按小写编码认（SupportedCodecs 全是小写），键也统一用它
  deletingCodecs.value.add(target)
  recycledCodecs.value.add(target) // 乐观灰显：确认了就当下灰下去，没删成再原样变回来（行不摘，只是灰）
  codecDelNote.value = null
  try {
    // 无请求体：整版删没有参数可递，编码就在路径里。
    const res = await fetch(`/api/videos/${props.videoId}/codecs/${encodeURIComponent(target)}`, { method: 'DELETE' })
    const body = await res.json().catch(() => null)
    // 后端给的人话原样贴出来（站内铁律：前端不改写），它没说才轮到兜底那句。
    // 404「没有这个版本」/409「占用、指的是主件、还在下载中」/403「不是你的稿」都走这一句。
    if (!res.ok) {
      recycledCodecs.value.delete(target) // 行还在服务端账上，不能让它凭空灰掉
      codecDelNote.value = { text: body?.message ?? '这一版没删成，稍后再试', bad: true }
      return
    }
    recycledCodecs.value.add(target) // 父页那一发重拉可能正赶在路上（带回来的还是删前的行），落定这一刻把旗子按回去
    codecDelNote.value = { text: body?.message ?? '已删除，文件与台账记录已移入回收站', bad: false }
    emit('codec-arrived') // 请父页重拉 watch，交权给服务端那本账（照引擎到货那条 emit 同一姿势，不另发明一套）
  } catch {
    recycledCodecs.value.delete(target)
    codecDelNote.value = { text: '分站后端没有应答，这一版没删成', bad: true }
  } finally {
    deletingCodecs.value.delete(target)
  }
}

// 进站中的引擎实况：有 Downloading 行时每 5 秒拉一发轻量进度端点（与站长室队列同一本账），
// 「进站中」后缀上百分比/已下/速度；某编码从进度表消失而版本行还报 Downloading = 十有八九定档了，
// 请父页重拉 watch（@codec-arrived），行自己会换成 Arrived 并带出大小与码率。
// 块子必须摆在 pendingCodecs/versionInfo 之后：immediate 首评就会碰这两家，摆前面是自爆 TDZ（盐系少女事故）。
const codecProg = ref({})
let progTimer = null
async function pullCodecProg() {
  if (!props.videoId) return
  try {
    const res = await fetch(`/api/videos/${props.videoId}/codecs/progress`)
    if (!res.ok) return
    const rows = await res.json()
    const map = {}
    for (const r of rows) if (!map[r.codec]) map[r.codec] = r
    const vanished = Object.keys(codecProg.value).filter((c) => !map[c])
    codecProg.value = map
    if (vanished.some((c) => versionInfo(c).state === 'downloading')) emit('codec-arrived')
  } catch {
    // 拉不到进度维持原样，不拿猜测冒充实况
  }
}
const hasDownloading = computed(() => CODEC_ROWS.some(([c]) => versionInfo(c).state === 'downloading'))
watch(hasDownloading, (on) => {
  if (on && !progTimer) {
    progTimer = setInterval(pullCodecProg, 5000)
    pullCodecProg()
  } else if (!on && progTimer) {
    clearInterval(progTimer)
    progTimer = null
    codecProg.value = {}
  }
}, { immediate: true })

function progTail(codec) {
  const p = codecProg.value[codec]
  if (!p) return ''
  const bits = []
  if (p.percent > 0) bits.push(`${Math.round(p.percent)}%`)
  if (p.got) bits.push(p.got)
  if (p.speed && p.speed !== '—' && p.speed !== '0 B/s') bits.push(p.speed)
  return bits.length ? ` · ${bits.join(' · ')}` : ''
}

// 在库行右侧标签：分轨码率（有数才显）+ 文件大小（采得到才显）；未下载的不占位。
// 「播放中」不占字位——当前条蓝底 + 版本名旁的 ▶ 小灯已把话说完。
function stockBadge(codec) {
  const info = versionInfo(codec)
  const bits = []
  const kbps = trackKbpsText(info)
  if (kbps) bits.push(kbps)
  if (info.bytes != null) bits.push(fmtVersionSize(info.bytes))
  return bits.join(' · ')
}

// 分轨码率只留数字不带 kbps 字样（站长要求；单位在统计面板里看）：「视频 2,814 / 音频 184」，
// 音频为空省成「视频 1,195」，两个都 null 整段不占位。
// 数据只来自后端落库值：downloading / failed 行文件还没落盘，不探、也不拿主版本的数糊，直接空着。
function trackKbpsText(info) {
  if (info.state !== 'stock') return ''
  const v = info.videoKbps
  const a = info.audioKbps
  if (v == null && a == null) return ''
  if (v != null && a != null) return `视频 ${v.toLocaleString()} / 音频 ${a.toLocaleString()}`
  if (v != null) return `视频 ${v.toLocaleString()}`
  return `音频 ${a.toLocaleString()}`
}

const presentRows = computed(() => CODEC_ROWS.filter(([c]) => versionInfo(c).state !== 'missing'))
const missingRows = computed(() => CODEC_ROWS.filter(([c]) => versionInfo(c).state === 'missing'))

// ─── 加档前的「预估」规格（后端代问 B 站 playurl 的标称值）───
// 只给没到货的那几行用：到货的行吃 ffprobe 实测，两者绝不混显。拿不到就整段不显 —— 宁空不假。
// 按需才发：人得先点开「▸ 点单加档其他版本」才问（不展开就不替分站打 playurl），而且三档全在库时不发。
const estimates = ref({})
const estimatesPage = ref(null) // 正为哪个分 P 发着这一发（只防同一次展开重发，不当「问过就不问」的账）

async function loadEstimates() {
  if (!props.videoId || !missingRows.value.length) return
  const page = props.page?.index ?? 1
  if (estimatesPage.value === page) return
  estimatesPage.value = page
  try {
    const res = await fetch(`/api/videos/${props.videoId}/codecs/estimates?page=${page}`)
    if (!res.ok) {
      estimatesPage.value = null // 没问成，下次再试；也不拿空的顶掉已有的行情
      return
    }
    const list = (await res.json()).items ?? []
    if (list.length) {
      const map = {}
      for (const it of list) map[it.codec] = it
      estimates.value = map
    } else {
      // 拉不到就空着：预估而已，没义务必须拉到。不记「问过」，下次点开折叠区再拉一次就好。
      estimatesPage.value = null
    }
  } catch {
    estimatesPage.value = null
  }
}

// 展开折叠区（真要挑档位的那一刻）或版本清单变动后按需问一次；没展开就一发出网都不该有。
watch([showMore, () => props.page?.index, versions], () => {
  if (showMore.value) loadEstimates()
}, { immediate: true })

// 数字格式拄上面那行实测的一模一样（千分位、「视频 X / 音频 Y · 48.3 MB」），只在前面顶个「预估」二字。
// 不拿 trackKbpsText 是因为它开头就卡 state === 'stock'，而预估行恰恰不是 stock。
function estBadge(codec) {
  const it = estimates.value[codec]
  if (!it) return ''
  const bits = []
  const v = it.videoKbps ?? null
  const a = it.audioKbps ?? null
  if (v != null && a != null) bits.push(`视频 ${v.toLocaleString()} / 音频 ${a.toLocaleString()}`)
  else if (v != null) bits.push(`视频 ${v.toLocaleString()}`)
  else if (a != null) bits.push(`音频 ${a.toLocaleString()}`)
  if (it.bytes != null) bits.push(fmtVersionSize(it.bytes))
  return bits.length ? `预估 ${bits.join(' · ')}` : ''
}

function estTitle(codec) {
  const it = estimates.value[codec]
  if (!it) return ''
  const bits = [
    '这是 B 站给的标称值，不是本地实测：B 站只给平均带宽，体积按（视频 + 音频）× 时长 ÷ 8 算的',
    '三档编码共用同一条音轨，所以音频那一栏三行会是同一个数',
    '到货后以 ffprobe 实测为准',
  ]
  if (it.qn != null) bits.unshift(`按 qn=${it.qn}${it.height ? `（${it.height}p）` : ''} 这一档估`)
  return bits.join('；')
}

function playingCodec() {
  return activeCodec.value ?? props.page?.codec ?? null
}

// 把「正在放的编码」报给观看页：标题下的下载按钮跟着换目标版本
watch([activeCodec, () => props.page?.codec], () => emit('codec-change', playingCodec()), { immediate: true })

function clickVersion(codec) {
  const info = versionInfo(codec)
  // 已删除的行既不能选来播（文件已进回收站），也不能当「没到货」再点一次单（这一版的账在回收站那个组里，
  // 想让回来是站长那一发恢复，不是在这里再点一发）。
  if (info.state === 'recycled') return
  if (info.state === 'stock') {
    // 点行 = 要用这个编码的文件播：主版本编码回主文件，纯加档切版本文件（setCodec 自带 no-op 防护）
    setCodec(info.fromMain ? null : codec)
    return
  }
  if (info.state === 'failed' || info.state === 'missing') {
    pendingCodecs.value.add(codec)
    emit('request-codec', codec)
  }
}

// 版本账按分 P 记，切 P 回主版本。续播指针也要归零：旧 P 没落的 seek 不许飞进新 P，
// 新 P 的点位由观看页重查后拿 resumeSec 重喂（它的 watch 自己会接）。
watch(() => props.page?.index, () => {
  activeCodec.value = null
  restorePlan = null
  resumePending = 0
  codecDelNote.value = null // 上一 P 那句「已删除」/失败话不跟到换了的新 P 脸上
})

watch(currentSrc, () => {
  const old = video.value
  const pageIdx = props.page?.index ?? 0
  // 切 P 前给旧 P 补一发：心跳只在正常播放时报，暂停/拖拽里切走最多丢 15 秒进度。
  // 拖拽中的 currentTime 是半个手指位置，报出去就是错值，直接跳过。
  if (old && pageIdx !== loadedPageIdx && !dragging && Number(old.currentTime) > 0) {
    reportProgress(loadedPageIdx)
  }
  // 换源只重建 <video>，旧元素不会被自动 pause：不先按停它还在后台解码/报事件（声音叠音、心跳继续报进度）。
  old?.pause()
  const plan = restorePlan
  restorePlan = null
  if (!plan) return
  nextTick(() => {
    const v = video.value
    if (!v) return
    v.addEventListener(
      'loadedmetadata',
      () => {
        v.currentTime = plan.t
        if (plan.play) v.play().catch(() => {})
      },
      { once: true },
    )
  })
})

function setCodec(codec) {
  if (codec !== null) {
    const hit = arrivedRow(codec) // 已删除的行不当播放源（arrivedRow 里那道闸），这儿再兜一次防止旁路进来
    if (!hit || codec === activeCodec.value) return
  } else if (!activeCodec.value) {
    return
  }
  const v = video.value
  restorePlan = v && v.readyState >= 1 ? { t: v.currentTime, play: !v.paused } : null
  activeCodec.value = codec
}

function fmtVersionSize(n) {
  if (n == null) return '在库'
  const mb = n / (1024 * 1024)
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${mb.toFixed(1)} MB`
}

// ─── 字幕（多轨：全部挂上，显示哪一路由这里说了算）───
// 字幕只有路径这一本账：artifacts 表 kind='subtitle' 存的是 rel_path，语言、人工/AI 全没落库，
// 所以轨名只能从文件名猜，库里没写的事前端一个字都不许编。
const subPanel = ref(false)
const subOn = ref(true) // 默认开：与这次改造之前一致（单轨时挂着 default 就是显示着的）
const subIdx = ref(0)
const subtitleUrls = computed(() => props.page?.subtitleUrls ?? [])

// 轨名认得出来就用它，认不出来给「字幕 1 / 字幕 2」这种笨名字。
// 引擎写盘的形状是 `{base}_{LanDoc}.srt`，同一种语言撞车时再追一枚 `_{trackId}` 尾
// （见 vendor 里 DownloadOutputLayout.GetSubtitleTrackPath 与 DownloadArtifactWriter 的 fileLabel）。
// 铁律：这一路无论拿到什么脏名字都不许抛错——一个标签而已，宁可笨到底，也不把整台播放器带走。
function subLabel(url, i) {
  const dumb = `字幕 ${i + 1}`
  try {
    if (typeof url !== 'string' || !url) return dumb
    let name = url.split('?')[0].split('#')[0]
    name = name.slice(name.lastIndexOf('/') + 1)
    try {
      name = decodeURIComponent(name) // 名字里有光秃秃的 % 就当它没编码，交给下面正则一起判
    } catch {
      /* 保持原样 */
    }
    name = name.replace(/\.(srt|vtt|json)$/i, '') // 台账里 .srt/.vtt/.json 都算 subtitle（StationDb 那张 switch）
    const segs = name.split('_')
    if (segs.length < 2) return dumb // 光秃秃一个基名：尾段就是片名本身，跟语言无关
    const tail = segs[segs.length - 1].trim()
    // 尾段得像个语言名：至少有一个字（字母或汉字都算），不是纯数字（那是重名轨的 trackId 尾），
    // 也不是 P3 这种分 P 尾缀（成片基名自己带的），也别长成一整句片名。
    if (!tail || !/\p{L}/u.test(tail) || /^\d+$/.test(tail) || /^p\d+$/i.test(tail) || tail.length > 24) return dumb
    return tail
  } catch {
    return dumb
  }
}

// 把「开没开 + 选哪一路」施加到元素上。浏览器只在 track 到货那阵自己认 default 一次（而且多条同时 default
// 只认第一条，等于白给），之后一切以这里为准。textTracks[i] 可能还没长出来（track 是异步挂的），
// 拿不到就这一趟跳过，等下一发现成时机——绝不在这里 while 干等，那会把主线程焊死。
function applySubtitleModes() {
  const tracks = video.value?.textTracks
  if (!tracks) return
  for (let i = 0; i < tracks.length; i++) {
    const tt = tracks[i]
    if (!tt) continue
    tt.mode = subOn.value && i === subIdx.value ? 'showing' : 'disabled'
  }
}

function pickSubtitle(i) {
  subIdx.value = i
  subOn.value = true // 点轨就是想看它：顺手把总开关带上，不用先开总开关再点轨走两步
  applySubtitleModes()
}

// <video> 带 :key="currentSrc"：切 P / 换编码都整枚重建，<track> 跟着重挂，mode 会被浏览器忘光。
// 这里挂在已有的重挂点（换源、字幕数组变动）上重新施加，不另开定时器；flush post 是为了等 DOM 把 track 插进去。
// ⚠ subtitleSrcs 的声明必须排在这支 watch 之前（交接单坑 4：script setup 里 watch 数组当场取值，
//   const 排在后面就是 TDZ ReferenceError，setup 一炸整个播放器都没了——11-11 实况踩过一次）。
const subtitleSrcs = ref([])
let subSrcSeq = 0
const subBlobUrls = new Set() // 只销本播放器自己造的 blob；退回原地址的那些不在账上，碰不得

watch([currentSrc, subtitleUrls, subtitleSrcs], () => applySubtitleModes(), { flush: 'post' })
// 每个分 P 的轨数可以不一样：上一 P 选到第 3 轨，这一 P 只有一轨时得把指针拉回 0，
// 否则 mode 谁都轮不上，看着像字幕坏了。
watch(subtitleUrls, (urls) => {
  if (subIdx.value > urls.length - 1) subIdx.value = 0
})

// ─── 字幕到货加工：浏览器 <track> 只认 WebVTT ───
// 台账里 129 条字幕全是 .srt（无 WEBVTT 头、时间戳用逗号），原样挂上会被 VTT 解析器整文件判死：
// 轨上 0 条 cue，面板写着「显示中」画面上却没一个字——站长 11-11 实机抓到的「字幕不显示」就是这条。
// 到货路上 fetch 下来就地转 VTT 挂 blob 地址；盘上原始文件一个字节不动（导出包里躺着的仍是原字幕）。
// 认不出或断线就退回原地址：最坏也就是眼下的现状（空轨），不会更坏。
// 代次护城（观看页 watchEpoch 同族）：切 P 时旧批迟到不许写新页的轨 src；自造的 blob 随批回收不留孤儿。
// （subtitleSrcs / subSrcSeq / subBlobUrls 三枚家底已前移到重挂 watch 之前，TDZ 缘由见那边注释）
async function refreshSubtitleSources() {
  const tok = ++subSrcSeq
  const made = []
  const srcs = []
  for (const u of subtitleUrls.value) {
    let out = u
    try {
      const res = await fetch(u)
      if (res.ok) {
        const vtt = subtitleTextToVtt(u, await res.text())
        if (vtt) {
          out = URL.createObjectURL(new Blob([vtt], { type: 'text/vtt' }))
          made.push(out)
        }
      }
    } catch {
      /* 断线就用原地址碰运气：同一份字节，坏不了也更不了 */
    }
    srcs.push(out)
  }
  if (tok !== subSrcSeq) {
    for (const b of made) URL.revokeObjectURL(b) // 迟到整批：自造的当场回收，不碰屏上更新的那一份
    return
  }
  for (const b of subtitleSrcs.value) {
    if (subBlobUrls.delete(b)) URL.revokeObjectURL(b)
  }
  for (const b of made) subBlobUrls.add(b)
  subtitleSrcs.value = srcs
}

watch(subtitleUrls, refreshSubtitleSources, { immediate: true })

// ─── 站内弹幕（只在本页飘，不上传 B 站）───
const danmakuLayer = ref(null)
const danmakuDraft = ref('')

function sendDanmaku() {
  const text = danmakuDraft.value.trim()
  if (!text) return
  // 交给观看页入库；后端回 id 后 localItems 刷新，弹幕从发送时点起飞
  emit('send-danmaku', { text, pageIdx: props.page?.index ?? 0, t: video.value?.currentTime ?? 0 })
  danmakuDraft.value = ''
}

function onEnded() {
  playing.value = false
  reportProgress() // 末尾那一发会把这条历史销掉（位置过 95% 服务端直接删行）
  if (play.autoNext && props.hasNextPage) emit('next')
  else if (props.autoRelated) emit('next-video')
}

// ─── 观看历史：位置随手报（进度全从 video 元素现取，不养影子状态）───
// 心跳 15 秒一发（暂停与拖拽中跳发）；pause / ended 当场补一发；关页面前交给 sendBeacon——
// 那时候 fetch 会被浏览器掉手，sendBeacon 才是它答应替你送完的那一程。
const PROGRESS_BEAT_MS = 15000
let progressBeat = 0

// 元素里实际装的是哪一 P：切 P 时 props.page 已是新的，补报旧 P 要靠它
let loadedPageIdx = props.page?.index ?? 0

function progressPayload(pageIdxOverride) {
  const v = video.value
  if (!v || !props.videoId) return null
  const durationSec = Number(v.duration)
  const positionSec = Number(v.currentTime)
  if (!Number.isFinite(durationSec) || durationSec <= 0) return null
  if (!Number.isFinite(positionSec) || positionSec < 0) return null
  return { pageIdx: pageIdxOverride ?? props.page?.index ?? 0, positionSec, durationSec }
}

function reportProgress(pageIdxOverride) {
  const body = progressPayload(pageIdxOverride)
  if (!body) return
  fetch(`/api/videos/${props.videoId}/progress`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }).catch(() => {
    /* 报不上就算了：下一轮心跳自然补上，不打扰看片 */
  })
}

function reportProgressByBeacon() {
  const body = progressPayload()
  if (!body || typeof navigator.sendBeacon !== 'function') return
  navigator.sendBeacon(
    `/api/videos/${props.videoId}/progress`,
    new Blob([JSON.stringify(body)], { type: 'application/json' }),
  )
}

function onProgressBeat() {
  const v = video.value
  if (!v || v.paused || dragging) return // 暂停归 pause 报、拖拽归松手报，心跳只管正常播放那一段
  reportProgress()
}

// 关灯：用 clipPath 在整屏黑幕上挖掉播放器区域，只关页面不关画面。
const lightsStyle = ref(null)
function refreshLights() {
  const r = root.value?.getBoundingClientRect()
  if (!r) return
  lightsStyle.value = {
    clipPath: `polygon(evenodd, 0 0, 100vw 0, 100vw 100vh, 0 100vh, 0 0, ${r.left}px ${r.top}px, ${r.right}px ${r.top}px, ${r.right}px ${r.bottom}px, ${r.left}px ${r.bottom}px, ${r.left}px ${r.top}px)`,
  }
}
// B 站宽屏式高度驱动：先给顶栏/留白/弹幕条/与正文的间隔让出竖向空间，播放器高度吃满剩余视口，宽度按 16:9 反推居中
const SHELL_V_RESERVE = 170
// 站长嫌画面小了一圈，高度驱动的结果整体再放大 6%（2026-10-31；1.1 → 1.08 → 1.06 一路试下来的）。
// 代价说在明处：现在的高度本来就刚好吃满视口，乘上去会比视口高出一截（约 40~60px），
// 全屏看片得滚一点点才够着控制条。嫌滚得慌就把这个数改回 1.0，或者改削上面的 SHELL_V_RESERVE。
const SHELL_SCALE = 1.06
const shellEl = ref(null)
const shellWidth = ref(0)
function measureShell() {
  if (!shellEl.value || document.fullscreenElement) {
    shellWidth.value = 0
    emit('shell-width', 0)
    return
  }
  const availH = (window.innerHeight - SHELL_V_RESERVE) * SHELL_SCALE
  shellWidth.value = Math.max(360, Math.min(window.innerWidth - 32, Math.floor((availH * 16) / 9)))
  emit('shell-width', shellWidth.value)
}
const shellStyle = computed(() => (shellWidth.value ? { width: `${shellWidth.value}px` } : null))
const onViewportChange = () => {
  // 全屏时黑幕（body 级）不在全屏元素子树内，会凭空消失，自动开灯兜底
  if (document.fullscreenElement && play.lightsOff) play.lightsOff = false
  if (play.lightsOff) refreshLights()
  measureShell()
}
window.addEventListener('resize', onViewportChange)
window.addEventListener('fullscreenchange', onViewportChange)
window.addEventListener('mouseup', onHoldMouseUp) // 在播放器外松右键的兜底，处理器幂等
watch(
  () => play.lightsOff,
  async (on) => {
    // 锁滚动：否则黑幕挖洞坐标不随页面滚，底下露白。黑幕本体已 Teleport 到 body，
    // 不再被 root 的 isolate 堆叠上下文困住（此前顶栏 z-20 压在整棵播放器子树上，盖不黑）。
    document.body.style.overflow = on ? 'hidden' : ''
    if (on) {
      await nextTick()
      refreshLights()
    }
  },
)
onBeforeUnmount(() => {
  document.body.style.overflow = ''
})

// 右键长按倍速（B 站同款手势）。状态机：右键按下开始计时，contextmenu（几乎总在按下后 ≤30ms 内到）决方向：
// ① holdSpeed 关档位 → contextmenu 到即弹菜单（老行为）；
// ② 开档位 → contextmenu 到达但未满 300ms 就松手 = 弹菜单；按住满 300ms = 临时加速，全程不弹菜单；
// ③ 鼠标移出播放器外松手 → window 级兜底；mouseup 彻底丢失 → 20s watchdog 自动归位，不永久卡加速态。
let holdTimer = 0
let menuTimer = 0
let watchdogTimer = 0
let ctxPending = null
let menuShownThisGesture = false

function clearHoldTimers() {
  clearTimeout(holdTimer)
  clearTimeout(menuTimer)
  clearTimeout(watchdogTimer)
  holdTimer = 0
  menuTimer = 0
  watchdogTimer = 0
}

function stopHold() {
  if (holdingSpeed.value) {
    holdingSpeed.value = false
    if (video.value) video.value.playbackRate = rate.value
  }
}

function endHoldGesture() {
  clearHoldTimers()
  ctxPending = null
  stopHold()
}

function beginHoldGesture(clientX, clientY) {
  endHoldGesture()
  menuShownThisGesture = false
  if (play.holdSpeed <= 0) return // 关档位：contextmenu 时机直接弹菜单，老行为
  ctxPending = clientX == null ? null : { x: clientX, y: clientY }
  holdTimer = setTimeout(() => {
    holdTimer = 0
    stopHold() // 上一次残留先归位
    holdingSpeed.value = true
    ctx.visible = false
    if (video.value) video.value.playbackRate = play.holdSpeed // 暂停中也允许按住，watchdog 会归位
    watchdogTimer = setTimeout(endHoldGesture, 20000) // mouseup/keyup 丢失时的自愈底线
  }, 300)
}

function onRootMouseDown(e) {
  if (e.button !== 2) return
  beginHoldGesture(e.clientX, e.clientY)
}

function onHoldMouseUp(e) {
  if (e && e.button !== 2) return
  if (holdTimer) {
    // 300ms 内松手 = 右键点击：弹菜单（contextmenu 时机被按住判定拦下了，这里补弹）
    clearTimeout(holdTimer)
    holdTimer = 0
    if (ctxPending && !menuShownThisGesture) {
      openCtxAt(ctxPending.x, ctxPending.y)
      menuShownThisGesture = true
    }
    ctxPending = null
    return
  }
  endHoldGesture() // 加速中松手 → 恢复倍速
}
const statsOpen = ref(false)
const ctx = reactive({ visible: false, x: 0, y: 0 })
const hover = reactive({ show: false, x: 0, t: 0 })
const stats = ref({})
const speedText = ref('—')
const netText = ref('0 KB')
const sparkVideo = ref(null)
const sparkNet = ref(null)
const logs = []
let statsTimer = 0
const sparkVData = []
const sparkNData = []
const sizeCache = new Map() // currentSrc → 字节数（HEAD 探一次）
let speedTick = 0

function logEvent(msg) {
  logs.push(`[${new Date().toLocaleTimeString('zh-CN', { hour12: false })}] ${msg}`)
  if (logs.length > 400) logs.shift()
}

let hideTimer = 0

// 弹幕偏好：站长室下发当出厂默认，本地 localStorage 覆盖（B 站也这么记）。
function danmakuDefaults() {
  return {
    on: true,
    opacity: 100,
    area: 100,
    fontScale: 100, // 弹幕字号百分比 50~150
    speed: 1,
    speedSync: false, // 弹幕速度同步播放倍速
    bold: false,
    stroke: 'thick', // thick 重墨 | thin 描边 | shadow 45°投影
    family: 'hei', // hei 黑体 | song 宋体 | mono 等宽
    keywords: '',
    modes: { scroll: true, top: true, bottom: true, color: true },
  }
}
const danmaku = reactive({ ...danmakuDefaults(), ...(JSON.parse(localStorage.getItem('lbs.danmaku.prefs') || 'null') ?? {}) })
danmaku.modes = { ...danmakuDefaults().modes, ...(danmaku.modes ?? {}) }
watch(danmaku, (v) => localStorage.setItem('lbs.danmaku.prefs', JSON.stringify(v)), { deep: true })

function resetDanmakuPrefs() {
  Object.assign(danmaku, danmakuDefaults())
}

const keywordList = computed(() =>
  String(danmaku.keywords ?? '')
    .split(/[,，、;；\s]+/)
    .map((s) => s.trim())
    .filter(Boolean),
)

function filterOn(k) {
  if (k === 'scroll') return danmaku.modes.scroll !== false
  if (k === 'color') return danmaku.modes.color !== false
  return danmaku.modes.top !== false || danmaku.modes.bottom !== false
}

function toggleDmFilter(k) {
  if (k === 'fixed') {
    const on = !(danmaku.modes.top || danmaku.modes.bottom)
    danmaku.modes.top = on
    danmaku.modes.bottom = on
  } else {
    danmaku.modes[k] = !filterOn(k)
  }
}

const SPEED_TICKS = [
  [0.5, '较慢'],
  [0.75, '慢'],
  [1, '适中'],
  [1.5, '快'],
  [2, '较快'],
]
const speedTickIndex = computed(() => {
  let best = 2
  let bestGap = Infinity
  SPEED_TICKS.forEach(([v], i) => {
    const gap = Math.abs(v - (danmaku.speed || 1))
    if (gap < bestGap) {
      bestGap = gap
      best = i
    }
  })
  return best
})

const engineDefaults = ref({ fontSize: -1, lineCount: -1 })
const layerFontSize = computed(() => {
  const base = engineDefaults.value.fontSize > 0 ? engineDefaults.value.fontSize : 50
  return Math.round((base * (danmaku.fontScale || 100)) / 100)
})

async function loadEngineDefaults() {
  try {
    const res = await fetch('/api/settings/engine')
    if (res.ok) {
      const d = (await res.json()).danmaku
      if (d) engineDefaults.value = { fontSize: d.fontSize ?? -1, lineCount: d.lineCount ?? -1 }
    }
  } catch {
    /* 用站内默认 */
  }
}

// ---- 播放控制 ----
function togglePlay() {
  const v = video.value
  if (!v || !props.page?.videoUrl) return
  if (v.paused) {
    userPaused = false
    logEvent('播放')
    v.play().catch(() => {})
  } else {
    userPaused = true
    logEvent('暂停')
    v.pause()
  }
}

// canplay 时分两件事：收尾缓冲态；若开了自动播放且没人按下过暂停，就自己开跑。
// 浏览器不给有声放行时 play() 会被拒，中央大按钮接着，不白屏干等。
function onCanplay() {
  waiting.value = false
  stuckRecover.value = false
  if (play.autoplay && !userPaused && !playing.value) video.value?.play().catch(() => {})
}

function fmt(sec) {
  if (!Number.isFinite(sec) || sec < 0) sec = 0
  const h = Math.floor(sec / 3600)
  const m = Math.floor((sec % 3600) / 60)
  const s = Math.floor(sec % 60)
  const mm = String(m).padStart(2, '0')
  const ss = String(s).padStart(2, '0')
  return h > 0 ? `${h}:${mm}:${ss}` : `${mm}:${ss}`
}

function seekTo(sec) {
  const v = video.value
  if (!v || !Number.isFinite(v.duration)) return
  const target = Math.min(Math.max(0, sec), v.duration - 0.05)
  // 连按合并：120ms 内的多次请求只落一次 currentTime，消灭 seek 风暴（快速连跳的卡死温床）。
  seekTarget = target
  clearTimeout(seekMergeTimer)
  seekMergeTimer = setTimeout(flushSeek, 120)
}

function flushSeek() {
  clearTimeout(seekMergeTimer)
  seekMergeTimer = 0
  const v = video.value
  if (!v || seekTarget == null) return
  logEvent(`seek→${seekTarget.toFixed(1)}s`)
  v.currentTime = seekTarget
  seekTarget = null
}

// 相对跳转：连按时以「还没落地的目标点」为基数累加，八次短按就是 8 个 2 秒而不是一次 2 秒。
function seekBy(delta) {
  const v = video.value
  if (!v || !Number.isFinite(v.duration)) return
  const base = seekTarget != null ? seekTarget : v.currentTime
  seekTo(base + delta)
}

// 地址栏的 ?t= 只在初次挂载时取一次：切 P / 切编码都会 :key 重建 video 元素，
// 每次 loadedmetadata 都重读 search 会把人反复拽回同一个时间点。
const tParamRaw = new URLSearchParams(window.location.search).get('t')
let tParam = tParamRaw !== null && Number.isFinite(Number(tParamRaw)) ? Number(tParamRaw) : null

function onLoadedMetadata() {
  const v = video.value
  duration.value = v.duration || 0
  videoHeight.value = v.videoHeight || 0
  v.playbackRate = rate.value
  v.volume = volume.value
  v.muted = muted.value
  loadedPageIdx = props.page?.index ?? 0 // 这一页确实装进元素了，补报进度拿它当旧 P
  applySubtitleModes() // 重建过的 <video> 连 <track> 一起换新，字幕开没开在这里重新落一次（不靠定时器补）
  if (tParam != null) {
    const t = tParam
    tParam = null // 一次性消费，后面重建元素不再提
    seekTo(t)
  } else flushResume() // 地址栏的 ?t= 才是硬指令，没它才轮到续播点
}

// ─── 续播：观看页算好了该从哪一秒起（位置地板、95% 上限都在它那里判）───
// 元数据没就位时 seek 会被默默吃掉，所以这里存一发，等 loadedmetadata 再落（与编码切换的 restorePlan 同一套路）。
let resumePending = 0

watch(() => props.resumeSec, (v) => {
  resumePending = Number.isFinite(v) && v > 0 ? v : 0
  const el = video.value
  if (resumePending && el && el.readyState >= 1) flushResume()
}, { immediate: true })

// 从头开始：续播泡上的反悔钮——收到增量就跳 0 接着播；元数据没就位时 seekTo 自己会碎，
// 而那会儿本来就还没离开片头，不亏
watch(() => props.restartTick, (v) => {
  if (Number(v) > 0) {
    resumePending = 0 // 撤回还没落地的续播点，别让它在 seek 之后又把片子拽回去
    seekTo(0)
  }
})

function flushResume() {
  if (!resumePending) return
  const v = video.value
  if (!v || !Number.isFinite(v.duration) || v.duration <= 0) return
  const target = Math.min(resumePending, v.duration - 0.05)
  resumePending = 0
  logEvent(`续播 seek→${target.toFixed(1)}s`)
  seekTo(target)
}

function applyVolume(x) {
  volume.value = Math.min(1, Math.max(0, x))
  muted.value = volume.value === 0
  if (video.value) {
    video.value.volume = volume.value
    video.value.muted = muted.value
  }
  localStorage.setItem('lbs.player.volume', String(volume.value))
}

// ─── 音量竖条（B 站 .bpx-player-ctrl-volume-box 同款）：拖拽/点选都只跟 applyVolume 说话，静音联动全走老口 ───
// 可见性不吃 CSS group-hover（弹层与音量钮间有 8px 死区，穿越瞬间 CSS :hover 就断），改由三个状态合成：
//   volHovering —— 真 hover（mouseenter/leave 维护；弹层与桥都是容器子树，进出子树不算离开）；
//   volPinned  —— 拖拽锁（pointerdown 起锁，拖到哪都看得见）或鼠标已离场后的收起宽限；
//   可见 = volHovering || volPinned；离场后 200ms 才卸 pinned（重新进入则取消），手抖穿越间隙不闪没。
//   pointer capture 期间 hover 事件会被抑制，松手时用 容器∩弹层 包围盒实测坐标补判一次。
const volTrackEl = ref(null)
const volZoneEl = ref(null)
const volPopEl = ref(null)
const volHovering = ref(false)
const volPinned = ref(false)
const volDragging = ref(false)
const volVisible = computed(() => volHovering.value || volPinned.value)
const volumePercent = computed(() => Math.round((muted.value ? 0 : volume.value) * 100))
let volHideTimer = null

function volClearTimer() {
  if (volHideTimer) {
    clearTimeout(volHideTimer)
    volHideTimer = null
  }
}

function volArmHide(delay) {
  volClearTimer()
  volHideTimer = setTimeout(() => {
    volHideTimer = null
    if (!volDragging.value) volPinned.value = false // 拖拽中途绝不卸锁
  }, delay)
}

function volEnter() {
  volClearTimer()
  volHovering.value = true
  volPinned.value = false
}

function volLeave() {
  volHovering.value = false
  volArmHide(200)
}

function volInsidePoint(e) {
  // 松手时指针落在音量钮区或弹层（含桥）都算「人还在」，继续显示不收。
  for (const el of [volZoneEl.value, volPopEl.value]) {
    if (!el) continue
    const r = el.getBoundingClientRect()
    if (e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom) return true
  }
  return false
}

function volFromPointer(e) {
  const el = volTrackEl.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  const ratio = (rect.bottom - e.clientY) / rect.height // Y 自底向上映射 0-1
  applyVolume(Math.round(Math.min(1, Math.max(0, ratio)) * 100) / 100)
}

function volTrackDown(e) {
  volDragging.value = true
  volClearTimer()
  volPinned.value = true // 拖拽锁：无论 hover 与否都保持可见 + 可命中
  volTrackEl.value?.setPointerCapture(e.pointerId) // 滑出元素也能续拖
  volFromPointer(e) // 单点轨道任意位置直接跳到该音量
  e.preventDefault()
}

function volTrackMove(e) {
  if (volDragging.value) volFromPointer(e)
}

function volTrackUp(e) {
  volDragging.value = false
  try {
    volTrackEl.value?.releasePointerCapture(e.pointerId)
  } catch {
    /* 已自动释放，不管 */
  }
  volHovering.value = volInsidePoint(e)
  if (volHovering.value) {
    volClearTimer()
    volPinned.value = false // 人还在，hover 状态接管，锁当场卸
  } else {
    volArmHide(150) // 松手即离场：宽限期内锁继续撑着，timer 回调里才卸锁再收
  }
}

function setRate(x) {
  rate.value = x
  logEvent(`倍速 ${x}×`)
  holdingSpeed.value = false
  if (video.value) video.value.playbackRate = x
  rateMenu.value = false
}

function takeScreenshot() {
  const v = video.value
  if (!v || !v.videoWidth) return
  const c = document.createElement('canvas')
  c.width = v.videoWidth
  c.height = v.videoHeight
  c.getContext('2d').drawImage(v, 0, 0)
  const a = document.createElement('a')
  a.href = c.toDataURL('image/png')
  a.download = `${props.bvid}-P${props.page?.index ?? 1}-${fmt(currentTime.value).replace(/:/g, '')}.png`
  a.click()
  ctx.visible = false
  logEvent(`截图 ${fmt(currentTime.value)}`)
}

async function togglePip() {
  const v = video.value
  if (!v) return
  ctx.visible = false
  logEvent(document.pictureInPictureElement ? '关闭画中画' : '开启画中画')
  try {
    if (document.pictureInPictureElement) await document.exitPictureInPicture()
    else await v.requestPictureInPicture()
  } catch {
    /* 浏览器不让弹就算了 */
  }
}

function copyLink() {
  const url = `${window.location.origin}/watch/${props.bvid}?t=${Math.floor(currentTime.value)}`
  navigator.clipboard?.writeText(url).catch(() => {})
  ctx.visible = false
}

async function toggleFullscreen() {
  if (document.fullscreenElement) await document.exitFullscreen()
  else await root.value?.requestFullscreen().catch(() => {})
}

// 统计面板的编码只认容器实测。旧做法拿 canPlayType 猜：它问的是「浏览器放不放得了」，
// Chrome 对第一项 avc1 就回 probably，循环第一次就 return，无论实际封的什么都说 H.264 —— 那是骗人。
// 实测探不到 fourcc 才退回数据库标称，并且明标「(标称)」，让用户分得清哪个是量的、哪个是登记的。
const NOMINAL_CODEC = {
  avc: { label: 'H.264', fourcc: 'avc1' },
  hevc: { label: 'HEVC', fourcc: 'hvc1' },
  av1: { label: 'AV1', fourcc: 'av01' },
}

// 当前在放的是哪个编码：跟 currentSrc 用同一套判据（加档行 Arrived 才算真在播加档），别说一套、播另一套
function nominalCodec() {
  const hit = activeCodec.value && versions.value.find((c) => c.codec === activeCodec.value && c.status === 'Arrived')
  return NOMINAL_CODEC[hit ? activeCodec.value : props.page?.codec] ?? null
}

// 把实测 fourcc 拼成统计面板那行 mime；哪一路没探到就缺哪一路，两条都没有整行显「—」（绝不写死 mp4a.40.2）
function mimeText(videoFourcc, audioFourcc, suffix = '') {
  const parts = []
  if (videoFourcc) parts.push(`video/mp4;codecs="${videoFourcc}"`)
  if (audioFourcc) parts.push(`audio/mp4;codecs="${audioFourcc}"`)
  return parts.length ? `${parts.join(', ')}${suffix}` : '—'
}

function measureFps(done) {
  const v = video.value
  if (!v?.requestVideoFrameCallback) {
    done(null)
    return
  }
  let n = 0
  const t0 = performance.now()
  let settled = false
  const finish = () => {
    if (settled || n < 3) return
    settled = true
    done(Math.round((n / (performance.now() - t0)) * 1000 * 1000) / 1000)
  }
  const cb = () => {
    n++
    if (n < 15) v.requestVideoFrameCallback(cb)
    else finish()
  }
  v.requestVideoFrameCallback(cb)
  setTimeout(finish, 1700)
}

function fileBytes() {
  const v = video.value
  if (!v?.currentSrc) return 0
  for (const e of performance.getEntriesByType('resource')) {
    if (e.name === v.currentSrc) return e.encodedBodySize || e.transferSize || 0
  }
  return 0
}

// 视频大文件经常不进 performance 资源表，体积直接 HEAD 问服务器要 Content-Length。
async function probeTotalBytes() {
  const v = video.value
  const src = v?.currentSrc
  if (!src) return 0
  if (sizeCache.has(src)) return sizeCache.get(src)
  try {
    const r = await fetch(src, { method: 'HEAD', cache: 'no-store' })
    const len = Number(r.headers.get('content-length')) || 0
    if (len) {
      sizeCache.set(src, len)
      return len
    }
  } catch {
    /* 服务端不应 HEAD 就回到资源表 */
  }
  return fileBytes()
}

// 拉 4MB Range 块测瞬时吞吐，本地回环和远程部署都拿到的是真实网速。
async function sampleThroughput() {
  const v = video.value
  const src = v?.currentSrc
  if (!src) {
    speedText.value = '—'
    return
  }
  const ctrl = new AbortController()
  const t0 = performance.now()
  let got = 0
  const timer = setTimeout(() => ctrl.abort(), 1500)
  try {
    const res = await fetch(src, { headers: { Range: 'bytes=0-4194303' }, signal: ctrl.signal, cache: 'no-store' })
    const reader = res.body?.getReader()
    while (reader) {
      const { done, value } = await reader.read()
      if (done) break
      got += value?.byteLength ?? 0
    }
  } catch {
    /* 超时中断属于正常收尾 */
  }
  clearTimeout(timer)
  const secs = Math.max(0.05, (performance.now() - t0) / 1000)
  const kbps = Math.max(0, Math.round((got * 8) / secs / 1000))
  speedText.value = `${kbps.toLocaleString()} Kbps`
  sparkVData.push(kbps)
  if (sparkVData.length > 40) sparkVData.shift()
  drawSpark(sparkVideo.value, sparkVData)
}

function drawSpark(el, data) {
  if (!el) return
  const dpr = window.devicePixelRatio || 1
  el.width = Math.max(1, Math.floor(el.clientWidth * dpr))
  el.height = Math.max(1, Math.floor(el.clientHeight * dpr))
  const ctx2 = el.getContext('2d')
  ctx2.clearRect(0, 0, el.width, el.height)
  if (data.length < 2) return
  const max = Math.max(1, ...data)
  ctx2.strokeStyle = 'rgba(255,255,255,0.9)'
  ctx2.lineWidth = Math.max(1, dpr)
  ctx2.beginPath()
  data.forEach((val, i) => {
    const x = (i / (data.length - 1)) * el.width
    const y = el.height - (val / max) * (el.height - 2 * dpr) - dpr
    if (i === 0) ctx2.moveTo(x, y)
    else ctx2.lineTo(x, y)
  })
  ctx2.stroke()
}

function tickStats() {
  const v = video.value
  if (!v) return
  netText.value = fmtBytes(sparkNData.length ? sparkNData[sparkNData.length - 1] : 0)
  if (sparkNData.length > 40) sparkNData.shift()
  drawSpark(sparkNet.value, sparkNData)
  const q = v.getVideoPlaybackQuality?.() ?? {}
  stats.value['Dropped Frames'] = `${q.droppedVideoFrames ?? 0} / ${q.totalVideoFrames ?? 0}`
  stats.value['Segments'] = `${v.buffered.length || 0} / 1`
  sparkNData.push(sizeCache.get(v.currentSrc) || 0)
  // 每 3 秒重新测一次吞吐，曲线有起伏又不浪费带宽。
  if (++speedTick % 3 === 0) sampleThroughput()
}

function fmtBytes(bytes) {
  if (!bytes) return '0 KB'
  return bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(0)} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

let statsProbe = 0 // 面板探测的代次：关面板、换源后迟到的那份结果不许再往面板上写

async function openStats() {
  const v = video.value
  ctx.visible = false
  statsOpen.value = true
  logEvent('打开统计信息')
  if (!v) return
  const src = v.currentSrc
  const seq = ++statsProbe
  const host = (() => {
    try {
      return new URL(v.currentSrc).host
    } catch {
      return window.location.host
    }
  })()
  const size = await probeTotalBytes()
  const totalKbps = size && duration.value ? Math.round((size * 8) / duration.value / 1000) : null
  // 先把标称版摆上去，实测回来再盖：面板不该为了等一发 Range 探测而空着字段。
  const nominal = nominalCodec()
  const nominalTag = nominal ? `${nominal.label} (标称)` : '—'
  stats.value = {
    'Mime Type': mimeText(nominal?.fourcc ?? null, null, nominal ? ' (标称)' : ''),
    'Player Type': 'LocalPlayer',
    'File Size': size ? fmtBytes(size) : '—',
    Resolution: '—',
    'Video DataRate': totalKbps ? `${totalKbps.toLocaleString()} Kbps [${nominalTag}]` : '—',
    'Audio DataRate': '—',
    Segments: `${v.buffered.length || 0} / 1`,
    'Dropped Frames': '—',
    'Video Host': host,
    'Audio Host': host,
  }
  // 一次 moov 取样同时拿编码与分轨码率：每开一次面板只探一发，切版本后重开面板会重探（不拿旧缓存糊）。
  probeMp4(src, size).then((m) => {
    if (seq !== statsProbe || !statsOpen.value || video.value?.currentSrc !== src) return
    const videoLabel = m?.videoLabel ?? nominalTag
    if (m?.videoKbps) stats.value['Video DataRate'] = `${m.videoKbps.toLocaleString()} Kbps [${videoLabel}]`
    else if (m) stats.value['Video DataRate'] = totalKbps ? `${totalKbps.toLocaleString()} Kbps [${videoLabel}]` : '—'
    // 括号里跟视频一样用人话标签（AAC / E-AC-3…），裸 fourcc 只留在 Mime Type 那行给愿意看的人
    if (m?.audioKbps) stats.value['Audio DataRate'] = `${m.audioKbps.toLocaleString()} Kbps [${m.audioLabel ?? m.audioFourcc ?? '—'}]`
    if (m?.videoFourcc || m?.audioFourcc) {
      stats.value['Mime Type'] = mimeText(m.videoFourcc ?? null, m.audioFourcc ?? null)
    }
    logEvent(`容器实测 视频${m?.videoLabel ?? '?'} / 音频${m?.audioFourcc ?? '?'} 分轨码率 视频${m?.videoKbps ?? '?'} / 音频${m?.audioKbps ?? '?'} Kbps`)
  })
  if (v.videoWidth) {
    const apply = (fps) => {
      stats.value.Resolution = fps ? `${v.videoWidth} x ${v.videoHeight}@${fps.toFixed(3)}` : `${v.videoWidth} x ${v.videoHeight}`
    }
    if (v.paused || !v.requestVideoFrameCallback) apply(null)
    else measureFps(apply)
  }
  sparkVData.length = 0
  sparkNData.length = 0
  netText.value = fmtBytes(size)
  clearInterval(statsTimer)
  statsTimer = setInterval(tickStats, 1000)
  nextTick(() => {
    drawSpark(sparkVideo.value, [0, 0])
    drawSpark(sparkNet.value, [0, 0])
    sampleThroughput()
  })
}

function closeStats() {
  statsOpen.value = false
  statsProbe += 1 // 在飞的探测结果就此作废，别等下次打开时把上一份糊上来
  clearInterval(statsTimer)
}

function copyLog() {
  navigator.clipboard?.writeText(logs.join('\n')).catch(() => {})
}

function downloadLog() {
  const blob = new Blob([logs.join('\n')], { type: 'text/plain;charset=utf-8' })
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = `${props.bvid}-player.log`
  a.click()
  setTimeout(() => URL.revokeObjectURL(a.href), 5000)
}

// ---- 进度条拖拽 ----
let dragging = false
function trackSeek(e) {
  const el = trackEl.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  const ratio = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width))
  seekTo(ratio * duration.value)
}
function trackDown(e) {
  dragging = true
  trackEl.value?.setPointerCapture(e.pointerId)
  trackSeek(e)
}
function trackMove(e) {
  const el = trackEl.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  hover.x = Math.min(rect.width, Math.max(0, e.clientX - rect.left))
  hover.t = (hover.x / rect.width) * duration.value
  hover.show = true
  if (dragging) trackSeek(e)
}
function trackUp(e) {
  dragging = false
  trackEl.value?.releasePointerCapture?.(e.pointerId)
}

// ---- 控制条显隐 ----
function wakeControls() {
  showControls.value = true
  clearTimeout(hideTimer)
  hideTimer = setTimeout(() => {
    // 真缓冲（waiting）期间不落帘：素圈住在条里，条隐圈灭会让用户以为片子死了；缓冲一到货自恢复 5 秒落帘节奏。
    if (playing.value && !waiting.value && !rateMenu.value && !qualityMenu.value && !gearPanel.value && !playPanel.value && !statsOpen.value && !ctx.visible) showControls.value = false
  }, 5000) // 站长口径：与续播小泡同步驻留五秒，鼠标一碰重新计时
}

// ---- 右键菜单 ----
function openCtx(e) {
  if (menuShownThisGesture) return // 同一手势重复触发不重弹
  if (ctxPending !== null) return // 加速判定中：菜单交给松手路径；加速中则全程不弹
  openCtxAt(e.clientX, e.clientY)
  menuShownThisGesture = true
}

function openCtxAt(clientX, clientY) {
  const rect = root.value.getBoundingClientRect()
  ctx.visible = true
  ctx.x = Math.min(clientX - rect.left, rect.width - 170)
  ctx.y = Math.min(clientY - rect.top, rect.height - 190)
  clearTimeout(hideTimer)
  showControls.value = true
}
function closeCtx() {
  ctx.visible = false
  wakeControls()
}

// ---- 键盘快捷键 ----
function onKey(e) {
  const tag = (e.target?.tagName ?? '').toLowerCase()
  if (tag === 'input' || tag === 'textarea' || e.target?.isContentEditable) return
  if (e.altKey || e.ctrlKey || e.metaKey) return
  // 按住不放时系统会连发 keydown（e.repeat）：除音量键外一律忽略，
  // 避免长按方向键秒级连续跳片（B 站同款：一次按键只跳一次）。
  if (e.repeat && e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return
  const v = video.value
  if (!v) return
  let used = true
  switch (e.key) {
    case ' ':
    case 'k':
      togglePlay()
      break
    case 'ArrowRight':
      // 按下只开快进计时：300ms 内松手 = 短按跳 2s（keyup 里跳）；满 300ms = 直接快进不跳。
      arrowSeekPending = true
      if (play.holdSpeed > 0) beginHoldGesture(null, null)
      break
    case 'ArrowLeft':
      seekBy(-2)
      break
    case 'l':
      seekBy(10)
      break
    case 'j':
      seekBy(-10)
      break
    case 'ArrowUp':
      applyVolume(volume.value + 0.1)
      break
    case 'ArrowDown':
      applyVolume(volume.value - 0.1)
      break
    case 'm':
      muted.value = !muted.value
      v.muted = muted.value
      break
    case 'f':
      toggleFullscreen()
      break
    case 'd':
      danmaku.on = !danmaku.on
      break
    default:
      if (/^[0-9]$/.test(e.key) && duration.value) seekTo((Number(e.key) / 10) * duration.value)
      else used = false
  }
  if (used) {
    e.preventDefault()
    wakeControls()
  }
}

let arrowSeekPending = false

function onKeyUp(e) {
  if (e.key !== 'ArrowRight') return
  const wasHolding = holdingSpeed.value
  const shortPress = arrowSeekPending && !wasHolding
  arrowSeekPending = false
  endHoldGesture()
  if (shortPress) seekBy(2)
}

function onFsChange() {
  isFs.value = Boolean(document.fullscreenElement)
}

onMounted(() => {
  measureShell()
  loadEngineDefaults()
  window.addEventListener('keydown', onKey)
  window.addEventListener('keyup', onKeyUp)
  window.addEventListener('blur', endHoldGesture) // 切走窗口时按键释放收不到，直接归位
  document.addEventListener('fullscreenchange', onFsChange)
  document.addEventListener('click', closeCtx)
  progressBeat = setInterval(onProgressBeat, PROGRESS_BEAT_MS) // 心跳只管正常播放那一段（函数里自己筛）
  window.addEventListener('pagehide', reportProgressByBeacon) // 关页面/刷新：最后一发走信标
})
onBeforeUnmount(() => {
  clearTimeout(hideTimer)
  clearTimeout(stuckTimer)
  clearTimeout(seekMergeTimer)
  subSrcSeq += 1 // 在飞的字幕加工整发作废：迟到不写死实例，也不留挂空 blob
  for (const b of subtitleSrcs.value) {
    if (subBlobUrls.delete(b)) URL.revokeObjectURL(b)
  }
  subtitleSrcs.value = []
  volClearTimer() // 音量弹层 200ms 延迟收起那一发，卸载后不再回来拨已消失的实例
  endHoldGesture()
  window.removeEventListener('mouseup', onHoldMouseUp)
  window.removeEventListener('resize', onViewportChange)
  window.removeEventListener('fullscreenchange', onViewportChange)
  clearInterval(statsTimer)
  window.removeEventListener('keydown', onKey)
  window.removeEventListener('keyup', onKeyUp)
  window.removeEventListener('blur', endHoldGesture)
  document.removeEventListener('fullscreenchange', onFsChange)
  document.removeEventListener('click', closeCtx)
  clearInterval(progressBeat)
  progressBeat = 0
  // 编码进度 5 秒轮询的开关在 watch 手里，卸载时不掐会替已消失的实例继续打端点
  if (progTimer) {
    clearInterval(progTimer)
    progTimer = null
  }
  window.removeEventListener('pagehide', reportProgressByBeacon)
})

const RATE_ITEMS = [0.5, 0.75, 1, 1.25, 1.5, 2]
const FAMILY_ITEMS = [
  ['hei', '黑体'],
  ['song', '宋体'],
  ['mono', '等宽'],
]
const STROKE_ITEMS = [
  ['thick', '重墨'],
  ['thin', '描边'],
  ['shadow', '45°投影'],
]
</script>

<template>
  <!-- 外壳：播放器本体 + 下方弹幕条成一个整体（直角，与 B 站新版一致）；限宽居中，避免通栏 16:9 在高分屏上吃满一整屏 -->
  <div class="flex justify-center">
  <div ref="shellEl" class="shrink-0 bg-cardBg" :style="shellStyle">
  <div
    ref="root"
    class="group relative isolate aspect-video w-full select-none overflow-hidden bg-black outline-none"
    tabindex="0"
    @mousemove="wakeControls"
    @mousedown="onRootMouseDown"
    @mouseleave="hover.show = false; playing && wakeControls()"
    @contextmenu.prevent="openCtx"
  >
    <video
      v-if="page?.videoUrl"
      ref="video"
      :key="currentSrc"
      :src="currentSrc"
      preload="metadata"
      playsinline
      :loop="play.loop"
      :style="{ objectFit: play.fit }"
      class="h-full w-full"
      @click="togglePlay"
      @dblclick="toggleFullscreen"
      @timeupdate="currentTime = video.currentTime; bufferedEnd = video.buffered.length ? video.buffered.end(video.buffered.length - 1) : 0"
      @loadedmetadata="onLoadedMetadata"
      @play="playing = true; wakeControls()"
      @playing="waiting = false; stuckRecover = false; playing = true"
      @pause="onMediaPause"
      @waiting="onMediaWait"
      @stalled="onMediaWait"
      @canplay="onCanplay"
      @ended="onEnded"
      @volumechange="muted = video.muted; volume = video.muted ? volume : video.volume"
    >
      <!-- 字幕轨：有几条挂几条（以前只取 [0]，第二条以后的人根本没得选）。显示哪一路交给 applySubtitleModes，
           default 只给当前选中那一条（多条同时 default 浏览器只认第一条，给多了等于白给）；
           @load 到货那一刻再施加一次，防的就是「track 还没长出来、textTracks[i] 拿不到」那一瞬。 -->
      <track
        v-for="(u, i) in subtitleUrls"
        :key="i"
        :src="subtitleSrcs[i] || u"
        kind="subtitles"
        :label="subLabel(u, i)"
        :default="subOn && i === subIdx"
        @load="applySubtitleModes"
      />
    </video>
    <div v-else class="flex h-full w-full flex-col items-center justify-center gap-2 text-sm text-white/50">
      <span v-if="status === 1">这集还在进站路上，稍后再来</span>
      <span v-else-if="status === 3">这集进站失败，去首页看失败原因</span>
      <span v-else>本地还没有这个分 P 的成片</span>
    </div>

    <!-- 开场封面：压在 video 之上、弹幕画布之下（两者都不带 z，靠 DOM 顺序排队），
         object-fit 跟画面同一个值：免得「封面铺满、起播却出黑边」那样跳一下。
         常驻不拆只控 opacity：拆了就没得淡出，而且本地文件本来就拉过一遍。 -->
    <img
      v-if="page?.videoUrl && cover"
      v-show="coverShown"
      :src="cover"
      referrerpolicy="no-referrer"
      decoding="async"
      alt=""
      class="pointer-events-none absolute inset-0 h-full w-full transition-opacity duration-500"
      :class="coverShown ? 'opacity-100' : 'opacity-0'"
      :style="{ objectFit: play.fit }"
    />

    <DanmakuLayer
      v-if="page?.danmakuUrl || localItems.length > 0"
      ref="danmakuLayer"
      :src="page?.danmakuUrl ?? null"
      :local-items="localItems"
      :video="video"
      :enabled="danmaku.on"
      :font-size="layerFontSize"
      :line-count="engineDefaults.lineCount"
      :opacity="danmaku.opacity / 100"
      :area="danmaku.area / 100"
      :speed="danmaku.speed * (danmaku.speedSync ? rate : 1)"
      :modes="danmaku.modes"
      :family="danmaku.family"
      :bold="danmaku.bold"
      :stroke="danmaku.stroke"
      :keywords="keywordList"
      @delete-local="$emit('delete-danmaku', $event)"
      @report-local="$emit('report-danmaku', $event)"
    />

    <!-- 关灯模式：只黑掉画面以外的页面，视频和弹幕照常亮；点黑幕亮回（B 站同款）。必须传送到 body：root 带 isolate，黑幕留在子树里 z 再高也盖不住页面 -->
    <Teleport to="body">
      <div
        v-if="play.lightsOff && lightsStyle"
        class="fixed inset-0 z-[70] cursor-pointer bg-black/[0.92]"
        :style="lightsStyle"
        @click="play.lightsOff = false"
      ></div>
    </Teleport>

    <!-- 长按倍速提示 -->
    <div v-if="holdingSpeed" class="absolute right-4 top-4 z-30 rounded-md bg-black/70 px-2.5 py-1 text-xs text-stationBlue">
      ⏩ {{ play.holdSpeed }}× 快进中
    </div>

    <!-- 暂停时的大按钮：站长钦点右下角，不再堵画面正中（小电视只在 playing 缓冲时报到，与它错峰不同台）。 -->
    <button
      v-if="!playing && page?.videoUrl"
      class="absolute bottom-16 right-4 flex h-14 w-14 items-center justify-center rounded-full bg-black/45 text-white backdrop-blur transition hover:bg-stationPink"
      @click.stop="togglePlay"
    >
      <BIcon :meta="biPlay" class="ml-1 h-8 w-8" />
    </button>

    <!-- 缓冲转圈已搬进控制条（时间戳身后 14px 素圈，站长定稿）；正中不再挂大圈 -->

    <!-- 自动重试也没救回来时的手动恢复钮：站长口径摆播放器左上角，不再堵画面正中（加速提示在右上，不碰面） -->
    <button
      v-if="stuckRecover"
      class="absolute left-3 top-3 z-20 rounded-lg bg-black/70 px-4 py-2 text-sm text-white backdrop-blur transition hover:bg-stationBlue"
      @click.stop="manualUnstick"
    >
      缓冲停滞，点击恢复播放
    </button>

    <!-- 缓冲加载小电视已拆：它和右下角暂停大按钮同角同家，状态机摆动的瞬间会叠在一起闪（站长令删）。 -->

    <!-- 底部控制条：淡背与续播气泡同源，opacity 缓进缓出 500ms（v-show 只能 display 硬切不淡，改 pointer-events-none 兼管隐藏时不挡点）-->
    <div
      class="absolute inset-x-0 bottom-0 z-10 bg-gradient-to-t from-black/85 via-black/35 to-transparent px-3 pb-1.5 pt-10 transition-opacity duration-500"
      :class="showControls ? 'opacity-100' : 'opacity-0 pointer-events-none'"
      @click.stop
      @dblclick.stop
      @mousemove="wakeControls"
    >
      <!-- 进度条 -->
      <div
        ref="trackEl"
        class="group/track relative -mt-7 mb-1 cursor-pointer py-2"
        @pointerdown="trackDown"
        @pointermove="trackMove"
        @pointerup="trackUp"
        @pointerleave="hover.show = false"
      >
        <!-- 弹幕高能条：官方口径的直方图贴轨道正上方（峰 48px 白 15% 平滑 5s，站长定稿）。
             stretch viewBox 与轨道同宽免 ResizeObserver，pointer-events-none 不挡 seek，
             住在控制条里随它淡入淡出（官方同款显隐）；无弹幕时整条不存在，零视觉负担 -->
        <svg
          v-if="heatPath"
          class="pointer-events-none absolute inset-x-0 bottom-[calc(50%+2px)] w-full"
          :style="{ height: HEAT.peakPx + 'px' }"
          :viewBox="'0 0 ' + heat.length + ' 100'"
          preserveAspectRatio="none"
          aria-hidden="true"
        >
          <defs>
            <clipPath :id="heatClipId">
              <rect x="0" y="0" :width="heatPlayedUnits" height="100" />
            </clipPath>
          </defs>
          <path :d="heatPath" fill="#fff" :opacity="HEAT.alpha" />
          <path :d="heatPath" fill="#fff" :opacity="HEAT.playedAlpha" :clip-path="'url(#' + heatClipId + ')'" />
        </svg>
        <div class="relative h-1 w-full rounded-full bg-white/25 transition-[height] group-hover/track:h-1.5">
          <div class="absolute inset-y-0 left-0 rounded-full bg-white/45" :style="{ width: duration ? (bufferedEnd / duration) * 100 + '%' : '0%' }"></div>
          <div class="absolute inset-y-0 left-0 rounded-full bg-stationPink" :style="{ width: duration ? (currentTime / duration) * 100 + '%' : '0%' }"></div>
          <div
            class="absolute top-1/2 h-3.5 w-3.5 -translate-x-1/2 -translate-y-1/2 rounded-full bg-stationPink opacity-0 shadow transition-opacity group-hover/track:opacity-100"
            :style="{ left: duration ? (currentTime / duration) * 100 + '%' : '0%' }"
          ></div>
        </div>
        <div
          v-if="hover.show"
          class="pointer-events-none absolute -top-6 -translate-x-1/2 rounded bg-black/80 px-1.5 py-0.5 text-[11px] text-white"
          :style="{ left: hover.x + 'px' }"
        >
          {{ fmt(hover.t) }}
        </div>
      </div>

      <div class="flex items-center gap-0.5 text-white">
        <button class="flex h-8 w-8 items-center justify-center rounded hover:bg-white/10" :title="playing ? '暂停（空格）' : '播放（空格）'" @click="togglePlay">
          <BIcon v-if="!playing" :meta="biPlay" class="h-5 w-5" />
          <!-- 暂停态官方包里没有，自绘两条圆角竖条顶上（不碰 bicons.js） -->
          <svg v-else viewBox="0 0 24 24" class="h-5 w-5" fill="currentColor"><rect x="6" y="5" width="4" height="14" rx="1.2" /><rect x="14" y="5" width="4" height="14" rx="1.2" /></svg>
        </button>

        <span class="mx-1.5 text-xs tabular-nums text-white/90">{{ fmt(currentTime) }} / {{ fmt(duration) }}</span>

        <span class="flex-1"></span>

        <!-- 音量：悬停弹 B 站同款竖条（深底圆角盒：数字 28 + 轨道 60 + 留白，骑在音量钮正上方）。
             可见性走 volVisible 状态机（hover+拖拽锁+收起宽限），不用 CSS group-hover：
             弹层与按钮间那 8px 是 :hover 死区，穿越瞬间会把弹层抽走、拖拽腰断。 -->
        <!-- Hi-Res 无损音质徽记（B 站官方 npd-ctrl-flac 控件徽记同源：path 直抄官方播放器包 c586 模块 780，
             无 fill 靠 currentColor 上色）：坐在音量钮旁，官方也是这个位置；只在音频真是 flac 时上桌。 -->
        <span v-if="hiResActive" class="flex h-8 items-center px-1 text-white" title="Hi-Res 无损音质">
          <BIcon :meta="biHiRes" class="h-[20px] w-auto" />
        </span>

        <div ref="volZoneEl" class="relative flex h-8 items-center gap-1 rounded px-1 hover:bg-white/10" @mouseenter="volEnter" @mouseleave="volLeave">
          <!-- after 只撑热区（24→32，正好吃掉容器 px-1 与 gap-1，不越界抢邻居）：盒子和图标位置一个字没动 -->
          <button class="relative flex h-6 w-6 items-center justify-center after:absolute after:-inset-1 after:content-['']" :title="muted ? '取消静音（M）' : '静音（M）'" @click="muted ? (applyVolume(volume || 1)) : applyVolume(0)">
            <span class="relative flex h-5 w-5 items-center justify-center">
              <BIcon :meta="biVolume" class="h-5 w-5" />
              <!-- 静音态官方包里没有，白身斜杠叠加在音量图标上（纯 CSS，不碰 bicons.js） -->
              <span v-if="muted || volume === 0" class="pointer-events-none absolute left-1/2 top-1/2 h-[1.6px] w-[15px] -translate-x-1/2 -translate-y-1/2 rotate-45 rounded bg-white"></span>
            </span>
          </button>
          <!-- 透明桥：填平弹层底边到容器顶边的 8px 间隙，鼠标垂直上移全程命中容器子树，不会触发 leave；观感零变化 -->
          <span class="pointer-events-auto absolute bottom-8 left-1/2 h-2 w-8 -translate-x-1/2" aria-hidden="true"></span>
          <div
            ref="volPopEl"
            class="absolute bottom-[40px] left-1/2 z-20 -translate-x-1/2 rounded-[4px] bg-[rgba(20,20,20,0.9)] backdrop-blur transition-opacity duration-150"
            :class="volVisible ? 'pointer-events-auto opacity-100' : 'pointer-events-none opacity-0'"
          >
            <div class="flex w-8 flex-col items-center pb-2">
              <span class="flex h-7 items-center justify-center text-xs tabular-nums text-[#e5e9ef]">{{ volumePercent }}</span>
              <div
                ref="volTrackEl"
                class="relative h-[60px] w-2 cursor-pointer touch-none"
                @pointerdown="volTrackDown"
                @pointermove="volTrackMove"
                @pointerup="volTrackUp"
                @pointercancel="volTrackUp"
              >
                <span class="absolute left-1/2 top-0 h-full w-0.5 -translate-x-1/2 rounded bg-white/90"></span>
                <span class="absolute bottom-0 left-1/2 w-0.5 -translate-x-1/2 rounded bg-[#00aedc]" :style="{ height: volumePercent + '%' }"></span>
                <span class="absolute left-1/2 h-3 w-3 -translate-x-1/2 rounded-full bg-[#00aedc]" :style="{ bottom: `calc(${volumePercent}% - 6px)` }"></span>
              </div>
            </div>
          </div>
        </div>

        <!-- 清晰度（只展示不切换） -->
        <div class="relative">
          <button
            class="flex h-8 items-center justify-center rounded px-1.5 text-xs hover:bg-white/10"
            title="清晰度（本地馆藏单档）"
            @click.stop="qualityMenu = !qualityMenu; rateMenu = false; gearPanel = false; subPanel = false; wakeControls()"
          >
            {{ qualityLabel }}
          </button>
          <div v-if="qualityMenu" class="absolute bottom-9 right-0 z-20 w-36 rounded-lg bg-[#222]/95 py-1 text-xs backdrop-blur" @click.stop>
            <div
              v-for="q in QUALITIES"
              :key="q"
              class="flex items-center justify-between px-3 py-2"
              :class="q === qualityLabel ? 'text-stationPink' : 'text-white/30'"
            >
              <span>{{ q }}</span>
              <span v-if="q === qualityLabel" class="text-[10px] text-white/50">馆藏</span>
            </div>
            <div class="mt-1 border-t border-white/10 px-3 py-1.5 text-[10px] text-white/40">本地馆藏仅此档</div>
          </div>
        </div>

        <!-- 倍速 -->
        <div class="relative">
          <button
            class="flex h-8 min-w-9 items-center justify-center rounded px-1 text-xs tabular-nums hover:bg-white/10"
            title="播放速度"
            @click.stop="rateMenu = !rateMenu; qualityMenu = false; gearPanel = false; subPanel = false; wakeControls()"
          >
            {{ rate === 1 ? '倍速' : rate + '×' }}
          </button>
          <div v-if="rateMenu" class="absolute bottom-9 right-0 z-20 w-20 rounded-lg bg-[#222]/95 py-1 text-center text-xs backdrop-blur" @click.stop>
            <button
              v-for="r in RATE_ITEMS"
              :key="r"
              class="block w-full py-1.5 tabular-nums transition hover:bg-white/10"
              :class="r === rate ? 'text-stationPink' : 'text-white/85'"
              @click="setRate(r)"
            >
              {{ r }}×
            </button>
          </div>
        </div>

        <!-- 截图 -->
        <button class="flex h-8 w-8 items-center justify-center rounded hover:bg-white/10" title="截图当前画面" @click="takeScreenshot">
          <svg viewBox="0 0 24 24" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="1.7">
            <path d="M4 8h3l1.5-2h7L17 8h3v11H4V8z" stroke-linejoin="round" />
            <circle cx="12" cy="13" r="3.4" />
          </svg>
        </button>

        <!-- 字幕（多轨）：片子里真躺着字幕文件才上桌（无轨时这枚钮根本没东西可摆）。
             判的是 subtitleUrls 而不是 page.subtitleUrls?.length：控制条不在 v-if="page?.videoUrl" 的盒子里，
             page 为空时直接读它会敲在 null 上，走 subtitleUrls 那枚已经兼容空 page 的计算属性。 -->
        <button
          v-if="subtitleUrls.length"
          class="flex h-8 items-center justify-center rounded px-1.5 text-xs hover:bg-white/10"
          :class="subPanel ? 'text-stationBlue' : ''"
          title="字幕"
          @click.stop="subPanel = !subPanel; gearPanel = false; playPanel = false; rateMenu = false; qualityMenu = false; wakeControls()"
        >
          字幕
        </button>

        <!-- 播放设定齿轮 -->
        <button
          class="flex h-8 w-8 items-center justify-center rounded hover:bg-white/10"
          :class="playPanel ? 'text-stationBlue' : ''"
          title="播放设定"
          @click.stop="playPanel = !playPanel; gearPanel = false; rateMenu = false; qualityMenu = false; subPanel = false; wakeControls()"
        >
          <BIcon :meta="biSetting" class="h-5 w-5" />
        </button>

        <!-- 画中画 -->
        <button class="flex h-8 w-8 items-center justify-center rounded hover:bg-white/10" title="画中画" @click="togglePip">
          <BIcon :meta="biPip" class="h-5 w-5" />
        </button>

        <!-- 全屏 -->
        <button class="flex h-8 w-8 items-center justify-center rounded hover:bg-white/10" title="全屏（F）" @click="toggleFullscreen">
          <BIcon :meta="biFull" class="h-5 w-5" />
        </button>
      </div>
    </div>

    <!-- 播放设定面板：分站精简版 -->
    <div
      v-if="playPanel"
      class="absolute bottom-14 right-3 z-20 w-[300px] rounded-xl bg-[#222]/95 p-3.5 text-xs text-white/90 shadow-xl backdrop-blur"
      @click.stop
      @mousemove="wakeControls"
    >
      <p class="relative mb-3 text-center text-[13px] font-medium text-white">
        播放设定
        <button class="absolute right-0 top-0 text-white/50 transition hover:text-white" @click="playPanel = false">✕</button>
      </p>

      <div class="mb-2.5 flex items-center justify-between">
        <span class="text-white/85">开播封面</span>
        <button
          class="relative h-5 w-9 rounded-full transition"
          :class="play.preCover ? 'bg-stationBlue' : 'bg-white/25'"
          title="关掉后开播前不摆封面，直接看首帧；开播后的行为不变"
          @click="play.preCover = !play.preCover"
        >
          <span class="absolute top-0.5 h-4 w-4 rounded-full bg-white shadow transition-all" :class="play.preCover ? 'left-[18px]' : 'left-0.5'"></span>
        </button>
      </div>
      <div class="mb-2.5 flex items-center justify-between">
        <span class="text-white/85">自动播放</span>
        <button class="relative h-5 w-9 rounded-full transition" :class="play.autoplay ? 'bg-stationBlue' : 'bg-white/25'" @click="play.autoplay = !play.autoplay">
          <span class="absolute top-0.5 h-4 w-4 rounded-full bg-white shadow transition-all" :class="play.autoplay ? 'left-[18px]' : 'left-0.5'"></span>
        </button>
      </div>
      <div class="mb-2.5 flex items-center justify-between">
        <span class="text-white/85">单集循环</span>
        <button class="relative h-5 w-9 rounded-full transition" :class="play.loop ? 'bg-stationBlue' : 'bg-white/25'" @click="play.loop = !play.loop">
          <span class="absolute top-0.5 h-4 w-4 rounded-full bg-white shadow transition-all" :class="play.loop ? 'left-[18px]' : 'left-0.5'"></span>
        </button>
      </div>
      <div class="mb-2.5 flex items-center justify-between">
        <span class="text-white/85">播完自动播下一集</span>
        <button
          class="relative h-5 w-9 rounded-full transition"
          :class="[play.autoNext ? 'bg-stationBlue' : 'bg-white/25', hasNextPage ? '' : 'opacity-40']"
          :title="hasNextPage ? '' : '已经是最后一集'"
          @click="play.autoNext = !play.autoNext"
        >
          <span class="absolute top-0.5 h-4 w-4 rounded-full bg-white shadow transition-all" :class="play.autoNext ? 'left-[18px]' : 'left-0.5'"></span>
        </button>
      </div>

      <span class="mb-1 block text-white/60">视频比例</span>
      <div class="mb-2.5 flex gap-1.5">
        <button
          v-for="f in [['contain', '自动适配'], ['cover', '充满画面']]"
          :key="f[0]"
          class="flex-1 rounded py-1.5 transition"
          :class="play.fit === f[0] ? 'bg-stationBlue text-white' : 'bg-white/10 hover:bg-white/20'"
          @click="play.fit = f[0]"
        >
          {{ f[1] }}
        </button>
      </div>

      <span class="mb-1 block text-white/60">右键长按倍速</span>
      <div class="mb-2.5 flex gap-1.5">
        <button
          v-for="h in [[0, '关'], [1.5, '1.5×'], [2, '2×'], [3, '3×']]"
          :key="h[0]"
          class="flex-1 rounded py-1.5 tabular-nums transition"
          :class="play.holdSpeed === h[0] ? 'bg-stationBlue text-white' : 'bg-white/10 hover:bg-white/20'"
          @click="play.holdSpeed = h[0]"
        >
          {{ h[1] }}
        </button>
      </div>

      <span v-if="presentRows.length" class="mb-1 block text-white/60">播放版本</span>
      <div v-if="presentRows.length" class="mb-1.5 flex flex-col gap-1">
        <!-- 行根从 button 改成 div：站长那枚「删这一版」要坐在行里，button 套 button 不合法
             （浏览器会把内层钮踢出去，点谁都不对）。外层这枚 div 只当盒子不接点击，切版本仍是那枚整行钮。 -->
        <div v-for="c in presentRows" :key="c[0]" class="flex items-center gap-1">
          <button
            class="relative flex min-w-0 flex-1 items-center justify-between rounded px-2 py-1.5 transition"
            :class="playingCodec() === c[0] ? 'bg-stationBlue text-white' : versionInfo(c[0]).state === 'stock' ? 'bg-white/10 hover:bg-white/20' : versionInfo(c[0]).state === 'recycled' ? 'cursor-not-allowed bg-white/5 text-white/30' : 'bg-white/5 text-white/50 hover:bg-white/10'"
            :title="versionInfo(c[0]).state === 'recycled' ? '这一档已删除，媒体文件与台账记录在回收站里' : c[0] === 'hevc' ? 'HEVC 在部分浏览器可能放不动' : ''"
            @click="clickVersion(c[0])"
          >
            <span class="shrink-0 whitespace-nowrap">
              <span v-if="playingCodec() === c[0]" class="mr-1 text-[9px]">▶</span>{{ c[1] }}
            </span>
            <span class="ml-2 min-w-0 truncate text-right text-[10px] tabular-nums whitespace-nowrap">
              <template v-if="versionInfo(c[0]).state === 'stock'">{{ stockBadge(c[0]) }}</template>
              <!-- 已删除那一档只留这句话：文件都在回收站里了，再报大小与进度就是拿陈账冒充实况（进度条那条 v-if 认 downloading，也跟着闭嘴）。 -->
              <template v-else-if="versionInfo(c[0]).state === 'recycled'">{{ versionInfo(c[0]).pending ? '删除中……' : '已删除（文件在回收站）' }}</template>
              <template v-else-if="versionInfo(c[0]).state === 'downloading'">进站中{{ progTail(c[0]) }}</template>
              <template v-else-if="versionInfo(c[0]).state === 'failed'">失败 · 重点重试</template>
            </span>
            <!-- 进站中的行底细进度条：与站长室队列同款，不占字位也能看出在动 -->
            <span
              v-if="versionInfo(c[0]).state === 'downloading' && codecProg[c[0]] && codecProg[c[0]].percent > 0"
              class="absolute bottom-0 left-0 h-0.5 rounded-full bg-white/50 transition-all"
              :style="{ width: `${Math.min(100, Math.round(codecProg[c[0]].percent))}%` }"
            ></span>
          </button>
          <!-- 站长专用那一枚（走整版那口，一次 = 回收站里的一个组）。真门在端点里（站长与这稿投稿人删得动），这里只是不把钮摆给不该按的人；
               按下去那一行就转成「已删除」那一档，canDeleteRow 随之回假、这枚钮自己退场（不给人按第二遍），
               deletingCodecs 只作同一拍里防连点的兵（deleteCodecRow 开头还有一道）。 -->
          <button
            v-if="canDeleteRow(c[0])"
            class="flex h-7 shrink-0 items-center justify-center rounded bg-white/5 px-1.5 text-[10px] whitespace-nowrap text-white/50 transition hover:bg-stationPink hover:text-white disabled:cursor-not-allowed disabled:opacity-40"
            :disabled="deletingCodecs.has(c[0])"
            :title="`删除这一档（${c[1]}）：所有分 P 的媒体文件与台账记录一并移入回收站，主版本不受影响`"
            @click="deleteCodecRow(c[0])"
          >
            删
          </button>
        </div>
      </div>
      <!-- 后端那句人话原样贴在这里（「已删除 N 行、N 个文件（进了回收站，站长能整组恢复）」/「没有这个版本」/「文件正被占用…」都是它说的，前端不改写）；
           它没说才轮到兜底那句，失败走玫红提醒，成功只算一行情报。 -->
      <p v-if="codecDelNote" class="mb-2 px-1 text-[10px]" :class="codecDelNote.bad ? 'text-stationPink' : 'text-white/50'">{{ codecDelNote.text }}</p>
      <button v-if="missingRows.length" class="mb-2 px-1 text-left text-[10px] text-white/40 transition hover:text-white/70" @click="showMore = !showMore">
        {{ showMore ? '▾ 收起其他版本' : '▸ 加档其他版本' }}
      </button>
      <div v-if="showMore && missingRows.length" class="mb-2 flex flex-col gap-1">
        <button
          v-for="c in missingRows"
          :key="c[0]"
          class="flex items-center justify-between rounded bg-white/5 px-2 py-1.5 text-white/50 transition hover:bg-white/10"
          :title="c[0] === 'hevc' ? 'HEVC 在部分浏览器可能放不动' : ''"
          @click="clickVersion(c[0])"
        >
          <span class="shrink-0 whitespace-nowrap">{{ c[1] }}</span>
          <!-- 三段各不缩、各自不折行：以前三个元素挤在一行又都能压缩，数字一长就堆成三行（站长 11-02 那张图）。 -->
          <span class="flex min-w-0 items-center justify-end gap-2">
            <span v-if="estBadge(c[0])" class="min-w-0 truncate text-[10px] tabular-nums whitespace-nowrap text-white/35" :title="estTitle(c[0])">{{ estBadge(c[0]) }}</span>
            <span class="shrink-0 text-[10px] whitespace-nowrap">加档</span>
          </span>
        </button>
      </div>

      <div class="flex items-center justify-between">
        <span class="text-white/85">关灯模式</span>
        <button class="relative h-5 w-9 rounded-full transition" :class="play.lightsOff ? 'bg-stationBlue' : 'bg-white/25'" @click="play.lightsOff = !play.lightsOff">
          <span class="absolute top-0.5 h-4 w-4 rounded-full bg-white shadow transition-all" :class="play.lightsOff ? 'left-[18px]' : 'left-0.5'"></span>
        </button>
      </div>
    </div>

    <!-- 字幕面板：照弹幕设定面板的形状。顶部那枚总开关独立于选轨（只有一轨时也得能关掉），
         底下每轨一行单选，选中那行吃现成的蓝底选中态。 -->
    <div
      v-if="subPanel && subtitleUrls.length"
      class="absolute bottom-14 right-3 z-20 w-[300px] rounded-xl bg-[#222]/95 p-3.5 text-xs text-white/90 shadow-xl backdrop-blur"
      @click.stop
      @mousemove="wakeControls"
    >
      <p class="relative mb-3 text-center text-[13px] font-medium text-white">
        字幕
        <button class="absolute right-0 top-0 text-white/50 transition hover:text-white" @click="subPanel = false">✕</button>
      </p>

      <div class="mb-2.5 flex items-center justify-between">
        <span class="text-white/85">{{ subOn ? '字幕开' : '关掉字幕' }}</span>
        <button
          class="relative h-5 w-9 rounded-full transition"
          :class="subOn ? 'bg-stationBlue' : 'bg-white/25'"
          title="关掉后画面里不飘字；字幕文件仍在原地不动，随时开得回来"
          @click="subOn = !subOn; applySubtitleModes()"
        >
          <span class="absolute top-0.5 h-4 w-4 rounded-full bg-white shadow transition-all" :class="subOn ? 'left-[18px]' : 'left-0.5'"></span>
        </button>
      </div>

      <span class="mb-1 block text-white/60">字幕轨</span>
      <div class="mb-2 flex flex-col gap-1">
        <button
          v-for="(u, i) in subtitleUrls"
          :key="i"
          class="flex items-center justify-between rounded px-2 py-1.5 transition"
          :class="subOn && i === subIdx ? 'bg-stationBlue text-white' : 'bg-white/10 hover:bg-white/20'"
          :title="u"
          @click="pickSubtitle(i)"
        >
          <span class="min-w-0 truncate">{{ subLabel(u, i) }}</span>
          <span v-if="subOn && i === subIdx" class="ml-2 shrink-0 text-[10px] whitespace-nowrap text-white/50">显示中</span>
        </button>
      </div>
    </div>

    <!-- 弹幕设定面板：B 站原型，只留刚需 -->
    <div
      v-if="gearPanel"
      class="absolute bottom-14 left-3 z-20 w-[300px] rounded-xl bg-[#222]/95 p-3.5 text-xs text-white/90 shadow-xl backdrop-blur"
      @click.stop
      @mousemove="wakeControls"
    >
      <p class="relative mb-3 text-center text-[13px] font-medium text-white">
        弹幕设定
        <button class="absolute right-0 top-0 text-white/50 transition hover:text-white" @click="gearPanel = false">✕</button>
      </p>

      <span class="mb-1.5 block text-white/60">按类型过滤</span>
      <div class="mb-3 flex gap-2">
        <button v-for="f in [['scroll', '滚动'], ['fixed', '固定'], ['color', '彩色']]" :key="f[0]" class="flex flex-1 flex-col items-center gap-1" @click="toggleDmFilter(f[0])">
          <span class="flex h-8 w-full items-center justify-center rounded-md text-sm transition" :class="filterOn(f[0]) ? 'bg-white text-black' : 'bg-white/15 text-white/50'">
            <template v-if="f[0] === 'scroll'">≡</template>
            <template v-else-if="f[0] === 'fixed'">…</template>
            <template v-else>调色</template>
          </span>
          <span :class="filterOn(f[0]) ? 'text-white' : 'text-white/50'">{{ f[1] }}</span>
        </button>
      </div>

      <label class="mb-2 flex items-center gap-2">
        <span class="w-14 shrink-0 text-white/60">显示区域</span>
        <input v-model.number="danmaku.area" type="range" min="25" max="100" step="25" class="min-w-0 flex-1 accent-stationBlue" />
        <span class="w-10 shrink-0 text-right tabular-nums">{{ danmaku.area }}%</span>
      </label>
      <label class="mb-2 flex items-center gap-2">
        <span class="w-14 shrink-0 text-white/60">不透明度</span>
        <input v-model.number="danmaku.opacity" type="range" min="10" max="100" step="5" class="min-w-0 flex-1 accent-stationBlue" />
        <span class="w-10 shrink-0 text-right tabular-nums">{{ danmaku.opacity }}%</span>
      </label>
      <label class="mb-2 flex items-center gap-2">
        <span class="w-14 shrink-0 text-white/60">弹幕字号</span>
        <input v-model.number="danmaku.fontScale" type="range" min="50" max="150" step="5" class="min-w-0 flex-1 accent-stationBlue" />
        <span class="w-10 shrink-0 text-right tabular-nums">{{ danmaku.fontScale }}%</span>
      </label>
      <label class="mb-2.5 flex items-center gap-2">
        <span class="w-14 shrink-0 text-white/60">弹幕速度</span>
        <input :value="speedTickIndex" type="range" min="0" max="4" step="1" class="min-w-0 flex-1 accent-stationBlue" @input="danmaku.speed = SPEED_TICKS[Number($event.target.value)][0]" />
        <span class="w-10 shrink-0 text-right">{{ SPEED_TICKS[speedTickIndex][1] }}</span>
      </label>

      <div class="mb-2.5 flex items-center gap-4 pl-0.5">
        <label class="flex cursor-pointer items-center gap-1.5 text-white/85">
          <input v-model="danmaku.speedSync" type="checkbox" class="accent-stationBlue" />
          速度同步倍速
        </label>
        <label class="flex cursor-pointer items-center gap-1.5 text-white/85">
          <input v-model="danmaku.bold" type="checkbox" class="accent-stationBlue" />
          粗体
        </label>
      </div>

      <span class="mb-1 block text-white/60">弹幕字体</span>
      <div class="mb-2.5 flex gap-1">
        <button
          v-for="[k, label] in FAMILY_ITEMS"
          :key="k"
          class="flex-1 rounded py-1 transition"
          :class="danmaku.family === k ? 'bg-stationBlue text-white' : 'bg-white/10 hover:bg-white/20'"
          @click="danmaku.family = k"
        >
          {{ label }}
        </button>
      </div>

      <span class="mb-1 block text-white/60">描边类型</span>
      <div class="mb-2.5 flex gap-1">
        <button
          v-for="[k, label] in STROKE_ITEMS"
          :key="k"
          class="flex-1 rounded py-1 transition"
          :class="danmaku.stroke === k ? 'bg-stationBlue text-white' : 'bg-white/10 hover:bg-white/20'"
          @click="danmaku.stroke = k"
        >
          {{ label }}
        </button>
      </div>

      <span class="mb-1 block text-white/60">屏蔽词</span>
      <input
        v-model="danmaku.keywords"
        type="text"
        placeholder="逗号分隔，命中的弹幕直接隐藏"
        class="mb-3 w-full rounded-md bg-white/10 px-2 py-1.5 text-white placeholder-white/30 focus:bg-white/15 focus:outline-none"
      />

      <button class="w-full rounded-md border border-white/25 py-1.5 text-white/85 transition hover:bg-white/10" @click="resetDanmakuPrefs">恢复默认设置</button>
    </div>

    <!-- 右键菜单 -->
    <div
      v-if="ctx.visible"
      class="absolute z-30 w-40 overflow-hidden rounded-lg bg-[#222]/95 py-1 text-xs text-white/90 shadow-xl backdrop-blur"
      :style="{ left: ctx.x + 'px', top: ctx.y + 'px' }"
      @click.stop
      @contextmenu.prevent
    >
      <button class="block w-full px-3 py-2 text-left transition hover:bg-white/10" @click="takeScreenshot">截图当前画面</button>
      <button class="block w-full px-3 py-2 text-left transition hover:bg-white/10" @click="copyLink">复制带进度链接</button>
      <button class="block w-full px-3 py-2 text-left transition hover:bg-white/10" @click="togglePip">画中画播放</button>
      <div class="my-1 border-t border-white/10"></div>
      <button class="block w-full px-3 py-2 text-left transition hover:bg-white/10" @click="openStats">视频统计信息</button>
    </div>

    <!-- 统计信息：B 站同款左上角半透面板，锁在播放框内不挡页面 -->
    <div
      v-if="statsOpen"
      class="absolute left-2 top-2 z-40 max-h-[calc(100%-16px)] w-[520px] max-w-[calc(100%-16px)] overflow-y-auto rounded-2xl bg-black/80 px-4 py-3 text-[12px] text-white shadow-2xl backdrop-blur-sm"
      @click.stop
      @mousemove="wakeControls"
    >
      <p class="relative mb-2 text-center text-sm font-medium">
        统计信息
        <button class="absolute -top-0.5 right-0 px-1 text-base leading-none text-white/80 transition hover:text-white" @click="closeStats">✕</button>
      </p>
      <dl class="grid grid-cols-[118px_1fr] items-center gap-x-3 gap-y-1">
        <template v-for="(val, key) in stats" :key="key">
          <dt class="text-right text-white/55">{{ key }}:</dt>
          <dd class="break-all text-white/85">{{ val }}</dd>
        </template>
        <dt class="text-right text-white/55">Video Speed:</dt>
        <dd class="flex items-center gap-2">
          <canvas ref="sparkVideo" class="h-4 flex-1"></canvas>
          <span class="w-[90px] shrink-0 text-white/85">{{ speedText }}</span>
        </dd>
        <dt class="text-right text-white/55">Audio Speed:</dt>
        <dd class="flex items-center gap-2">
          <span class="h-px flex-1 bg-white/85"></span>
          <span class="w-[90px] shrink-0 text-white/85">—</span>
        </dd>
        <dt class="text-right text-white/55">Network Activity:</dt>
        <dd class="flex items-center gap-2">
          <canvas ref="sparkNet" class="h-4 flex-1"></canvas>
          <span class="w-[90px] shrink-0 text-white/85">{{ netText }}</span>
        </dd>
        <dt class="text-right text-white/55">分站-Log:</dt>
        <dd class="flex items-center gap-2">
          <button class="rounded bg-white/15 px-2.5 py-1 text-white/90 transition hover:bg-white/25" @click="copyLog">[Copy]</button>
          <button class="rounded bg-white/15 px-2.5 py-1 text-white/90 transition hover:bg-white/25" @click="downloadLog">[Download]</button>
        </dd>
      </dl>
    </div>
  </div>

    <!-- 弹幕条（B 站同款位，播放器下方）：左计数，中输入，右发送。分站不发 B 站，发出的弹幕只在本页飘落 -->
    <div class="flex items-center gap-3 border-t border-black/5 px-3 py-2 shadow-[0_2px_10px_rgba(0,0,0,0.12)]">
      <div class="flex shrink-0 items-center gap-3 text-xs text-inkFaint">
        <span class="flex items-center gap-1 tabular-nums" title="已归档的 B 站弹幕总数">
          <DanmakuCountIcon class="h-3.5 w-3.5" />
          {{ danmakuCount != null ? `${danmakuCount} 条弹幕` : '弹幕未归档' }}
        </span>
        <span v-if="localItems.length > 0" class="hidden text-inkFaint/70 sm:inline">站内弹幕 +{{ localItems.length }}</span>
        <!-- 弹幕开关与设定：从控制栏搬来计数右边（B 站把弹幕设定也放弹幕栏一带） -->
        <button class="flex h-8 w-8 items-center justify-center rounded hover:bg-black/5" title="弹幕（D）" @click="danmaku.on = !danmaku.on">
          <!-- 官方弹幕枚：开启对勾（第二条 path）染 B 站蓝；关闭态换官方同款「弹+右下禁止符」枚，整体灰掉 -->
          <BIcon :meta="danmaku.on ? biDmOn : biDmOff" class="h-6 w-6 transition" :class="danmaku.on ? 'dm-on text-inkSub' : 'text-inkFaint/50'" />
        </button>
        <button
          class="flex h-8 w-8 items-center justify-center rounded hover:bg-black/5"
          :class="gearPanel ? 'bg-stationBlue/10' : ''"
          title="弹幕设定"
          @click.stop="gearPanel = !gearPanel; playPanel = false; rateMenu = false; qualityMenu = false; subPanel = false; wakeControls()"
        >
          <BIcon :meta="biDmSet" class="h-6 w-6 text-inkSub" />
        </button>
      </div>
      <div class="flex min-w-0 flex-1 items-center gap-2 rounded-full bg-[#f1f2f3] px-3.5 py-1.5">
        <svg viewBox="0 0 16 16" class="h-3.5 w-3.5 shrink-0 text-inkFaint" fill="none" stroke="currentColor" stroke-width="1.4">
          <path d="M3 12.5 8 3l5 9.5M4.8 9.2h6.4" stroke-linecap="round" stroke-linejoin="round" />
        </svg>
        <input
          v-model="danmakuDraft"
          type="text"
          maxlength="100"
          placeholder="发个友善的站内弹幕见证当下（仅本页可见，不上传 B 站）"
          class="min-w-0 flex-1 bg-transparent text-xs text-inkMain outline-none placeholder:text-inkFaint/70"
          @keydown.enter.prevent="sendDanmaku"
        />
      </div>
      <button
        class="shrink-0 rounded-md bg-stationBlue px-4 py-1.5 text-xs font-medium text-white transition enabled:hover:opacity-85 disabled:cursor-not-allowed disabled:opacity-40"
        :disabled="!danmakuDraft.trim()"
        @click="sendDanmaku"
      >
        发送
      </button>
    </div>
  </div>
  </div>
</template>

<style scoped>
/* 弹幕开启态：官方弹幕枚的第二条 path（对勾）染 B 站蓝。
   BIcon 用 v-html 注入图标内容，scoped 样式够不到，得 :deep。 */
.dm-on :deep(path:nth-of-type(2)) {
  fill: #00a1d6;
}
</style>
