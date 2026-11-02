#!/usr/bin/env python3
# 把 biSubtitle 手写行从单引号规范成与其余 13 枚一致的双引号 JSON 格式
import re, json

p = 'web/src/assets/bicons.js'
src = open(p, encoding='utf-8').read()
m = re.search(r"export const biSubtitle = \{ vb: '([^']*)', inner: '(.*)' \}", src)
vb = m.group(1)
inner = m.group(2).replace('\\"', '"')
new = 'export const biSubtitle = { vb: %s, inner: %s }' % (json.dumps(vb), json.dumps(inner, ensure_ascii=False))
src = src[:m.start()] + new + src[m.end():]
open(p, 'w', encoding='utf-8').write(src)
print('normalized biSubtitle line; starts:', inner[:20])
