import { askDeleteVideo } from '../confirmStore'
import { clearWallThumbs } from './wallThumbs'

// ─── 馆藏动线的共用把手：放映厅互动栏那两枚钮与「稿件设置」面板都调这里 —— 一处逻辑、两处入口。
// 这里只管发请求、把后端回的人话整理成能直接念的一句话；页面状态（重挂评论区、给弹幕 URL 换个 v=、
// 跳回大厅）一律留在调用方：面板不该知道放映厅里有哪些 ref，反过来也一样。
// 措辞单一出处同 confirmStore 的规矩：同一个动作在两个地方不该有四种说法。

const NET_FAIL = '分站后端没有应答'

// 重拉封面与媒体的三档话术：后端自己会回一句 message（以它为主），这三个词只在 message 缺席时用来拼兵底句。
// 措辞的铁律：cover 砸了得明说「旧的还留着」（别让人以为封面没了），media 排上了得带上「已回炉，下一班进站」。
const MEDIA_COVER = { replaced: '封面已换新', kept: '封面沿用留档', failed: '封面没换成，旧的还留着' }
const MEDIA_META = { updated: '档案已更新', kept: '档案没变', failed: '档案没采回来，原样留着' }
const MEDIA_STATE = { queued: '媒体已回炉，下一班进站', intact: '媒体完好，没动' }

// 拿 cover / meta / media 三个词拼一句能直接念的。三个词一个都没认出来时不编好话，照实说没回话。
export function mediaSentence(body) {
  const parts = [MEDIA_COVER[body?.cover], MEDIA_META[body?.meta], MEDIA_STATE[body?.media]].filter(Boolean)
  if (!parts.length) return '封面与媒体这一趟没个回话，稍后再试'
  // 两样都没砸才起肯定句的头；有一样没成就别吹，直接报各样的下场
  const green =
    (body.cover === 'replaced' || body.cover === 'kept') && (body.meta === 'updated' || body.meta === 'kept')
  return green ? `封面与媒体都查过一遍：${parts.join('，')}` : parts.join('，')
}

// 后端这些端点都回 { message }（人话），message 缺席 / 为空时才落到这里的兜底句上（say = 拿载荷里的词拼）。
const ACTIONS = {
  comments: { path: 'comments', ok: '评论已归档', fail: '评论采集失败' },
  meta: { path: 'meta-refresh', ok: '视频数据已重拉', fail: '视频数据重拉失败' },
  danmaku: { path: 'danmaku-refresh', ok: 'B 站弹幕已重拉', fail: '弹幕重拉失败' },
  subtitles: { path: 'subtitles-refresh', ok: '字幕存档已重拉', fail: '字幕重拉失败' },
  media: { path: 'media-refresh', ok: '封面与媒体已重拉', fail: '封面与媒体重拉失败', say: mediaSentence },
  requeue: { path: 'requeue', ok: '已重新排队', fail: '排队没成' },
  revive: { path: 'revive', ok: '已续下', fail: '续下失败' },
}

// 后端那句 message 为主（空串算没回）；没回时成了就拿 say 拼、没成就用那句短兜底。
function saidOf(meta, ok, body) {
  const said = typeof body?.message === 'string' ? body.message.trim() : ''
  if (said) return said
  if (!ok) return meta.fail
  return meta.say ? meta.say(body ?? {}) : meta.ok
}

// POST 一发馆藏动作。不抛：网络断了也回成 { ok: false }，调用方只管摆文案，不用处处 try/catch。
// 返回 { ok, status, message, body }（status 0 = 压根没连上；body = 后端那份原始 JSON，
// 留着是给调用方看细节用的 —— 比如 media 那一发拿 cover 认「封面负不负换了」，拼话术之外还得能判别）。
export async function postVideoAction(id, action) {
  const meta = ACTIONS[action]
  if (!meta) return { ok: false, status: 0, message: `不认得这个动作：${action}`, body: null }
  try {
    const res = await fetch(`/api/videos/${id}/${meta.path}`, { method: 'POST' })
    const body = await res.json().catch(() => null)
    return { ok: res.ok, status: res.status, message: saidOf(meta, res.ok, body), body }
  } catch {
    return { ok: false, status: 0, message: NET_FAIL, body: null }
  }
}

