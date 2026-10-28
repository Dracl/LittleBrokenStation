<script setup>
// 头像显示倒挂（站长方针：平时白嫖 B 站图床，嫖不到回落本地）：
// src 起手 = 远程热链缩略（hdslb 裸链拼 _220x220.jpg，几 KB；已带 @ 或非 hdslb 域原样，cover.js 同款防御）；
// 热链挂了切 faceLocal（data/avatars 本地原图，存档最高清）；两边都空就顶全站默认小人。
// 尺寸、底色、字号全由外层 class 控制（单根条件渲染，class 落到 img 头上）。
import { computed, ref } from 'vue'
import { defaultAvatar } from '../utils/avatar.js'

const props = defineProps({
  face: { type: String, default: '' }, // 远程头像 url（原图或已定格的本地路径都可）
  faceLocal: { type: String, default: '' }, // /avatars/xxx 本地原图
  name: { type: String, default: '' }, // 默认头像的 alt 用（读屏与裂图提示）
})

const failedRemote = ref(false)
const failedLocal = ref(false)

const remoteThumb = computed(() => {
  const url = props.face || ''
  if (!url) return ''
  // 只给 hdslb 域名的裸链挂缩略后缀；带 @ 参数、非 hdslb 域（含 /avatars/ 本地路径）原样放行。
  if (!/^https?:\/\/[^/]*hdslb\.com\//.test(url) || url.includes('@')) return url
  return `${url}_220x220.jpg`
})

const src = computed(() => {
  if (remoteThumb.value && !failedRemote.value) return remoteThumb.value
  if (props.faceLocal && !failedLocal.value) return props.faceLocal
  return ''
})

function onError() {
  // 同一位只各许挂一次：热链挂了切本地，本地再挂就落占位，不来回横跳。
  if (src.value && src.value === props.faceLocal) failedLocal.value = true
  else failedRemote.value = true
}
</script>

<template>
  <img
    v-if="src"
    :src="src"
    referrerpolicy="no-referrer"
    loading="lazy"
    decoding="async"
    alt=""
    @error="onError"
  />
  <img
    v-else
    :src="defaultAvatar"
    :alt="name || ''"
    decoding="async"
    class="shrink-0 object-cover"
  />
</template>
