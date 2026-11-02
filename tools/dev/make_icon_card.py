#!/usr/bin/env python3
# 从小破分站 web/src/assets/bicons.js 直接组装「B 站图标全家福」预览卡片。
# 动画参数与站内的 VideoActions/VideoPlayer 同款 keyframes（照小织实现搬运）。
import re, json, os

BICONS = 'web/src/assets/bicons.js'
OUT = os.path.join(os.getcwd(), 'OH-Works', 'Cards', 'bicons-showcase.html')

JS_STR = r'"(?:[^"\\]|\\.)*"'
icons = {}
for line in open(BICONS, encoding='utf-8'):
    m = re.match(r'^export const (\w+) = \{ vb: (' + JS_STR + r'), inner: (' + JS_STR + r') \}$', line)
    if m:
        icons[m.group(1)] = (json.loads(m.group(2)), json.loads(m.group(3)))

def svg(name, cls=''):
    vb, inner = icons[name]
    return '<svg class="bi %s" viewBox="%s" aria-hidden="true">%s</svg>' % (cls, vb, inner)

interaction = [('biLike', '点赞', '弹跳 + 粉色点亮'), ('biCoin', '投币', '硬币下落弹跳'),
               ('biFav', '收藏', '星星 pop + 金黄'), ('biShare', '分享', '轻缩放')]
player = [('biPlay', '播放'), ('biVolume', '音量'), ('biSetting', '设定'), ('biPip', '画中画'),
          ('biWide', '宽屏'), ('biWeb', '网页全屏'), ('biFull', '全屏'), ('biSubtitle', '字幕')]

inter_html = ''.join(
    '<button class="cell" data-kind="%s"><span class="ico ico-%s">%s</span>'
    '<span class="nm">%s</span><span class="ds">%s</span></button>'
    % (k, k, svg(k), nm, ds) for k, nm, ds in interaction)
play_html = ''.join(
    '<div class="pcell"><span class="pico">%s</span><span class="nm">%s</span></div>'
    % (svg(k), nm) for k, nm in player)

