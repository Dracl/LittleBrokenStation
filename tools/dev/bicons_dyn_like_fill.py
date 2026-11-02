#!/usr/bin/env python3
# 补一枚 BDC/hand_thumbsup_fill（评论区点亮态）：同 bicons_dyn_footer 的采法
import json
import sys

d = json.load(open('OH-Works/hi-res-scratch/icon_hand_thumbsup_fill.json', encoding='utf-8'))
ax = d['axes'][0]
assert ax['name'] == 'stroke'
path = ax['min'][0]
inner = f'<path fill="currentColor" d="{path}"></path>'

snippet = (
    '// 评论区·点赞点亮态（BDC/hand_thumbsup_fill v1, stroke=1），自 s1.hdslb.com/bfs/svg-next/BDC/hand_thumbsup_fill/v1.json 直采\n'
    f'export const biDynLikeFill = {{ vb: "0 0 24 24", inner: {json.dumps(inner, ensure_ascii=False)} }}\n'
)

path_js = 'web/src/assets/bicons.js'
src = open(path_js, encoding='utf-8').read()
if 'biDynLikeFill' in src:
    print('已入过库，跳过')
    sys.exit(0)
open(path_js, 'a', encoding='utf-8').write(snippet)
print('appended', len(snippet), 'bytes')
