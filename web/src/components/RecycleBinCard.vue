<script setup>
// 站长室「回收站」卡：删档后的馆藏先落这里，恢复回架或连本地文件一起销账。
// 数据源 GET /api/admin/recycle（按 deletedAtUtc 倒序）；三键 = 单件恢复 / 单件彻底删 / 一键清空，
// 两颗破坏键走全站那张自制确认窗（confirmStore），不再顶浏览器原生外壳；通知沿用站长室各卡自持 { kind, text } 那套（AdminPage 同款），不另起样式。
// 措辞口径向 askDeleteVideo 看齐：只有这一卡上的才是真没了，得说透；往回收站里抖的那类（抖进回收站可捞）不拉玫红。
import { computed, onMounted, ref } from 'vue'
import { requestConfirm } from '../confirmStore'
import { friendlyTime } from '../utils/time'

const items = ref(null) // null = 没拉到（加载中或后端没应答），[] = 确实空仓
const loading = ref(false)
const denied = ref(false) // 401/403：身份不对，别把拒客报成空仓
const notice = ref(null) // { kind: 'ok' | 'err', text }
const busyId = ref(null) // 正在办事的单件 id
const clearing = ref(false)
const coverGone = ref([]) // 本机拉挂了的封面 id，退化成首字母占位

const totalBytes = computed(() => (items.value ?? []).reduce((sum, it) => sum + (it.bytes ?? 0), 0))
// 任何一颗钮在飞就把整卡的动作键全按死，免得连点重复提交。
const busy = computed(() => busyId.value !== null || clearing.value)

async function load() {
  if (loading.value) return
  loading.value = true
  try {
    const res = await fetch('/api/admin/recycle')
    if (res.status === 401 || res.status === 403) {
      denied.value = true
      items.value = null
      return
    }
    const data = await res.json().catch(() => null)
    items.value = Array.isArray(data) ? data : []
    denied.value = false
    coverGone.value = []
  } catch {
    /* 后端打盹：不算空仓，交给卡内的重试条说话 */
    items.value = null
    notice.value = { kind: 'err', text: '分站后端没有应答，回收站暂时打不开' }
  } finally {
    loading.value = false
  }
}

// 封面照 AvatarImg 的降级思路：拉不到就退化首字母。
// 取数只能直出：coverUrl 是本站的 /api/admin/recycle/{id}/cover 端点（从盘上捐被删档的原图），
// 不是稿件对象：没 localCover 可退、没 coverThumbs 可认（档位件随档同销，回收站里无档可切），
// 所以全站收口的 utils/coverSrc 在这用不上；也不能贴 B 站缩放参数——那 URL 同源（旧的 coverThumb 在此从来是空转：hdslb 域判定拦下原样返回）。
function coverOf(item) {
  if (coverGone.value.includes(item.id)) return ''
  return item.coverUrl || ''
}

function dropCover(id) {
  if (!coverGone.value.includes(id)) coverGone.value = [...coverGone.value, id]
}

function initialOf(item) {
  const name = item.title || item.upName || item.bvid || ''
  return name ? Array.from(String(name))[0] : '?'
}

