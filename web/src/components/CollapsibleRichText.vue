<script setup>
// 长文折叠，全站一条规则：正文按换行计数，超过 lines 行就先只渲染前 lines 行，尾部出「展开」，点一下看全文、再点收起。
// 视频评论区（置顶主楼 / 主楼 / 楼中楼）与动态（正文 / 转发按语 / 转发明细 / 动态评论区）共用这一份，别在调用点各写一套。
// 切的是原文再交给 utils/emote 的同一根渲染管线，表情 token（[xxx_yyy]）不含换行，切不坏。
// 阈值 15 行是站长 2026-10-28 拍板的观感线；正好 15 行不折。行高由调用点的 contentClass 决定
// （评论区主楼 28px、楼中楼 24px、动态正文 ~22.5px），所以「15 行」在各处折出来的像素高度自然不同。
// 已知盲区：一行到底、不带换行的长文不触发（换行判定说了算），要补再这里加渲染高度兜底。
import { computed, ref } from 'vue'
import { richHtml } from '../utils/emote'

const props = defineProps({
  text: { type: String, default: '' },
  // 「转义符 → 图片地址」，原样交给 richHtml
  emotes: { type: Object, default: null },
  // 「转义符 → { jumpTitle, packageId, … }」：只有 B 站未下发 URL 的那批用得上（失效胶囊里念名字），老快照可为空
  emoteMeta: { type: Object, default: null },
  lines: { type: Number, default: 15 },
  contentClass: { type: String, default: 'whitespace-pre-line break-words' },
  buttonClass: { type: String, default: 'mt-0.5 block text-[12px] text-stationPink transition hover:underline' },
})

const open = ref(false)
const rows = computed(() => (props.text ?? '').split('\n'))
const collapsible = computed(() => rows.value.length > props.lines)
const shownText = computed(() => {
  if (!collapsible.value || open.value) return props.text ?? ''
  return rows.value.slice(0, props.lines).join('\n')
})
const html = computed(() => richHtml(shownText.value, props.emotes, props.emoteMeta))

function toggle() {
  open.value = !open.value
}
</script>

<template>
  <div class="min-w-0">
    <p :class="contentClass">
      <slot name="prefix" /><span v-html="html"></span>
    </p>
    <button v-if="collapsible" type="button" :class="buttonClass" @click="toggle">
      {{ open ? '收起 ⌃' : `展开 ⌄ 共 ${rows.length} 行` }}
    </button>
  </div>
</template>
