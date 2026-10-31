#!/usr/bin/env python3
# 官方动态页（t.bilibili.com dyn-home）底栏三枚图标入库：
# SVGIconNext BDC 系，DynAction 渲染带 axes:{stroke:1}，取 axes[0].min（细线变体）。
# viewBox 0 0 24 24，fill path 法（线稿已转 fill 闭路），补 fill="currentColor" 交外层着色。
import json

import sys

def load(name):
    d = json.load(open(f'OH-Works/hi-res-scratch/icon_{name}.json', encoding='utf-8'))
    ax = d['axes'][0]
    assert ax['name'] == 'stroke'
    # axes:{stroke:1} → min 端（value: min=1, max=2）
    return ax['min'][0]

rows = [
    ('biDynForward', 'arrow_share_line', '动态底栏·转发（BDC/arrow_share_line v1, stroke=1）'),
    ('biDynComment', 'bubble_comment_line', '动态底栏·评论（BDC/bubble_comment_line v1, stroke=1）'),
    ('biDynLike', 'hand_thumbsup_line', '动态底栏·点赞（BDC/hand_thumbsup_line v1, stroke=1）'),
]

lines = []
for export, name, comment in rows:
    d = load(name)
    inner = f'<path fill="currentColor" d="{d}"></path>'
    lines.append(f'// {comment}，自 s1.hdslb.com/bfs/svg-next/BDC/{name}/v1.json 直采')
    lines.append(f'export const {export} = {{ vb: "0 0 24 24", inner: {json.dumps(inner, ensure_ascii=False)} }}')

snippet = '\n'.join(lines) + '\n'

path = 'web/src/assets/bicons.js'
src = open(path, encoding='utf-8').read()
if 'biDynForward' in src:
    print('已入过库，跳过')
    sys.exit(0)
open(path, 'a', encoding='utf-8').write(snippet)
print('appended', len(snippet), 'bytes to', path)
