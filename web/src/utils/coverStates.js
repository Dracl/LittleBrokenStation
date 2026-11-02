import { ref } from 'vue'

// 封面「图到位了没」的账本：图到位前隐身（底下的微光帘顶着），到位后淡入，杜绝图糊上来的那一下白闪。
// 为什么是一本账而不是一个 ref：大屏幕模式的分区行是一个组件实例挂着二十张卡，一卡一个 ref 写不下；
// 网格卡阵是一卡一实例，用同一本账、只记自己那一个键。两副卡面从此共用一套淡入口径。
//
// 只许 Vue 绑这个 class，不许 e.target.classList.add('is-on') 手工塞：同一枚元素上挂着动态 :class（hover 放大），
// Vue 改 class 是把「静态 + 动态」整串重写，手工塞的那颗会被抹掉，opacity:0 当场复活（历史页 11-02 就是这么变灰的）。
export function createCoverLedger() {
  const ready = ref(new Set())

  function markReady(key) {
    if (ready.value.has(key)) return
    ready.value = new Set(ready.value).add(key)
  }

  function isReady(key) {
    return ready.value.has(key)
  }

  // 图命中缓存时 load 可能不补发：拿元素自己补探一次 complete（VideoCard 原先 onMounted 里那一枪的账）。
  // naturalWidth 也要看：加载失败的图 complete 同样是 true，别把它记成到位。
  function probe(key, el) {
    if (el && el.complete && el.naturalWidth > 0) markReady(key)
  }

  return { markReady, isReady, probe }
}