// 体积/计数口径与站长室其它卡一致，照 AdminPage 的实现写一份本地版（那两个函数是页面内的，没导出）。
function fmtBytes(b) {
  if (b === null || b === undefined) return '—'
  if (b === 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let v = b
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  return `${v >= 100 || i === 0 ? Math.round(v) : v.toFixed(1)} ${units[i]}`
}

function fmtCount(n) {
  if (n === null || n === undefined) return '—'
  if (n >= 100000000) return (n / 100000000).toFixed(1).replace(/\.0$/, '') + '亿'
  if (n >= 10000) return (n / 10000).toFixed(1).replace(/\.0$/, '') + '万'
  return String(n)
}

async function restore(item) {
  if (busy.value) return
  busyId.value = item.id
  notice.value = null
  let ok = false
  try {
    const res = await fetch(`/api/admin/recycle/${item.id}/restore`, { method: 'POST' })
    const data = await res.json().catch(() => null)
    ok = res.ok
    notice.value = { kind: res.ok ? 'ok' : 'err', text: data?.message ?? (res.ok ? '已恢复，这件重回馆藏' : '恢复没成，这件还躺在回收站里') }
  } catch {
    notice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    busyId.value = null
    if (ok) await load()
  }
}

async function destroy(item) {
  if (busy.value) return
  const label = item.title || item.bvid
  if (
    !(
      await requestConfirm({
        title: '彻底销毁这一件',
        sub: `${label} · ${item.bvid}`,
        message: '连本地文件（视频、封面、弹幕、评论存档）一起清掉，回收站里的清单也一并抹掉。这一步没有回头路，之后一件都捞不回来。',
        confirmText: '彻底销毁',
        cancelText: '再留留',
        danger: true,
        icon: 'trash',
      })
    )
  ) {
    return
  }
  busyId.value = item.id
  notice.value = null
  let ok = false
  try {
    const res = await fetch(`/api/admin/recycle/${item.id}`, { method: 'DELETE' })
    const data = await res.json().catch(() => null)
    ok = res.ok
    notice.value = { kind: res.ok ? 'ok' : 'err', text: data?.message ?? (res.ok ? '已连本地文件一并销掉' : '彻底删除没成，件还在') }
  } catch {
    notice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    busyId.value = null
    if (ok) await load()
  }
}

async function clearAll() {
  if (busy.value || !items.value?.length) return
  if (
    !(
      await requestConfirm({
        title: '清空回收站',
        sub: `${items.value.length} 件 · ${fmtBytes(totalBytes.value)}`,
        message: '连本地文件一起清掉，回收站里的清单也一并抹掉。这一步没有回头路，之后一件都捞不回来。',
        confirmText: '全部销毁',
        cancelText: '再留留',
        danger: true,
        icon: 'trash',
      })
    )
  ) {
    return
  }
  clearing.value = true
  notice.value = null
  let ok = false
  try {
    const res = await fetch('/api/admin/recycle', { method: 'DELETE' })
    const data = await res.json().catch(() => null)
    ok = res.ok
    const n = data?.cleared ?? items.value.length
    notice.value = { kind: res.ok ? 'ok' : 'err', text: data?.message ?? (res.ok ? `已清空 ${n} 件，回收站空了` : '清空没成，件都还在') }
  } catch {
    notice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    clearing.value = false
    if (ok) await load()
  }
}

onMounted(load)

defineExpose({ load })
</script>

<template>
  <section class="lbs-card">
    <div class="mb-4 flex items-center justify-between gap-2">
      <h3 class="lbs-title !mb-0">
        回收站
        <span class="ml-1 rounded-full border border-rose-400/30 px-2 py-0.5 text-[10px] normal-case tracking-normal text-rose-300/70">已删馆藏 · 暂存待捞</span>
      </h3>
      <div class="flex shrink-0 items-center gap-2">
        <p v-if="items?.length" class="font-mono text-[11px] text-white/30">
          {{ items.length }} 件 · {{ fmtBytes(totalBytes) }}
        </p>
        <button class="lbs-btn-ghost !px-3 !py-1 !text-[11px]" :disabled="loading || busy" @click="load">
          重新翻一遍
        </button>
        <button
          class="lbs-btn-danger !px-3 !py-1 !text-[11px]"
          :disabled="!items?.length || loading || busy"
          @click="clearAll"
        >
          {{ clearing ? '清空中……' : '清空回收站' }}
        </button>
      </div>
    </div>

    <p class="mb-3 text-xs leading-5 text-white/30">
      馆藏被删后先进这里暂存：视频、封面、弹幕、评论都还在盘上，恢复回来就复架。彻底删除与清空都连本地文件一起清掉，出去的东西回不来。
    </p>
    <p v-if="notice" class="mb-3 font-mono text-xs" :class="notice.kind === 'ok' ? 'text-adminAccent/80' : 'text-rose-300/80'">
      {{ notice.text }}
    </p>

    <p v-if="denied" class="py-4 text-xs text-white/30">回收站只对站长开放——先把身份核对了再来翻这口柜。</p>
    <div v-else-if="items === null" class="flex items-center gap-3 py-4">
      <p class="text-xs text-white/30">{{ loading ? '正在翻回收站……' : '回收站没打开：分站后端没有应答' }}</p>
      <button v-if="!loading" class="lbs-btn-ghost !px-3 !py-1 !text-[11px]" @click="load">再试一次</button>
    </div>
    <p v-else-if="!items.length" class="py-4 text-xs text-white/30">回收站空着——删掉的馆藏会先搁在这儿，反悔了随时恢复去。</p>

    <ul v-else class="max-h-[420px] space-y-2 overflow-y-auto pr-1">
      <li
        v-for="item in items"
        :key="item.id"
        class="flex items-start gap-3 rounded-xl border border-white/[0.06] bg-black/25 p-3"
      >
        <div class="relative aspect-[16/10] w-[104px] shrink-0 overflow-hidden rounded-lg bg-black/40">
          <img
            v-if="coverOf(item)"
            :src="coverOf(item)"
            :alt="item.title || item.bvid"
            loading="lazy"
            decoding="async"
            referrerpolicy="no-referrer"
            class="h-full w-full object-cover"
            @error="dropCover(item.id)"
          />
          <span
            v-else
            class="flex h-full w-full select-none items-center justify-center font-mono text-lg text-stationPink/45"
            aria-hidden="true"
          >
            {{ initialOf(item) }}
          </span>
        </div>

        <div class="min-w-0 flex-1">
          <p class="truncate text-sm text-white/85" :title="item.title || item.bvid">{{ item.title || item.bvid }}</p>
          <p class="mt-1 flex flex-wrap items-center gap-x-2 gap-y-0.5 font-mono text-[11px] text-white/30">
            <span class="text-adminAccent/70">{{ item.bvid }}</span>
            <span class="max-w-[180px] truncate">{{ item.upName || 'UP 主不详' }}</span>
            <span>删于 {{ friendlyTime(item.deletedAtUtc) }}</span>
            <span>动手的：{{ item.deletedBy || '未记录' }}</span>
          </p>
          <p class="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-0.5 font-mono text-[11px] text-white/45">
            <span>{{ fmtCount(item.fileCount) }} 个文件</span>
            <span>{{ fmtBytes(item.bytes) }}</span>
            <span>{{ fmtCount(item.pageCount) }} P</span>
            <span>弹幕 {{ fmtCount(item.danmakuCount) }}</span>
            <span>评论 {{ fmtCount(item.commentCount) }}</span>
          </p>
        </div>

        <div class="flex shrink-0 flex-col items-stretch gap-2 self-center">
          <button
            class="rounded-lg border border-stationBlue/40 bg-stationBlue/[0.07] px-3 py-1 text-xs text-stationBlue transition-all hover:bg-stationBlue/[0.14] disabled:cursor-not-allowed disabled:opacity-40"
            :disabled="busy"
            :title="`恢复馆藏（${item.bvid}）：重新架上，本地文件不动`"
            @click="restore(item)"
          >
            {{ busyId === item.id ? '恢复中…' : '恢复' }}
          </button>
          <button
            class="lbs-btn-danger !px-3 !py-1 !text-xs"
            :disabled="busy"
            title="连本地文件一起清掉，之后恢复不回来"
            @click="destroy(item)"
          >
            {{ busyId === item.id ? '销账中…' : '彻底删' }}
          </button>
        </div>
      </li>
    </ul>
  </section>
</template>
