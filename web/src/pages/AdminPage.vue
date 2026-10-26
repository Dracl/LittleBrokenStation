<script setup>
import { onMounted, ref } from 'vue'

const health = ref(null)
const settings = ref(null)
const loginState = ref(null)
const cookieDraft = ref('')
const loginBusy = ref(false)
const loginNotice = ref(null) // { kind, text }

function notify(kind, text) {
  loginNotice.value = { kind, text }
}

const notice = ref(null)

// ─── 下载设置（站长室面板）───────────────────────────────────────────
const eng = ref(null)
const engDraft = ref(null)
const engBusy = ref(false)
const engNotice = ref(null) // { kind, text }
const engWarnings = ref([])
const keywordDraft = ref('')
const uidDraft = ref('')

const QUALITY_OPTIONS = [
  [127, '自动上限（8K 探顶）'],
  [126, '杜比视界'],
  [125, 'HDR 真彩'],
  [120, '4K 超清'],
  [116, '1080P 60帧'],
  [112, '1080P 高码率'],
  [80, '1080P 高清'],
  [74, '720P 60帧'],
  [64, '720P 高清'],
  [32, '480P 清晰'],
  [16, '360P 流畅'],
]
const AUDIO_OPTIONS = [
  [31251, 'Hi-Res 无损'],
  [31250, 'Dolby Atmos'],
  [30280, '高质量'],
  [30232, '中质量'],
  [30216, '低质量'],
]
const CODEC_OPTIONS = [
  [7, 'AVC（H.264，兼容最好）'],
  [12, 'HEVC（H.265，更省空间）'],
  [13, 'AV1（新设备首选）'],
]
const HW_OPTIONS = [
  [0, '未设'],
  [1, '自动探测'],
  [2, '强制软解'],
  [3, 'NVIDIA NVENC'],
  [4, 'Intel QSV'],
  [5, 'AMD AMF'],
  [6, 'VA-API'],
  [7, 'VideoToolbox'],
]
const OUTPUT_OPTIONS = [
  [0, 'ASS（观看页认它）'],
  [1, 'XML'],
  [2, 'ASS + XML 都要'],
]
const LAYOUT_OPTIONS = [
  [0, '默认'],
  [1, '异步排版'],
  [2, '同步排版'],
]
const LANG_OPTIONS = [
  [0, '不限'],
  [1, '简体中文'],
  [2, '繁体中文'],
  [3, '英语'],
  [4, '日语'],
  [5, '韩语'],
]
const TYPE_OPTIONS = [
  [0, '人工字幕优先'],
  [1, 'AI 字幕优先'],
]
const ORDER_OPTIONS = [
  [1, '自然数 1,2,3'],
  [2, '前导零 01,02,03'],
]
const TOKEN_LABELS = {
  1: '分P序号', 2: '分部', 3: '标题', 4: '分P标题', 5: '分区', 6: '音质', 7: '画质', 8: '编码',
  9: '发布日', 10: 'avid', 11: 'bvid', 12: 'cid', 13: 'UP主mid', 14: 'UP主名',
  100: '/', 101: '_', 102: '-', 103: '+', 104: ',', 105: '.', 106: 'and', 107: '#',
  108: '(', 109: ')', 110: '[', 111: ']', 112: '{', 113: '}', 114: '空格',
}
const TOKEN_ADDABLE = Object.keys(TOKEN_LABELS).map(Number)

async function loadEngine() {
  try {
    const res = await fetch('/api/settings/engine')
    if (!res.ok) throw new Error(String(res.status))
    eng.value = await res.json()
    engDraft.value = JSON.parse(JSON.stringify(eng.value))
    keywordDraft.value = (eng.value.danmaku.blockedKeywords || []).join('\n')
    uidDraft.value = (eng.value.danmaku.blockedSenderUids || []).join('\n')
  } catch {
    eng.value = null
  }
}

