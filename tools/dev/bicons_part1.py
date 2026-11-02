#!/usr/bin/env python3
# 从 B 站播放页（BV19rpt6tE88）DOM 扒回的官方图标原始 svg，清洗成 web/src/assets/bicons.js
# 清洗规则：去 defs/clipPath（只裁画布矩形）、去 display:none 空组、去 data-v-*/data-pointer/style 脚手架
# 产物格式：export const biXxx = { vb: 'viewBox', inner: 'svg 内容' }，由 BIcon.vue 统一渲染
import re, json, io, sys

RAW = {}

# [已移除：原始 B 站图标 SVG 源文，避免素材随仓库分发]

# [已移除：原始 B 站图标 SVG 源文，避免素材随仓库分发]

# [已移除：原始 B 站图标 SVG 源文，避免素材随仓库分发]

# [已移除：原始 B 站图标 SVG 源文，避免素材随仓库分发]

# [已移除：原始 B 站图标 SVG 源文，避免素材随仓库分发]

# [已移除：原始 B 站图标 SVG 源文，避免素材随仓库分发]

def clean(svg):
    m = re.search(r'viewBox="([^"]+)"', svg)
    vb = m.group(1)
    inner = re.sub(r'^.*?viewBox="[^"]*"', '', svg, count=1)
    inner = re.sub(r'></svg>\s*$', '', inner)
    inner = re.sub(r'<defs>.*?</defs>', '', inner, flags=re.S)
    inner = re.sub(r'\s*clip-path="url\([^)]*\)"', '', inner)
    inner = re.sub(r'<g style="display: ?none;">.*?</g></g>', '', inner, flags=re.S)
    inner = re.sub(r'<g style="display: ?none;">.*?</g>', '', inner, flags=re.S)
    inner = re.sub(r'\s*(data-v-[a-z0-9]+|data-pointer|style)="[^"]*"', '', inner)
    inner = re.sub(r'\s+xmlns="[^"]*"', '', inner)
    inner = re.sub(r'\s*width="[^"]*"|height="[^"]*"', '', inner)
    inner = re.sub(r'\s+', ' ', inner).strip()
    return vb, inner

def main():
    lines = [
        '// B 站播放页官方图标（2026-10 自 BV19rpt6tE88 播放页 DOM 扒录，lottie 渲染快照清洗而来）。',
        '// 交互四件套 fill=currentColor（着色交给外层 text-*）；播放器系 fill=rgb(255,255,255)（控制条白图标）。',
        '// 用 BIcon.vue 渲染：<BIcon :meta="biLike" class="h-6 w-6 text-inkSub" />',
        '',
    ]
    names = [('like','biLike'),('coin','biCoin'),('fav','biFav'),('share','biShare'),
             ('dmOn','biDmOn'),('dmSet','biDmSet')]
    for key, jsname in names:
        vb, inner = clean(RAW[key])
        lines.append('export const %s = { vb: %s, inner: %s }' % (jsname, json.dumps(vb), json.dumps(inner)))
    open(sys.argv[1], 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
    print('part1 written:', len(lines), 'lines')

main()
