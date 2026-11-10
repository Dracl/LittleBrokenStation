// 组件生命周期与 watch 的最小代身：测试装载页面模块后手动取用回调。
// watchers 记录 (source, cb) 对，测试用 source() 探测出「route.params.bvid 那支 watcher」手动引爆。
export const hooks = { mounted: [], unmount: [], watchers: [] }

export function onMounted(cb) {
  hooks.mounted.push(cb)
  return cb
}

export function onBeforeUnmount(cb) {
  hooks.unmount.push(cb)
  return cb
}

export function watch(source, cb) {
  if (typeof cb === 'function') hooks.watchers.push({ source, cb })
  return () => {}
}

export function resetHooks() {
  hooks.mounted.length = 0
  hooks.unmount.length = 0
  hooks.watchers.length = 0
}