function movePart(index, delta) {
  const parts = engDraft.value.video.fileNameParts
  const target = index + delta
  if (target < 0 || target >= parts.length) return
  const [item] = parts.splice(index, 1)
  parts.splice(target, 0, item)
}

function removePart(index) {
  engDraft.value.video.fileNameParts.splice(index, 1)
}

function addPart(event) {
  const value = Number(event.target.value)
  if (!value) return
  engDraft.value.video.fileNameParts.push(value)
  event.target.value = ''
}

async function saveEngine() {
  if (engBusy.value || !engDraft.value) return
  engBusy.value = true
  engWarnings.value = []
  const payload = JSON.parse(JSON.stringify(engDraft.value))
  payload.danmaku.blockedKeywords = keywordDraft.value.split(/[\n,，]/).map((s) => s.trim()).filter(Boolean)
  payload.danmaku.blockedSenderUids = uidDraft.value
    .split(/[\n,，]/)
    .map((s) => s.trim())
    .filter(Boolean)
    .map(Number)
    .filter((n) => Number.isFinite(n) && n > 0)
  try {
    const res = await fetch('/api/settings/engine', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      eng.value = data.settings
      engDraft.value = JSON.parse(JSON.stringify(data.settings))
      keywordDraft.value = (data.settings.danmaku.blockedKeywords || []).join('\n')
      uidDraft.value = (data.settings.danmaku.blockedSenderUids || []).join('\n')
      engWarnings.value = data.warnings || []
      engNotice.value = { kind: 'ok', text: '设置已落库，引擎即刻消费' }
    } else {
      engNotice.value = { kind: 'err', text: data?.message ?? '保存失败' }
    }
  } catch {
    engNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    engBusy.value = false
  }
}

async function loadLoginState() {
  try {
    loginState.value = await fetch('/api/login/status').then((r) => r.json())
  } catch {
    loginState.value = null
  }
}

async function saveCookie() {
  const cookie = cookieDraft.value.trim()
  if (!cookie || loginBusy.value) return
  loginBusy.value = true
  try {
    const res = await fetch('/api/login/cookie', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cookie }),
    })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      cookieDraft.value = ''
      notify('ok', data?.message ?? '登录态已更新')
    } else {
      notify('err', data?.message ?? '登录失败')
    }
  } catch {
    notify('err', '分站后端没有应答')
  } finally {
    loginBusy.value = false
    loadLoginState()
  }
}

async function logout() {
  if (loginBusy.value) return
  loginBusy.value = true
  try {
    const res = await fetch('/api/login/logout', { method: 'POST' })
    const data = await res.json().catch(() => null)
    notify(res.ok ? 'ok' : 'err', data?.message ?? (res.ok ? '已离站' : '登出失败'))
  } catch {
    notify('err', '分站后端没有应答')
  } finally {
    loginBusy.value = false
    loadLoginState()
  }
}

onMounted(async () => {
  try {
    const [h, s] = await Promise.all([fetch('/api/health').then((r) => r.json()), fetch('/api/settings').then((r) => r.json())])
    health.value = h
    settings.value = s
  } catch {
    // 后端未到站时留空显示
  }
  loadLoginState()
  loadEngine()
})
</script>

