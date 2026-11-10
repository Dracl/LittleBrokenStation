// 字幕转 WebVTT + cue 台账的回归（utils/subtitleVtt.js 纯函数直测）：
// 站长 11-11 实机抓到「面板显示中、画面没字」——台账 129 条字幕全是 .srt，<track> 的 VTT 解析器整文件判死。
// 钉死：补头与逗号只在时间戳行动手、BOM/CRLF 不毒、已是 VTT 原样、台词里的逗号不误伤、
// B 站 json 两种裹形、脏 to 收平、认不出形交空、.json 绝不走 srt 硬转；
// 11-11 深夜自绘改造后另钉解析段：头部/序号/NOTE 跳过、三形时间戳、标记剥净、脏块静默略、前闭后开取句。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { srtToVtt, bilibiliJsonToVtt, subtitleTextToVtt, parseVttCues, activeCueAt } from '../src/utils/subtitleVtt.js'

test('T1 srt 补头 + 时间戳逗号换点（多行台词保住）', () => {
  const srt = '1\r\n00:00:00,040 --> 00:00:03,000\r\n当我听到一段类似小号的采样\r\n\r\n2\r\n00:00:03,000 --> 00:00:06,360\r\n我决定\r\n用它写一首歌\r\n'
  const vtt = srtToVtt(srt)
  assert.ok(vtt.startsWith('WEBVTT\n\n'))
  assert.ok(vtt.includes('00:00:00.040 --> 00:00:03.000'))
  assert.ok(vtt.includes('00:00:03.000 --> 00:00:06.360'))
  assert.ok(vtt.includes('我决定\n用它写一首歌')) // 多行台词原样
  assert.ok(!vtt.includes(',')) // 全文再无逗号
})

test('T2 BOM 与杂 CRLF 不毒：认得出 VTT 货、转得动 srt 货', () => {
  const vttIn = '\uFEFFWEBVTT\r\n\r\n00:00:01.000 --> 00:00:02.000\r\n你好\r\n'
  assert.match(subtitleTextToVtt('/media/a/x.srt', vttIn), /^WEBVTT/) // 挂着 srt 名装着 VTT 货：原样放行
  const srtIn = '\uFEFF1\r\n00:00:01,000 --> 00:00:02,000\r\n喂\r\n'
  assert.match(subtitleTextToVtt('/media/a/y.srt', srtIn), /^WEBVTT\n\n1\n00:00:01\.000 --> 00:00:02\.000\n喂$/)
})

test('T3 台词里的数字逗号不误伤（逗号只换在时间戳行）', () => {
  const srt = '1\n00:00:01,500 --> 00:00:02,500\n那年工资 3,000 块\n'
  const vtt = srtToVtt(srt)
  assert.ok(vtt.includes('00:00:01.500 --> 00:00:02.500'))
  assert.ok(vtt.includes('那年工资 3,000 块')) // 台词行原样
})

test('T4 B 站字幕 json：直数组与 body 裹形都认', () => {
  const bare = '[{"from":0.04,"to":3.0,"content":"第一句"},{"from":3.0,"to":6.36,"content":"第二句"}]'
  const wrapped = '{"body":[{"from":3661.5,"to":3662.25,"content":"一小时外"}]}'
  const v1 = bilibiliJsonToVtt(bare)
  assert.ok(v1.startsWith('WEBVTT\n\n'))
  assert.ok(v1.includes('00:00:00.040 --> 00:00:03.000\n第一句'))
  assert.ok(bilibiliJsonToVtt(wrapped).includes('01:01:01.500 --> 01:01:02.250\n一小时外'))
})

test('T5 脏 json 交空：坏 JSON / 无数组 / 空 content 都不硬凑', () => {
  assert.equal(bilibiliJsonToVtt('不是json'), '')
  assert.equal(bilibiliJsonToVtt('{"foo":1}'), '')
  assert.equal(bilibiliJsonToVtt('[{"from":0,"to":1,"content":"  "}]'), '')
  // to 比 from 小的脏行：收平成零长 cue 而不是倒放
  const v = bilibiliJsonToVtt('[{"from":5,"to":2,"content":"怪账"}]')
  assert.ok(v.includes('00:00:05.000 --> 00:00:05.000\n怪账'))
})

test('T6 .json 认不出形交空，绝不按 srt 硬转', () => {
  assert.equal(subtitleTextToVtt('/media/a/3.json', '{"body":[]}'), '')
  assert.equal(subtitleTextToVtt('/media/a/3.json', '1\n00:00:01,000 --> 00:00:02,000\n货不对板'), '')
})

test('T7 空文本与光板串交空（调用方退回原地址）', () => {
  assert.equal(srtToVtt(''), '')
  assert.equal(srtToVtt(null), '')
  assert.equal(srtToVtt('   \n  \n'), '')
  assert.equal(subtitleTextToVtt('/media/a/x.srt', ''), '')
})