html = """<!DOCTYPE html>
<html><head><meta charset="utf-8"><title>B 站图标全家福</title>
<script type="application/json" data-card-manifest>{"spec":"1.0","display":{"preferredWidthPx":680}}</script>
</head><body>
<style>
:root{--pink:#fb7299;--gold:#ffe138;--blue:#00a1d6;--bar:#18191c}
*{box-sizing:border-box;margin:0;padding:0}
html,body{background:transparent}
.wrap{font-family:var(--font-ui,system-ui);color:var(--text,#222);padding:18px 20px 20px;background:var(--bg,#fff);min-height:1128px}
h1{font-size:19px;font-weight:700;padding-right:110px}
.sub{font-size:12.5px;color:var(--text-muted,#888);margin:4px 0 14px}
h2{font-size:14px;font-weight:600;margin:16px 2px 9px;display:flex;align-items:center;gap:7px}
h2 .tag{font-size:10.5px;font-weight:500;color:var(--text-muted,#999);border:1px solid var(--border,#ddd);border-radius:999px;padding:1px 8px}
.grid{display:grid;grid-template-columns:repeat(4,1fr);gap:10px}
.cell{display:flex;flex-direction:column;align-items:center;gap:6px;padding:16px 8px 12px;background:var(--bg-card,#f7f7f8);border:1px solid var(--border,#e6e6e8);border-radius:12px;cursor:pointer;font:inherit;color:inherit;transition:border-color .2s}
.cell:hover{border-color:var(--pink)}
.cell .bi{width:30px;height:30px;color:var(--text,#444);transition:color .25s}
.nm{font-size:13px;font-weight:600}
.ds{font-size:10.5px;color:var(--text-muted,#999);text-align:center;line-height:1.35}
.ico-coin .bi,.ico-share .bi{width:27px;height:27px}
.cell:hover .ico-like .bi,.cell.lit .ico-like .bi,.cell.lit .ico-coin .bi{color:var(--pink)}
.cell.lit .ico-fav .bi{color:var(--gold)}
.cell:hover .ico-share .bi{color:var(--blue)}
.anim-like{animation:va-like .45s ease}
.anim-coin{animation:va-coin .5s cubic-bezier(.34,1.56,.64,1)}
.anim-fav{animation:va-fav .4s cubic-bezier(.34,1.56,.64,1)}
.anim-share{animation:va-share .25s ease}
@keyframes va-like{0%{transform:scale(1)}30%{transform:scale(1.35)}55%{transform:scale(.85)}80%{transform:scale(1.08)}100%{transform:scale(1)}}
@keyframes va-coin{0%{transform:translateY(-14px) rotate(-8deg);opacity:.4}55%{transform:translateY(2px) rotate(4deg);opacity:1}75%{transform:translateY(-3px) rotate(-2deg)}100%{transform:translateY(0) rotate(0)}}
@keyframes va-fav{0%{transform:scale(.6)}55%{transform:scale(1.25)}80%{transform:scale(.94)}100%{transform:scale(1)}}
@keyframes va-share{0%{transform:scale(1)}50%{transform:scale(1.15)}100%{transform:scale(1)}}
.dmg{display:flex;gap:10px}
.dmcell{flex:1;display:flex;align-items:center;gap:10px;padding:12px 14px;background:var(--bg-card,#f7f7f8);border:1px solid var(--border,#e6e6e8);border-radius:12px;cursor:pointer;font:inherit;color:inherit;text-align:left}
.dmcell .bi{width:24px;height:24px;color:var(--text,#444)}
.dmcell .meta{display:flex;flex-direction:column;gap:2px}
.dmcell .ds{font-size:10.5px;color:var(--text-muted,#999)}
#dmBtn{transition:border-color .2s}
#dmBtn.on{border-color:var(--blue)}
#dmBtn.on .bi-dm path:nth-of-type(2){fill:var(--blue)}
#dmBtn.off .bi-dm{opacity:.45}
.bar{background:var(--bar);border-radius:12px;padding:14px 10px 10px;display:grid;grid-template-columns:repeat(8,1fr);gap:4px}
.pcell{display:flex;flex-direction:column;align-items:center;gap:7px;padding:8px 2px 4px;border-radius:9px;transition:background .2s}
.pcell:hover{background:rgba(255,255,255,.09)}
.pcell .bi{width:24px;height:24px}
.pcell .nm{font-size:11px;font-weight:400;color:rgba(255,255,255,.85)}
.note{margin-top:14px;font-size:11.5px;line-height:1.7;color:var(--text-muted,#999);border-top:1px dashed var(--border,#ddd);padding-top:10px}
.note b{color:var(--text,#555);font-weight:600}
</style>
<div class="wrap">
<h1>B 站播放页图标全家福 · 14 枚已入库</h1>
<p class="sub">自 BV19rpt6tE88 播放页 DOM 扒录的官方 lottie 渲染快照，清洗进 web/src/assets/bicons.js，站内已接线</p>

<h2>互动四件套 <span class="tag">点一下看动画 · 与站内同款 keyframes</span></h2>
<div class="grid">""" + inter_html + """</div>

<h2>弹幕栏 <span class="tag">24×24 官方枚 · 开关可切</span></h2>
<div class="dmg">
<button id="dmBtn" class="dmcell on"><span class="ico">""" + svg('biDmOn', 'bi-dm') + """</span>
<span class="meta"><span class="nm">弹幕开关</span><span class="ds">开启态对勾染 B 站蓝 #00a1d6</span></span></button>
<div class="dmcell" style="cursor:default"><span class="ico">""" + svg('biDmSet') + """</span>
<span class="meta"><span class="nm">弹幕设定</span><span class="ds">接入 VideoPlayer 弹幕栏设定钮</span></span></div>
</div>

<h2>播放器控制条 <span class="tag">深底白图标 · fill 自带</span></h2>
<div class="bar">""" + play_html + """</div>

<p class="note"><b>来历</b>：B 站把 lottie 烘焙进 JS bundle，拿不到 JSON，于是扒 DOM 渲染快照（视觉等同），动画改 CSS keyframes 复刻，不引 lottie-web。<br>
<b>接线</b>：四件套进 VideoActions（点亮是站内装饰态，不上报 B 站）；播放/音量/设定/画中画/全屏进 VideoPlayer 控制条（暂停=双圆角条自绘，静音=音量枚叠 45° 斜线）；弹幕两枚进弹幕栏。<br>
<b>宽屏/网页全屏/字幕</b>三枚是我们的播放器没有对应模式而备着，属预期待命。</p>
</div>
<script>
document.querySelectorAll('.cell').forEach(function(btn){
  var kind = btn.dataset.kind;
  btn.addEventListener('click', function(){
    var ico = btn.querySelector('.ico');
    var cls = 'anim-' + kind;
    ico.classList.remove(cls);
    void ico.offsetWidth;
    ico.classList.add(cls);
    if (kind !== 'share') btn.classList.toggle('lit');
    if (window.card) window.card.track('icon-demo', {icon: kind});
  });
});
var dm = document.getElementById('dmBtn');
dm.addEventListener('click', function(){
  dm.classList.toggle('on'); dm.classList.toggle('off');
  if (window.card) window.card.track('dm-toggle', {on: dm.classList.contains('on')});
});
</script>
</body></html>
"""

os.makedirs(os.path.dirname(OUT), exist_ok=True)
open(OUT, 'w', encoding='utf-8').write(html)
print('written:', OUT, len(html), 'bytes; icons used:', len(icons))