<template>
  <div class="min-h-screen bg-adminBg text-white">
    <header class="border-b border-adminLine">
      <div class="mx-auto flex h-14 max-w-[1100px] items-center justify-between px-6">
        <div class="flex items-center gap-3">
          <span class="h-2 w-2 rounded-full bg-adminAccent"></span>
          <span class="text-sm tracking-widest text-white/90">站长室</span>
        </div>
        <RouterLink to="/" class="text-sm text-white/50 transition-colors hover:text-adminAccent">回分站</RouterLink>
      </div>
    </header>

    <main class="mx-auto grid max-w-[1100px] grid-cols-1 gap-4 px-6 py-8 md:grid-cols-2">
      <section class="rounded-lg border border-adminLine bg-adminPanel p-5">
        <h3 class="mb-4 text-xs uppercase tracking-widest text-white/40">分站状态</h3>
        <dl class="space-y-3 font-mono text-sm">
          <div class="flex justify-between">
            <dt class="text-white/50">应答</dt>
            <dd :class="health ? 'text-adminAccent' : 'text-white/30'">{{ health ? 'ok' : '—' }}</dd>
          </div>
          <div class="flex justify-between">
            <dt class="text-white/50">版本</dt>
            <dd>{{ health?.version ?? '—' }}</dd>
          </div>
          <div class="flex justify-between">
            <dt class="text-white/50">服务器时间</dt>
            <dd>{{ health ? new Date(health.serverTimeUtc).toLocaleString('zh-CN') : '—' }}</dd>
          </div>
        </dl>
      </section>

      <section class="rounded-lg border border-adminLine bg-adminPanel p-5">
        <h3 class="mb-4 text-xs uppercase tracking-widest text-white/40">存储</h3>
        <dl class="space-y-3 font-mono text-sm">
          <div class="flex justify-between gap-4">
            <dt class="shrink-0 text-white/50">数据目录</dt>
            <dd class="truncate text-right">{{ settings?.dataDir ?? '—' }}</dd>
          </div>
          <p class="text-xs leading-5 text-white/30">
            视频库、SQLite、cookies、缓存都在这个目录里。容器内请挂载为 /data volume，并保持落在 NAS 本地盘。
          </p>
        </dl>
      </section>

      <section class="rounded-lg border border-adminLine bg-adminPanel p-5">
        <h3 class="mb-4 text-xs uppercase tracking-widest text-white/40">皮肤</h3>
        <div class="flex items-center justify-between">
          <div>
            <p class="text-sm">裸分站</p>
            <p class="mt-1 text-xs text-white/30">自研皮肤开关，打开后整体换脸</p>
          </div>
          <span
            class="flex h-6 w-11 cursor-not-allowed items-center rounded-full bg-white/10 px-1"
            :title="settings?.skin?.note ?? 'M4 到站'"
          >
            <span class="h-4 w-4 rounded-full bg-white/30"></span>
          </span>
        </div>
      </section>

      <section class="rounded-lg border border-adminLine bg-adminPanel p-5">
        <h3 class="mb-4 text-xs uppercase tracking-widest text-white/40">投稿与导入</h3>
        <p class="text-sm leading-6 text-white/40">
          视频链接、收藏夹链接、收藏夹文件都已归到分站顶栏的「投稿」，去那边丢就行。
        </p>
      </section>

      <section class="rounded-lg border border-adminLine bg-adminPanel p-5">
        <h3 class="mb-4 text-xs uppercase tracking-widest text-white/40">站长登录</h3>
        <div class="mb-4 flex items-center gap-2 text-sm">
          <span class="h-2 w-2 rounded-full" :class="loginState?.isLogin ? 'bg-adminAccent' : 'bg-white/20'"></span>
          <span v-if="loginState?.isLogin" class="text-white/85">
            {{ loginState.name }}
            <span v-if="loginState.vipStatus > 0" class="ml-1 text-adminAccent/80">大会员</span>
          </span>
          <span v-else class="text-white/40">未登录（匿名态，画质上限 480P）</span>
        </div>
        <p class="mb-2 text-xs leading-5 text-white/30">
          在浏览器登录 www.bilibili.com 后，从开发者工具的请求头里整行复制 Cookie 粘到这里。cookie 只进不出，不会出现在任何页面回显和日志里。
        </p>
        <textarea
          v-model="cookieDraft"
          rows="3"
          placeholder="SESSDATA=…; bili_jct=…; DedeUserID=…"
          class="w-full resize-none rounded border border-adminLine bg-black/30 px-3 py-2 font-mono text-xs text-white/80 placeholder:text-white/20 focus:border-adminAccent/60 focus:outline-none"
          :disabled="loginBusy"
        ></textarea>
        <div class="mt-3 flex items-center gap-2">
          <button
            class="rounded border border-adminAccent/40 px-3 py-1.5 text-sm text-adminAccent transition-colors hover:bg-adminAccent/10 disabled:opacity-40"
            :disabled="loginBusy || !cookieDraft.trim()"
            @click="saveCookie"
          >
            {{ loginBusy ? '验证中……' : '保存并进站' }}
          </button>
          <button
            v-if="loginState?.hasSavedCookies"
            class="rounded border border-white/15 px-3 py-1.5 text-sm text-white/50 transition-colors hover:border-white/30 hover:text-white/80 disabled:opacity-40"
            :disabled="loginBusy"
            @click="logout"
          >
            登出
          </button>
        </div>
        <p
          v-if="loginNotice"
          class="mt-3 font-mono text-xs"
          :class="loginNotice.kind === 'ok' ? 'text-adminAccent/80' : 'text-rose-300/80'"
        >
          {{ loginNotice.text }}
        </p>
      </section>
      <section v-if="eng && engDraft" class="rounded-lg border border-adminLine bg-adminPanel p-5 md:col-span-2">
        <div class="mb-4 flex items-center justify-between">
          <h3 class="text-xs uppercase tracking-widest text-white/40">下载设置</h3>
          <div class="flex items-center gap-2">
            <button
              class="rounded border border-white/15 px-3 py-1.5 text-sm text-white/50 transition-colors hover:border-white/30 hover:text-white/80 disabled:opacity-40"
              :disabled="engBusy"
              @click="loadEngine"
            >
              重载
            </button>
            <button
              class="rounded border border-adminAccent/40 px-3 py-1.5 text-sm text-adminAccent transition-colors hover:bg-adminAccent/10 disabled:opacity-40"
              :disabled="engBusy"
              @click="saveEngine"
            >
              {{ engBusy ? '保存中……' : '保存设置' }}
            </button>
          </div>
        </div>
        <p class="mb-4 text-xs leading-5 text-white/30">
          这些参数直接写进下载引擎自己的设置：标注「即时」的改动对新下载立即生效，「新提交」的只影响之后投稿的视频，「重启」的要重启分站进程。保存目录、代理、cookie 不在这里：分站锁死 data/videos，不碰代理，cookie 在站长登录卡。
        </p>
        <p
          v-if="engNotice"
          class="mb-3 font-mono text-xs"
          :class="engNotice.kind === 'ok' ? 'text-adminAccent/80' : 'text-rose-300/80'"
        >
          {{ engNotice.text }}
        </p>
        <ul v-if="engWarnings.length" class="mb-3 space-y-1">
          <li v-for="warning in engWarnings" :key="warning" class="font-mono text-xs text-amber-200/80">⚠ {{ warning }}</li>
        </ul>

        <div class="grid grid-cols-1 gap-5 lg:grid-cols-2">
          <!-- 网络 -->
          <div class="rounded border border-white/5 p-4">
            <h4 class="mb-3 text-xs tracking-widest text-white/50">网络 <span class="text-white/25">UA 即时·并发重启</span></h4>
            <label class="mb-1 block text-xs text-white/40">UserAgent（B 站 API 与下载共用；留空回默认）</label>
            <textarea
              v-model="engDraft.network.userAgent"
              rows="2"
              class="mb-3 w-full resize-none rounded border border-adminLine bg-black/30 px-2 py-1.5 font-mono text-xs text-white/80 focus:border-adminAccent/60 focus:outline-none"
            ></textarea>
            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="mb-1 block text-xs text-white/40">同时下载数（重启后生效）</label>
                <input v-model.number="engDraft.network.maxCurrentDownloads" type="number" min="1" max="16" class="eng-input" />
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">每文件线程数 1-32（即时）</label>
                <input v-model.number="engDraft.network.split" type="number" min="1" max="32" class="eng-input" />
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">全局限速 KB/s（-1 不限）</label>
                <input v-model.number="engDraft.network.maxOverallDownloadLimit" type="number" min="-1" class="eng-input" />
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">单任务限速 KB/s（-1 不限）</label>
                <input v-model.number="engDraft.network.maxDownloadLimit" type="number" min="-1" class="eng-input" />
              </div>
            </div>
          </div>

          <!-- 视频 -->
          <div class="rounded border border-white/5 p-4">
            <h4 class="mb-3 text-xs tracking-widest text-white/50">视频 <span class="text-white/25">新提交生效</span></h4>
            <div class="grid grid-cols-3 gap-3">
              <div>
                <label class="mb-1 block text-xs text-white/40">优先编码</label>
                <select v-model.number="engDraft.video.videoCodecs" class="eng-input">
                  <option v-for="[id, name] in CODEC_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">画质</label>
                <select v-model.number="engDraft.video.quality" class="eng-input">
                  <option v-for="[id, name] in QUALITY_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">音质</label>
                <select v-model.number="engDraft.video.audioQuality" class="eng-input">
                  <option v-for="[id, name] in AUDIO_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
            </div>
            <div class="mt-3 grid grid-cols-2 gap-3">
              <div>
                <label class="mb-1 block text-xs text-white/40">ffmpeg 硬解</label>
                <select v-model.number="engDraft.video.ffmpegHardwareAcceleration" class="eng-input">
                  <option v-for="[id, name] in HW_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">ffmpeg 并行 1-4（即时）</label>
                <input v-model.number="engDraft.video.ffmpegMaxParallelJobs" type="number" min="1" max="4" class="eng-input" />
              </div>
            </div>
            <div class="mt-3 flex flex-wrap gap-x-4 gap-y-2 text-xs text-white/60">
              <label class="flex items-center gap-1.5"><input v-model="engDraft.video.content.downloadVideo" type="checkbox" class="accent-adminAccent" />正片</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.video.content.downloadAudio" type="checkbox" class="accent-adminAccent" />音频</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.video.content.downloadDanmaku" type="checkbox" class="accent-adminAccent" />弹幕</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.video.content.downloadSubtitle" type="checkbox" class="accent-adminAccent" />字幕</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.video.content.downloadCover" type="checkbox" class="accent-adminAccent" />封面</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.video.content.generateMovieMetadata" type="checkbox" class="accent-adminAccent" />NFO 元数据</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.video.isTranscodingAacToMp3" type="checkbox" class="accent-adminAccent" />AAC 转 MP3</label>
            </div>
            <div class="mt-3 grid grid-cols-2 gap-3">
              <div>
                <label class="mb-1 block text-xs text-white/40">字幕语言偏好</label>
                <select v-model.number="engDraft.video.content.preferredSubtitleLanguage" class="eng-input">
                  <option v-for="[id, name] in LANG_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">字幕类型偏好</label>
                <select v-model.number="engDraft.video.content.preferredSubtitleType" class="eng-input">
                  <option v-for="[id, name] in TYPE_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
            </div>
          </div>

          <!-- 弹幕 -->
          <div class="rounded border border-white/5 p-4">
            <h4 class="mb-3 text-xs tracking-widest text-white/50">弹幕 <span class="text-white/25">格式新提交·字号行数观看页即时</span></h4>
            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="mb-1 block text-xs text-white/40">输出格式</label>
                <select v-model.number="engDraft.danmaku.outputFormat" class="eng-input">
                  <option v-for="[id, name] in OUTPUT_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">排版算法</label>
                <select v-model.number="engDraft.danmaku.layoutAlgorithm" class="eng-input">
                  <option v-for="[id, name] in LAYOUT_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">字号（观看页同步）</label>
                <input v-model.number="engDraft.danmaku.fontSize" type="number" min="1" max="500" class="eng-input" />
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">限行数 0=不限（观看页同步）</label>
                <input v-model.number="engDraft.danmaku.lineCount" type="number" min="0" class="eng-input" />
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">画布宽</label>
                <input v-model.number="engDraft.danmaku.screenWidth" type="number" min="1" class="eng-input" />
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">画布高</label>
                <input v-model.number="engDraft.danmaku.screenHeight" type="number" min="1" class="eng-input" />
              </div>
            </div>
            <label class="mb-1 mt-3 block text-xs text-white/40">字体名</label>
            <input v-model="engDraft.danmaku.fontName" type="text" class="eng-input" />
            <div class="mt-3 flex flex-wrap gap-x-4 gap-y-2 text-xs text-white/60">
              <label class="flex items-center gap-1.5"><input v-model="engDraft.danmaku.topFilter" type="checkbox" class="accent-adminAccent" />屏蔽顶部</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.danmaku.bottomFilter" type="checkbox" class="accent-adminAccent" />屏蔽底部</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.danmaku.scrollFilter" type="checkbox" class="accent-adminAccent" />屏蔽滚动</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.danmaku.removeEmojiAndSpecialCharacters" type="checkbox" class="accent-adminAccent" />清 Emoji</label>
            </div>
            <div class="mt-3 grid grid-cols-2 gap-3">
              <div>
                <label class="mb-1 block text-xs text-white/40">关键词黑名单（一行一条）</label>
                <textarea v-model="keywordDraft" rows="3" class="eng-input resize-none"></textarea>
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">UID 黑名单（一行一个）</label>
                <textarea v-model="uidDraft" rows="3" class="eng-input resize-none"></textarea>
              </div>
            </div>
          </div>

          <!-- 命名 -->
          <div class="rounded border border-white/5 p-4">
            <h4 class="mb-3 text-xs tracking-widest text-white/50">命名 <span class="text-white/25">新提交生效</span></h4>
            <p class="mb-2 text-xs text-white/40">文件命名字段（从上到下拼接；没带序号/cid/bvid 时分站自动补 cid 防多 P 互撞）</p>
            <ul class="mb-2 space-y-1">
              <li v-for="(part, index) in engDraft.video.fileNameParts" :key="`${part}-${index}`" class="flex items-center gap-2 text-xs">
                <span class="w-5 text-right text-white/25">{{ index + 1 }}</span>
                <span class="flex-1 rounded bg-black/30 px-2 py-1 font-mono text-white/70">{{ TOKEN_LABELS[part] ?? part }}</span>
                <button class="text-white/30 transition hover:text-white" :disabled="index === 0" @click="movePart(index, -1)">↑</button>
                <button class="text-white/30 transition hover:text-white" :disabled="index === engDraft.video.fileNameParts.length - 1" @click="movePart(index, 1)">↓</button>
                <button class="text-rose-300/60 transition hover:text-rose-300" @click="removePart(index)">✕</button>
              </li>
            </ul>
            <select class="eng-input mb-3" value="" @change="addPart">
              <option value="">＋ 添加字段……</option>
              <option v-for="token in TOKEN_ADDABLE" :key="token" :value="token">{{ TOKEN_LABELS[token] }}</option>
            </select>
            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="mb-1 block text-xs text-white/40">日期格式</label>
                <input v-model="engDraft.video.fileNamePartTimeFormat" type="text" class="eng-input" />
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">序号样式</label>
                <select v-model.number="engDraft.video.orderFormat" class="eng-input">
                  <option v-for="[id, name] in ORDER_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
            </div>
            <label class="mt-3 flex items-center gap-1.5 text-xs text-white/60">
              <input v-model="engDraft.basic.repeatFileAutoAddNumberSuffix" type="checkbox" class="accent-adminAccent" />
              撞名时自动加 (1) 后缀
            </label>
          </div>
        </div>
      </section>
    </main>
  </div>
</template>
