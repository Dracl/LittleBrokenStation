import { ref } from 'vue'

/// 全站共用的「问一句、拿个 bool」弹窗门店：ConfirmDialog.vue 在 App.vue 统一挂载，调用方 `await requestConfirm({...})`。
/// 为什么不走 window.confirm：它顶着「localhost:8090 显示」的浏览器外壳，跟站内装裱两套皮，
/// 还阻塞主线程、样式全交不出去。这里换成投稿窗同款装裱的自制弹窗，接口形状保持不变。
const CLOSED = {
  open: false,
  title: '请确认',
  sub: '',
  message: '',
  confirmText: '确定',
  cancelText: '再想想',
  danger: false,
  icon: 'warn',
}

export const confirmState = ref({ ...CLOSED })

let settle = null

/// 开一张确认窗。opts: { title, sub, message, confirmText, cancelText, danger, icon }
/// icon 取 'warn' | 'out' | 'trash'；danger=true 时主钮走玫红，用于不可逆的活。
export function requestConfirm(opts = {}) {
  // 叠着问两句不体面：前一张直接按「取消」收场，再把新的推上去
  settle?.(false)
  confirmState.value = { ...CLOSED, ...opts, open: true }
  return new Promise((resolve) => {
    settle = resolve
  })
}

/// 弹窗自己收场时用（确定 true / 取消、点遮罩、按 Esc 都算 false）
export function answerConfirm(ok) {
  settle?.(ok)
  settle = null
  confirmState.value = { ...confirmState.value, open: false }
}

/// 删档动线上的措辞单一出处：卡阵、大封面 banner、分区横滚行、放映厅都拉这两句，
/// 免得同一个钮在四个页面四种说法。改词只改这里，别再在组件里手写 title。
export const HINT_DELETE_VIDEO = '删掉这个馆藏：视频、弹幕、字幕、封面一起清掉'
export const HINT_FETCH_COMMENTS = '重新拉一次 B 站评论和弹幕，不动站内弹幕'

/// 「删掉这一部」的问话全站一处：放映厅、网格卡阵、大封面首页共用同一句。
/// 措辞得说真话：删档是把六张表的行连文件抖进回收站，站长在回收站里还能反悔恢复；
/// 老那句「从分站彻底删除…不可恢复」写的是「彻底销毁」那一档的事，害人在能反悔的地方不敢下手。
export function askDeleteVideo(label) {
  return requestConfirm({
    title: '删掉这一部',
    sub: label,
    message: '视频、弹幕、字幕、封面会一起抖进回收站。站长在回收站里还能反悔恢复，只有再点「彻底销毁」才是真没了。',
    confirmText: '删掉',
    cancelText: '先不删',
    danger: true,
    icon: 'trash',
  })
}
