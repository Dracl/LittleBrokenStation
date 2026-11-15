<script setup>
import { computed, onUnmounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import VisibilityPill from './VisibilityPill.vue'
import { HINT_DELETE_VIDEO } from '../confirmStore'
import { postVideoAction, refetchArchive, removeVideoEntry } from '../utils/videoActions.js'

// ─── 稿件设置窗：可见性 / 标题·简介·分区的站内覆写 / 档案动作（站长再多三枚）。
// 骨架照抄 CollectionSettingsDialog（它自己照 MemberPage 的「账号与登录设置」弹层）：fixed 遮罩 +
// role=dialog + 点遮罩关 + 内容限高内滚 + sticky 页眉，站内那套先例没搬 Teleport，这里也不搬。
// 段与段之间用站内那个 section 卡片语言（rounded-2xl border border-lineSoft bg-cardBg p-6）。
// 夜里 --card-bg == --page-bg：层次全靠那道 border-lineSoft，不靠底色拉。
//
// 落库走 PATCH /api/videos/{id}：只递真改过的栏，还原那一栏递 null；后端只回 { ok: true }，
// 不含新值 —— 所以存完 emit('saved') 让父页重拉 /api/video/{bvid}，前端不自改本地对象，免得两处真相。
// 「覆写中」显不显一律看后端下发的 titleOverridden / descOverridden / zoneOverridden，前端不自己比原值。
const props = defineProps({
  // watch 那坨载荷（含 id / bvid / visibility / title / description / zone /
  // canManage / isStationOwner / *Overridden）。显隐由父页的 v-if 管：只在开着的时候存在。
  // 简介这一栏全站只叫 description（与 video 载荷、放映厅正文同一格，不留第二个名字）；
  // 只有那面「覆写过」的旗子叫 descOverridden —— 它说的是那件事，不是那格字段。
  data: { type: Object, required: true },
})
const emit = defineEmits(['saved', 'close'])

const router = useRouter()

const VISIBILITIES = [
  { key: 'public', label: '公开' },
  { key: 'private', label: '私有' },
]
// 档案动作：与放映厅互动栏那两枚走的是 utils/videoActions.js 里同一份逻辑，这里只是第二个入口。
// media 这一枚按站长批的摆在投稿人也看得见这一段（不进 OWNER_ACTIONS）：封面件与档案回 B 站重采。
// 每枚两句话：hint 说这一下去 B 站干什么，note 只补一句「站内那部分动还是不动」这类跑完才会发生的事。
// 摆序按「最常按的那枚在上」：评论补采是日常活，弹幕留档重拉与封面回炉都属偶发。
const ARCHIVE_ACTIONS = [
  {
    key: 'comments',
    idle: '补采评论',
    busy: '评论采集中…',
    done: '已补采',
    hint: '回 B 站把评论快照再采一遍',
    note: '站内已有的评论原样留着',
  },
  {
    key: 'danmaku',
    idle: '重拉弹幕快照',
    busy: '弹幕重拉中…',
    done: '已重拉',
    hint: '回 B 站把这份弹幕留档重拉一遍',
    note: '分站朋友发的弹幕',
  },
  {
    key: 'subtitles',
    idle: '重拉字幕存档',
    busy: '字幕重拉中…',
    done: '已重拉',
    hint: '回 B 站把这份稿件的字幕轨全拉一遍',
    note: '缺的轨补齐，盘上旧轨同步刷新',
  },
  {
    key: 'media',
    idle: '重拉封面与媒体',
    busy: '拉取中…',
    done: '已重拉',
    hint: '回 B 站重采封面件与稿件档案',
    note: '缺件的媒体顺带回炉重下',
  },
]
// 站长那一组：会员根本看不见这一段（真门在端点里，这里只是不摆钮）
const OWNER_ACTIONS = [
  {
    key: 'requeue',
    idle: '重新排队',
    busy: '排队中…',
    done: '已排队',
    hint: '把被取消的馆藏放回下载队列',
    note: '下一班进站，已有的件不重下',
  },
  {
    key: 'revive',
    idle: '强制重下',
    busy: '重下中…',
    done: '已重下',
    hint: '救活卡死的下载',
    note: '能软续就软续，实在打不动才删行重投',
  },
]

const title = ref('')
const description = ref('')
const zone = ref('')
const visibility = ref('public')
const busy = ref(false) // PATCH 在飞
const removing = ref(false) // 删档在飞
const notice = ref(null) // 面板回执 { kind: 'ok' | 'warn' | 'err', text }
const act = ref({}) // 各枚动作的三态：key → { state: 'idle' | 'busy' | 'done', text }
// 各枚动作钮互斥：任何一枚在飞，其余都按不动（同一部片子连发两发补采没意义，还可能互相踩）
const anyActBusy = computed(() => Object.values(act.value).some((a) => a?.state === 'busy'))

function normalizeVisibility(v) {
  return v === 'private' ? 'private' : 'public' // 缺字段 / 脏值一律当公开（与后端 FromColumn 同一克制）
}

// 三行输入的名字就是 PATCH 的栏名（全站只用 description 这个名字，不新增 desc）。
const FIELDS = { title, description, zone }

// 底稿：上一轮与后端对齐的那一茬（有效值 + 档位）。payload 拿它比「有没有真改动」，
// 重拉落地时拿它认「哪一栏用户还没动过」—— 动过的不冲，没动过的跟着服务端归位。
const base = ref(snapshot(props.data))

function snapshot(d) {
  return {
    title: d?.title ?? '',
    description: d?.description ?? '',
    zone: d?.zone ?? '',
    visibility: normalizeVisibility(d?.visibility),
  }
}

function fillFrom(d) {
  const s = snapshot(d)
  base.value = s
  title.value = s.title
  description.value = s.description
  zone.value = s.zone
  visibility.value = s.visibility
  notice.value = null
}

// 只换「用户没动过」的那几栏：存完重拉、档案动作跑完都走这里，
// 正敲着的那几行不该被一趟重拉当成水冲走（编码加档那发轮询每 5 秒也在换 props.data）。
function refillUnchanged(d) {
  const s = snapshot(d)
  const b = base.value
  if (title.value === b.title) title.value = s.title
  if (description.value === b.description) description.value = s.description
  if (zone.value === b.zone) zone.value = s.zone
  if (visibility.value === b.visibility) visibility.value = s.visibility
  base.value = s
}

// 挂载时按 props 铺一次底稿与三行输入（未覆写时输入框里摆的就是后端给的有效值）。
fillFrom(props.data)

// 之后再换血只认我们自己请父页重拉那一趟（pendingRefill）。
// 一路跟着换的话，放映厅那个编码轮询每 5 秒就会把正敲的字冲掉一次。
let pendingRefill = false
watch(
  () => props.data,
  (d) => {
    if (!pendingRefill) return
    pendingRefill = false
    refillUnchanged(d)
  },
)

function close() {
  emit('close')
}

function onKeydown(event) {
  if (event.key === 'Escape') close()
}
window.addEventListener('keydown', onKeydown)
onUnmounted(() => window.removeEventListener('keydown', onKeydown))

const sub = computed(() => [props.data?.bvid, props.data?.upName].filter(Boolean).join(' · '))

// 只递真改动的栏：与底稿比，没改的不进 body，免得把后端没打算动的列覆盖回去。
// 清空 = 还原：placeholder 已经明说「留空 = 用 B 站原值」，所以空串递的是 null 而不是「」。
function diffOf(key) {
  const current = String(base.value[key] ?? '')
  const next = String(FIELDS[key].value ?? '').trim()
  if (next === current) return undefined
  return next === '' ? null : next
}

function payload() {
  const body = {}
  for (const key of Object.keys(FIELDS)) {
    const v = diffOf(key)
    if (v !== undefined) body[key] = v
  }
  if (visibility.value !== base.value.visibility) body.visibility = visibility.value
  return body
}

// 后端这几档各回一句人话；认不出的档才落到兜底句上。
function settle({ status, data }) {
  if (status === 400) return { kind: 'err', text: data?.message ?? '这一栏分站收不下，改改再存' }
  if (status === 401) return { kind: 'err', text: '先登录，再回来改稿件' }
  if (status === 403) return { kind: 'err', text: data?.message ?? '这篇稿子不归你改' }
  if (status === 404) return { kind: 'err', text: data?.message ?? '馆藏里没有这一部' }
  return { kind: 'err', text: data?.message ?? '分站收不下这次改动，稍后再试' }
}

// 存好了请父页重拉 /api/video：面板不自己改本地对象，改完的标题与那几面覆写标一律以服务端为准。
function tellSaved(detail) {
  pendingRefill = true
  emit('saved', detail ?? null)
}

async function save() {
  if (busy.value || removing.value) return
  const body = payload()
  if (!Object.keys(body).length) {
    notice.value = { kind: 'warn', text: '没改什么，先动一动再存' }
    return
  }
  busy.value = true
  notice.value = null
  try {
    const res = await fetch(`/api/videos/${props.data.id}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      notice.value = { kind: 'ok', text: data?.message ?? '改好了' }
      tellSaved(body)
      return
    }
    notice.value = settle({ status: res.status, data })
  } catch {
    notice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    busy.value = false
  }
}

function setAct(key, state, text) {
  act.value = { ...act.value, [key]: { state, text: text ?? act.value[key]?.text ?? '' } }
}
function labelOf(item) {
  const a = act.value[item.key]
  if (a?.state === 'busy') return item.busy
  if (a?.state === 'done') return item.done
  return item.idle
}
function actText(item) {
  return act.value[item.key]?.text || item.hint
}

// 两组的行同款（钮同宽、同一套脸），类串只在这一处写，免得两组越改越不像。
// 固定宽度是为了三颗钮的右缘对齐成一条线，右边那两行说明也跟着对齐，不再长长短短。
const ACT_BTN_BASE =
  'w-[124px] shrink-0 rounded-lg border px-3 py-1.5 text-center text-[12px] transition disabled:cursor-not-allowed disabled:opacity-40'
function actBtnClass(item) {
  return act.value[item.key]?.state === 'done'
    ? `${ACT_BTN_BASE} border-stationPink/40 bg-stationPink/5 text-stationPink`
    : `${ACT_BTN_BASE} border-lineSoft text-inkSub hover:border-stationPink/50 hover:text-stationPink`
}

// 弹幕 / 评论 / 封面与媒体各走 refetchArchive 那一份逻辑，只是每颗钮只点自己那一样（放映厅那枚是两个一起点）。
// 跑成任何一发都 tellSaved：父页重拉 /api/video，封面、那几栏与覆写标一律跟着服务端归位
// （/media 的图后端已带 no-cache，img 会自己复核，这里不另加 cache-buster）。
async function runArchive(item) {
  if (busy.value || removing.value || anyActBusy.value) return
  setAct(item.key, 'busy')
  const res = await refetchArchive(props.data.id, {
    comments: item.key === 'comments',
    danmaku: item.key === 'danmaku',
    subtitles: item.key === 'subtitles',
    media: item.key === 'media',
  })
  setAct(item.key, res.allOk ? 'done' : 'idle', res.text)
  if (res.ran && res.kind !== 'err') tellSaved(res) // 页面侧的收尾（重挂评论区、顶弹幕重取、重拉 /api/video）归父页
}

async function runOwner(item) {
  if (busy.value || removing.value || anyActBusy.value) return
  setAct(item.key, 'busy')
  const res = await postVideoAction(props.data.id, item.key)
  setAct(item.key, res.ok ? 'done' : 'idle', res.message)
  if (res.ok) tellSaved(null) // 状态变了（排队 / 重下），让父页把 watch 那份实况取回来
}

async function doDelete() {
  if (removing.value || busy.value) return
  removing.value = true
  const res = await removeVideoEntry(props.data.id, props.data?.title || props.data?.bvid)
  if (res.outcome === 'removed') {
    // 这部馆藏已经不存在了，把用户送回大厅，别留在空页面上（与放映厅那枚删档钮同一去处）
    router.push('/')
    return
  }
  removing.value = false
  if (res.outcome === 'failed') notice.value = { kind: 'err', text: res.text }
}

// 「覆写中」这枚是状态标不是可见性标（可见性一律用现成的 VisibilityPill，不再画第二枚私有标）：
// stationBlue 是硬编码色、日夜同一值，青调细描边把轮廓收住，不靠底色拉层次。
const OVERRIDE_CHIP = 'rounded-full border border-stationBlue/30 px-1.5 py-px text-[10px] font-medium text-stationBlue'
</script>

<template>
  <div
    class="fixed inset-0 z-50 flex items-center justify-center bg-black/45 p-4"
    role="dialog"
    aria-modal="true"
    aria-label="稿件设置"
    @click.self="close"
  >
    <div class="no-scrollbar max-h-[85vh] w-full max-w-[560px] overflow-y-auto rounded-2xl border border-lineSoft bg-cardBg shadow-2xl">
      <div class="sticky top-0 z-10 flex items-center justify-between gap-3 border-b border-lineSoft/70 bg-cardBg px-6 py-4">
        <div class="min-w-0">
          <h3 class="text-sm font-semibold text-inkMain">稿件设置</h3>
          <p v-if="sub" class="mt-0.5 truncate font-mono text-[11px] text-inkFaint" :title="sub">{{ sub }}</p>
        </div>
        <button
          class="grid h-7 w-7 shrink-0 place-items-center rounded-full border border-lineSoft text-inkSub transition hover:border-stationPink/60 hover:text-stationPink"
          aria-label="收起设置"
          @click="close"
        >
          ✕
        </button>
      </div>

      <div class="px-6 pb-6">
        <!-- ① 可见性：分段钮形状照投稿窗那一排（墙上「喂料三档」一族），白卡上直接摆，日夜同一套颜色 -->
        <section class="mt-4 rounded-2xl border border-lineSoft bg-cardBg p-6 shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
          <div class="flex items-center gap-2">
            <h4 class="text-[13px] font-medium text-inkMain">可见性</h4>
            <!-- 站内已存的那一档（后端下发）；改动要等存好重拉才落到这枚标上，图的就是「以服务端为准」 -->
            <VisibilityPill :visibility="data.visibility" />
          </div>
          <div class="mt-3 flex flex-wrap items-center gap-2">
            <div class="flex items-center rounded-full border border-lineSoft p-0.5" role="group" aria-label="这部片子谁能看见">
              <button
                v-for="v in VISIBILITIES"
                :key="v.key"
                type="button"
                class="rounded-full px-2.5 py-0.5 text-[11.5px] transition"
                :class="visibility === v.key ? 'bg-stationPink text-white' : 'text-inkSub hover:text-stationPink'"
                :aria-pressed="visibility === v.key"
                :disabled="busy"
                @click="visibility = v.key"
              >
                {{ v.label }}
              </button>
            </div>
            <span v-if="visibility === 'private'" class="text-[11.5px] text-inkFaint">
              只有你自己和站长看得着，大厅、搜索与分区里都不出现
            </span>
          </div>
        </section>

        <!-- ② 稿件信息：三行覆写位。留空 = 还原成 B 站原值（存的时候那一栏递 null） -->
        <section class="mt-4 rounded-2xl border border-lineSoft bg-cardBg p-6 shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
          <h4 class="text-[13px] font-medium text-inkMain">稿件信息</h4>
          <p class="mt-1 text-[11px] leading-5 text-inkFaint">
            站内改的字只归分站，B 站那边原样不动；点「还原」那一栏就交回 B 站原值。
          </p>

          <div class="mt-4">
            <div class="flex items-center justify-between gap-2">
              <span class="text-[11px] text-inkFaint">标题</span>
              <div class="flex items-center gap-1.5">
                <span v-if="data.titleOverridden" :class="OVERRIDE_CHIP">覆写中</span>
                <button
                  type="button"
                  class="rounded-md border border-lineSoft px-1.5 py-px text-[10.5px] text-inkSub transition hover:border-stationPink/50 hover:text-stationPink disabled:cursor-not-allowed disabled:opacity-40"
                  :disabled="!data.titleOverridden || busy || removing"
                  :title="data.titleOverridden ? '这一栏交回 B 站原值' : '没在站内改过，本来就是 B 站原值'"
                  @click="title = ''"
                >
                  还原
                </button>
              </div>
            </div>
            <input
              v-model="title"
              type="text"
              placeholder="留空 = 用 B 站原值"
              class="mt-1 w-full rounded-xl border border-lineSoft bg-[#f6f7f8] px-4 py-2.5 text-sm text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:outline-none focus:ring-2 focus:ring-stationPink/20 dark:bg-[#23262e]"
              :disabled="busy || removing"
            />
          </div>

          <div class="mt-4">
            <div class="flex items-center justify-between gap-2">
              <span class="text-[11px] text-inkFaint">简介</span>
              <div class="flex items-center gap-1.5">
                <span v-if="data.descOverridden" :class="OVERRIDE_CHIP">覆写中</span>
                <button
                  type="button"
                  class="rounded-md border border-lineSoft px-1.5 py-px text-[10.5px] text-inkSub transition hover:border-stationPink/50 hover:text-stationPink disabled:cursor-not-allowed disabled:opacity-40"
                  :disabled="!data.descOverridden || busy || removing"
                  :title="data.descOverridden ? '这一栏交回 B 站原值' : '没在站内改过，本来就是 B 站原值'"
                  @click="description = ''"
                >
                  还原
                </button>
              </div>
            </div>
            <textarea
              v-model="description"
              rows="3"
              placeholder="留空 = 用 B 站原值"
              class="no-scrollbar mt-1 w-full resize-y rounded-xl border border-lineSoft bg-[#f6f7f8] px-4 py-2.5 text-sm leading-6 text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:outline-none focus:ring-2 focus:ring-stationPink/20 dark:bg-[#23262e]"
              :disabled="busy || removing"
            ></textarea>
          </div>

          <div class="mt-4">
            <div class="flex items-center justify-between gap-2">
              <span class="text-[11px] text-inkFaint">分区</span>
              <div class="flex items-center gap-1.5">
                <span v-if="data.zoneOverridden" :class="OVERRIDE_CHIP">覆写中</span>
                <button
                  type="button"
                  class="rounded-md border border-lineSoft px-1.5 py-px text-[10.5px] text-inkSub transition hover:border-stationPink/50 hover:text-stationPink disabled:cursor-not-allowed disabled:opacity-40"
                  :disabled="!data.zoneOverridden || busy || removing"
                  :title="data.zoneOverridden ? '这一栏交回 B 站原值' : '没在站内改过，本来就是 B 站原值'"
                  @click="zone = ''"
                >
                  还原
                </button>
              </div>
            </div>
            <input
              v-model="zone"
              type="text"
              placeholder="留空 = 用 B 站原值"
              class="mt-1 w-full rounded-xl border border-lineSoft bg-[#f6f7f8] px-4 py-2.5 text-sm text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:outline-none focus:ring-2 focus:ring-stationPink/20 dark:bg-[#23262e]"
              :disabled="busy || removing"
            />
          </div>
        </section>

        <!-- ③ 档案动作：与放映厅互动栏那两枚同源（utils/videoActions.js），成功后各回一句人话 -->
        <section class="mt-4 rounded-2xl border border-lineSoft bg-cardBg p-6 shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
          <h4 class="text-[13px] font-medium text-inkMain">档案动作</h4>
          <div class="mt-2">
            <div
              v-for="item in ARCHIVE_ACTIONS"
              :key="item.key"
              class="flex items-start gap-3 border-b border-lineSoft/50 py-2.5 last:border-b-0 last:pb-0"
            >
              <button
                type="button"
                :class="actBtnClass(item)"
                :disabled="busy || removing || anyActBusy"
                @click="runArchive(item)"
              >
                {{ labelOf(item) }}
              </button>
              <div class="min-w-0 flex-1">
                <p class="text-[12px] leading-5 text-inkSub">{{ actText(item) }}</p>
                <p v-if="item.note" class="mt-0.5 text-[11px] leading-4 text-inkFaint">{{ item.note }}</p>
              </div>
            </div>
          </div>

          <!-- 站长那一组：会员看不见（真门在端点里，这里只是不摆钮）。加档不重做清晰度 UI，交给站长室 -->
          <div v-if="data.isStationOwner" class="mt-5 border-t border-lineSoft/70 pt-4">
            <div class="flex items-center gap-2">
              <h5 class="text-[11px] uppercase tracking-widest text-inkFaint">站长专用</h5>
              <span class="text-[11px] text-inkFaint">这几枚只有站长按得动</span>
            </div>
            <div class="mt-2.5">
              <div
                v-for="item in OWNER_ACTIONS"
                :key="item.key"
                class="flex items-start gap-3 border-b border-lineSoft/50 py-2.5"
              >
                <button
                  type="button"
                  :class="actBtnClass(item)"
                  :disabled="busy || removing || anyActBusy"
                  @click="runOwner(item)"
                >
                  {{ labelOf(item) }}
                </button>
                <div class="min-w-0 flex-1">
                  <p class="text-[12px] leading-5 text-inkSub">{{ actText(item) }}</p>
                  <p v-if="item.note" class="mt-0.5 text-[11px] leading-4 text-inkFaint">{{ item.note }}</p>
                </div>
              </div>
              <div class="flex items-start gap-3 py-2.5">
                <RouterLink
                  :to="{ path: `/video/${props.data?.bvid}` }"
                  class="w-[124px] shrink-0 rounded-lg border border-lineSoft px-3 py-1.5 text-center text-[12px] text-inkSub transition hover:border-stationPink/50 hover:text-stationPink"
                  title="加档点单在放映厅的播放器里（版本行折叠那排），这里不重做一份"
                >
                  去播放器加档
                </RouterLink>
                <div class="min-w-0 flex-1">
                  <p class="text-[12px] leading-5 text-inkSub">挑哪几档清晰度，到这部片子的播放器里点「加档其他版本」</p>
                  <p class="mt-0.5 text-[11px] leading-4 text-inkFaint">这一窗不重做一份清晰度 UI（进货口只管队列与取消）</p>
                </div>
              </div>
            </div>
          </div>

          <!-- 危险区那一行：站内既有的 destructive 配色（合集详情页「拆除抽屉」同一套），不自己调色 -->
          <div class="mt-5 flex flex-wrap items-center gap-2.5 border-t border-lineSoft/70 pt-4">
            <button
              type="button"
              class="shrink-0 rounded-lg border border-rose-300 px-3 py-1.5 text-[12px] text-rose-500 transition hover:bg-rose-50 disabled:cursor-not-allowed disabled:opacity-40 dark:border-rose-500/40 dark:text-rose-400 dark:hover:bg-rose-500/10"
              :disabled="removing || busy"
              @click="doDelete"
            >
              {{ removing ? '删掉中…' : '删掉这一部' }}
            </button>
            <span class="min-w-0 flex-1 text-[11px] leading-5 text-inkFaint">{{ HINT_DELETE_VIDEO }}</span>
          </div>
        </section>

        <p
          v-if="notice"
          class="mt-3 text-xs leading-5"
          :class="{
            'text-emerald-600': notice.kind === 'ok',
            'text-amber-600': notice.kind === 'warn',
            'text-rose-500': notice.kind === 'err',
          }"
        >
          {{ notice.text }}
        </p>

        <div class="mt-5 flex items-center justify-end gap-2 border-t border-lineSoft/70 pt-4">
          <button class="rounded-lg px-4 py-2 text-sm text-inkSub transition hover:bg-black/5 hover:text-inkMain" :disabled="busy" @click="close">
            算了
          </button>
          <button
            class="rounded-lg bg-stationPink px-5 py-2 text-sm font-medium text-white transition hover:opacity-90 disabled:opacity-50"
            :disabled="busy || removing"
            @click="save"
          >
            {{ busy ? '存好中…' : '存好' }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