// 一次（或几样一起）档案补采。卡阵与放映厅的冒泡钮三个都要（评论 + 弹幕 + 视频数据），设置面板里每颗钮只点自己那一样 —— 走的都是这条。
// 字幕枚（subtitles）只归设置面板：整档刷新动的是盘上轨文件，那颗顺手钮不该敲它。
// 返回：{ kind, text, commentsOk, danmakuOk, subtitlesOk, mediaOk, metaOk, mediaReplaced, allOk, ran }
//   kind ∈ 'ok' | 'warn' | 'err'（该跑的几发全砸才算 err，只砸一部分是 warn）；text 是能直接念的那一句。
//   media 默认不跑：顺手钮不该顺手动到封面与媒体，只有面板那颗钮点名才走；meta 默认跟着顺（档案六栏无害，不碰盘上件）。
//   mediaReplaced 只在 media 那一发成了、且后端说 cover === 'replaced' 时为真 —— 封面件是原地覆盖同一个 rel，
//   URL 一个字不变，得靠它告诉放映厅「这张图该重请一次」（kept / failed 一律不打那个戳，不该白刷）。
//   同一个为真的那一刻这里还会清一次会员墙的小图缓存（清失败不影响这几个标志与 allOk，下面那段有账）。
export async function refetchArchive(id, { comments = true, danmaku = true, subtitles = false, media = false, meta = true } = {}) {
  const wants = []
  if (comments) wants.push('comments')
  if (danmaku) wants.push('danmaku')
  if (subtitles) wants.push('subtitles')
  if (media) wants.push('media')
  if (meta) wants.push('meta')
  if (!wants.length)
    return {
      kind: 'ok',
      text: '这一趟没要采什么',
      commentsOk: false,
      danmakuOk: false,
      subtitlesOk: false,
      mediaOk: false,
      metaOk: false,
      mediaReplaced: false,
      allOk: true,
      ran: 0,
    }

  const results = await Promise.all(wants.map((w) => postVideoAction(id, w)))
  const got = {}
  for (let i = 0; i < wants.length; i += 1) got[wants[i]] = results[i]

  const fails = results.filter((r) => !r.ok).length

  // 封面真换掉了，才顺手把会员墙那面小图缓存抹了重生成 —— 判据与下面返回的 mediaReplaced 是同一条（kept / failed
  // 时盘上还是老图，清了等于白白重解码一整墙，是自毁缓存不是清缓存），故把标志先算出来、两处共用。
  // 为什么补在这一行而不是调用方：refetchArchive 是重拉封面的唯一漏斗，放映厅那颗钮与「稿件设置」面板那颗钮都从这儿过，
  // 在这儿补一次两个入口一起受益，也不必在两边各抄一份 cover === 'replaced' 的判断（措辞与判据单一出处的同一条规矩）。
  // 墙上为什么非得清：CanvasWall 的小图是按原图 URL 存在 IndexedDB 里的，封面件又是原地覆盖同一个 rel，URL 不变、
  // 缓存就不作废；放映厅靠给 src 打 ?r= 戳逼重请，墙上没那枚戳，只能把陈旧缩略图清掉让它重新显影。
  const mediaOk = got.media?.ok === true
  const mediaReplaced = mediaOk && got.media?.body?.cover === 'replaced'
  if (mediaReplaced) {
    // 清不成不算这趟重拉的败事：隐私模式下 IndexedDB 可能直接拒绝开库（clearWallThumbs 自己尽量吞了，这里再兜一层）。
    // 缓存留着下次进墙照样能用，砸的是这一发的话术与调用方的 allOk，不值当。
    try {
      await clearWallThumbs()
    } catch {
      console.warn('重拉封面后清墙上小图缓存没成，下次进墙会自己重新显影')
    }
  }

  return {
    kind: fails === results.length ? 'err' : fails ? 'warn' : 'ok',
    text: results.map((r) => r.message).join('；'),
    commentsOk: got.comments?.ok === true,
    danmakuOk: got.danmaku?.ok === true,
    subtitlesOk: got.subtitles?.ok === true,
    mediaOk,
    metaOk: got.meta?.ok === true,
    mediaReplaced,
    allOk: fails === 0,
    ran: results.length,
  }
}

// 删档：措辞走 confirmStore 的 askDeleteVideo（全站那一句），确认后 DELETE。
// 返回 { outcome: 'cancelled' | 'removed' | 'failed', text }（text 只在 failed 时有话）。
// 真的删成之后该去哪儿是页面的事（放映厅回大厅），这里不碰路由。
export async function removeVideoEntry(id, label) {
  if (!(await askDeleteVideo(label))) return { outcome: 'cancelled', text: '' }
  try {
    const res = await fetch(`/api/videos/${id}`, { method: 'DELETE' })
    if (res.ok) return { outcome: 'removed', text: '' }
    const body = await res.json().catch(() => null)
    return { outcome: 'failed', text: body?.message ?? '删不动，稍后再试' }
  } catch {
    return { outcome: 'failed', text: NET_FAIL }
  }
}
