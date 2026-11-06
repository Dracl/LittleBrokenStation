<script setup>
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { defaultAvatar } from '../utils/avatar.js'
import { fmtFans } from '../utils/fans.js'
import { faceFor } from '../utils/faceChoice.js'

const props = defineProps({
  upName: { type: String, default: null },
  upMid: { type: Number, default: null },
  upFace: { type: String, default: null },
  upSign: { type: String, default: null },
  upFans: { type: Number, default: null },
  // 创作团队（B 站 view 接口 data.staff 的归档）：{ mid, name, role, face }[]，没采过为 null。
  // 非空 = 联合创作稿，画团队；空 = 普通稿，画原来那张 UP 名片。
  staff: { type: Array, default: null },
})

const faceFailed = ref(false)
watch(() => props.upFace, () => (faceFailed.value = false))

// 热链拉不到/根本没头像：顶全站默认小人，不再渲首字占位。
// 先过本机钦选闸（utils/faceChoice）：在空间页给这位换了脸，放映厅这块也跟着换。
const faceSrc = computed(() => {
  const face = faceFor(props.upMid, props.upFace)
  return face && !faceFailed.value ? face : defaultAvatar
})
function onFaceError() {
  if (!props.upFace || faceFailed.value) return // 已经在画默认图，不再来回折腾
  faceFailed.value = true
}

// 联合创作名单：本人也算一员（B 站自己就把他列在头一个，角色写「UP主」），名字外链 B 站空间。
const team = computed(() =>
  (props.staff ?? [])
    .filter((p) => p && (p.name || p.mid))
    .map((p) => ({
      mid: p.mid ?? null,
      name: (p.name || '').trim() || `席位 ${p.mid ?? '?'}`,
      role: (p.role || '').trim() || null,
      face: p.face || null,
    })),
)
const isTeam = computed(() => team.value.length > 0)

// 成员头像单独记失败：某一张拉不到只顶它自己那颗默认小人，不把整队一起打回。
// 索引作键而不是图 URL：同一个人理论上可能在名单里出现两次，B 站 staff 不保证不重复。
const mateFailed = ref({})
watch(() => props.staff, () => (mateFailed.value = {}))

function mateFace(p, i) {
  const face = faceFor(p.mid, p.face)
  return face && !mateFailed.value[i] ? face : defaultAvatar
}

function onMateError(i) {
  mateFailed.value = { ...mateFailed.value, [i]: true }
}

// 没 mid 的成员（B 站偶尔给不出）就不套外链，省得点开一个空白空间。
function spaceHref(p) {
  return p.mid != null ? `https://space.bilibili.com/${p.mid}` : undefined
}
</script>