test('T8 秒表进位：时/分/秒/毫秒各归各位', () => {
  const v = bilibiliJsonToVtt('[{"from":3599.9994,"to":3661.001,"content":"进位"}]')
  assert.ok(v.includes('00:59:59.999 --> 01:01:01.001\n进位'))
})

test('T9 parseVttCues：头部区、cue 序号、NOTE/STYLE 全跳，只认时间戳块', () => {
  const vtt = [
    'WEBVTT',
    'Region: id=roll width:100% lines:3',
    '',
    '1',
    '00:00:01.000 --> 00:00:02.000',
    '第一句',
    '',
    'NOTE 给后期看的记账行，屏幕上不许有它',
    '',
    'STYLE',
    '::cue { color: red }',
    '',
    '2',
    '00:00:03.000 --> 00:00:04.000',
    '第二句',
    '',
  ].join('\n')
  const cues = parseVttCues(vtt)
  assert.equal(cues.length, 2)
  assert.deepEqual(cues[0], { start: 1, end: 2, text: '第一句' })
  assert.equal(cues[1].text, '第二句')
})

test('T10 parseVttCues：三形时间戳、逗号防御、时间戳行尾设置都不绊脚', () => {
  const vtt = [
    'WEBVTT',
    '',
    '01:02:03.500 --> 01:02:05.000',
    '时形',
    '',
    '02:03.250 --> 02:05.000',
    '分形',
    '',
    '12.5 --> 14.5',
    '秒形',
    '',
    '00:00:09,500 --> 00:00:10,000',
    '逗号漏网形',
    '',
    '00:00:20.000 --> 00:00:21.000 line:90% position:50% align:center',
    '带尾单',
    '',
  ].join('\n')
  const cues = parseVttCues(vtt)
  assert.deepEqual(cues.map((c) => [c.start, c.end, c.text]), [
    [3723.5, 3725, '时形'],
    [123.25, 125, '分形'],
    [12.5, 14.5, '秒形'],
    [9.5, 10, '逗号漏网形'],
    [20, 21, '带尾单'],
  ])
})

test('T11 parseVttCues：行内标记剥净、实体解包，&amp; 垫底不误解', () => {
  const vtt = [
    'WEBVTT',
    '',
    '00:00:01.000 --> 00:00:02.000',
    '<v 小明>你好 &lt;世界&gt;</v>',
    '<c.yellow>亮</c> &amp;amp; 稳',
    '',
  ].join('\n')
  const cues = parseVttCues(vtt)
  assert.equal(cues.length, 1)
  assert.equal(cues[0].text, '你好 <世界>\n亮 &amp; 稳') // 双重的 &amp;lt; 停在 &lt;，不一路解回 <
})

test('T12 parseVttCues：脏块静默略，一行坏账不拖垮整轨', () => {
  const vtt = [
    'WEBVTT',
    '',
    'abc --> def',
    '时间戳认不出',
    '',
    '00:00:05.000 --> 00:00:05.000',
    '零长隐形句',
    '',
    '00:00:06.000 --> 00:00:07.000',
    '   ',
    '',
    '00:00:08.000 --> 00:00:09.000',
    '好',
    '',
  ].join('\n')
  const cues = parseVttCues(vtt)
  assert.deepEqual(cues, [{ start: 8, end: 9, text: '好' }])
})

test('T13 真路全跑：srt 原文 → 转 VTT → cue 清单，到点取句', () => {
  const srt = '1\r\n00:00:00,040 --> 00:00:03,000\r\n第一句\r\n\r\n2\r\n00:00:03,000 --> 00:00:06,360\r\n我决定\r\n用它写一首歌\r\n'
  const cues = parseVttCues(subtitleTextToVtt('/media/a/x.srt', srt))
  assert.equal(cues.length, 2)
  assert.equal(activeCueAt(cues, 1.0), '第一句')
  // 前闭后开：交棒时刻旧句退场新句上岗，不许两句同屏闪现
  assert.equal(activeCueAt(cues, 3.0), '我决定\n用它写一首歌')
  assert.equal(activeCueAt(cues, 6.36), '')
})

test('T14 activeCueAt：重叠多句换行拼段；脏入参交空', () => {
  const cues = [
    { start: 1, end: 2, text: 'A' },
    { start: 1.5, end: 2.5, text: 'B' },
  ]
  assert.equal(activeCueAt(cues, 1), 'A')
  assert.equal(activeCueAt(cues, 1.5), 'A\nB')
  assert.equal(activeCueAt(cues, 2), 'B')
  assert.equal(activeCueAt(cues, 2.5), '')
  assert.equal(activeCueAt(cues, NaN), '')
  assert.equal(activeCueAt([], 1), '')
  assert.equal(activeCueAt(null, 1), '')
})