<template>
  <!-- 站长定的两套脸：B 站给了联合创作名单（staff 非空）就画「创作团队」那块；普通稿还是原来那张 UP 名片。
       别给没有团队的稿子硬套团队皮 —— 分站九成稿子都是普通稿。 -->
  <div v-if="isTeam || upName || upMid || upFace" class="rounded-xl bg-cardBg p-4">
    <!-- ① 联合创作：版式照 B 站，标题带人数，横排头像 + 粉色名字 + 灰角色。
         B 站头像上那颗蓝加号是「关注」动作，站内没有关注体系，点了没处落，就不拄一个假钮上来。 -->
    <template v-if="isTeam">
      <div class="flex items-baseline gap-1.5">
        <span class="text-sm font-semibold text-inkMain">创作团队</span>
        <span class="text-xs text-inkFaint">{{ team.length }}人</span>
      </div>
      <div class="mt-2.5 grid grid-cols-3 gap-x-2 gap-y-3">
        <a
          v-for="(p, i) in team"
          :key="`${p.mid ?? 'x'}-${i}`"
          :href="spaceHref(p)"
          :target="p.mid != null ? '_blank' : undefined"
          rel="noreferrer"
          class="group/mate flex min-w-0 flex-col items-center text-center"
          :title="p.role ? `${p.name} · ${p.role}` : p.name"
        >
          <img
            :src="mateFace(p, i)"
            :alt="p.name"
            loading="lazy"
            referrerpolicy="no-referrer"
            class="h-12 w-12 rounded-full bg-white/5 object-cover ring-1 ring-black/5 dark:ring-white/10"
            @error="onMateError(i)"
          />
          <span class="mt-1.5 w-full truncate text-xs text-stationPink transition group-hover/mate:underline">{{ p.name }}</span>
          <span class="mt-0.5 w-full truncate text-[11px] text-inkFaint">{{ p.role || '创作' }}</span>
          <!-- 站内入口（站长 11-02 点头要）：名字照旧直开 B 站空间，另挂一颗小蓝胶囊进分站里的这一位。
               这位分站一篇都没收过也不打紧 —— UP 空间那页自会说「分站还没收过这位的稿件」。 -->
          <RouterLink
            v-if="p.mid != null"
            :to="`/up/${p.mid}`"
            class="mt-1 rounded-full px-1.5 py-px text-[10px] leading-4 text-stationBlue ring-1 ring-stationBlue/30 transition hover:bg-stationBlue hover:text-white"
            title="这位在分站的空间"
          >
            空间
          </RouterLink>
        </a>
      </div>
    </template>

    <!-- ② 普通稿：B 站同款结构，左头像，右三行（名字+发消息 / 签名 / 关注钮通栏）；充电钮按站长旨意不做 -->
    <div v-else class="flex gap-3.5">
      <img
        :src="faceSrc"
        :alt="upName ?? 'UP 主头像'"
        loading="lazy"
        referrerpolicy="no-referrer"
        class="h-14 w-14 shrink-0 rounded-full bg-white/5 object-cover"
        @error="onFaceError"
      />
      <div class="min-w-0 flex-1">
        <div class="flex items-center justify-between gap-2">
          <a
            v-if="upMid"
            :href="`https://space.bilibili.com/${upMid}`"
            target="_blank"
            rel="noreferrer"
            class="min-w-0 truncate text-base font-semibold text-inkMain transition hover:text-stationPink"
            :title="upName"
          >
            {{ upName ?? '未知 UP 主' }}
          </a>
          <p v-else class="min-w-0 truncate text-base font-semibold text-inkMain">{{ upName ?? '未知 UP 主' }}</p>
        </div>
        <p class="mt-0.5 line-clamp-2 text-xs leading-5 text-inkFaint">{{ upSign || '这个 UP 主还没有签名' }}</p>
        <!-- 两颗钮并排：左边站内蓝钮进 UP 空间（只看分站缓存的货），右边粉色照旧去 B 站关注。
             共用同一个 v-if：没 mid 两枚都不画，不给空链留缝。 -->
        <div v-if="upMid" class="mt-2.5 flex gap-1.5">
          <RouterLink
            :to="`/up/${upMid}`"
            class="flex shrink-0 items-center justify-center gap-1 rounded-md bg-stationBlue px-2.5 py-1.5 text-xs font-medium text-white transition hover:opacity-85"
            title="这位在分站的空间：只列分站已缓存的稿件"
          >
            <svg viewBox="0 0 16 16" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.5">
              <rect x="2.2" y="3" width="11.6" height="10" rx="1.6" />
              <path d="M2.2 6.2h11.6M6.2 6.2V13" stroke-linecap="round" />
            </svg>
            UP 空间
          </RouterLink>
          <a
            :href="`https://space.bilibili.com/${upMid}`"
            target="_blank"
            rel="noreferrer"
            class="flex min-w-0 flex-1 items-center justify-center gap-1.5 rounded-md bg-stationPink py-1.5 text-xs font-medium text-white transition hover:opacity-85"
          >
            <svg viewBox="0 0 16 16" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.5">
              <path d="M8 3v10M3 8h10" stroke-linecap="round" />
            </svg>
            去 B 站关注{{ upFans != null ? ` · ${fmtFans(upFans)}` : '' }}
          </a>
        </div>
      </div>
    </div>
  </div>
</template>
