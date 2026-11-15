<div align="center">

<img src="web/public/favicon.svg" width="110" alt="小破分站台标" />

# 小破分站

<sub><b>L I T T L E&nbsp;&nbsp;B R O K E N&nbsp;&nbsp;S T A T I O N</b></sub>

---

分站不破，因为服务器在你手里。<br>
把链接和收藏夹丢进去，长出一座**属于自己的**小站。

[![许可证 GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)
![镜像](https://img.shields.io/badge/镜像-GHCR-cyan)
[![docker pull](https://img.shields.io/badge/docker-pull-red?logo=docker&logoColor=white)](#快速开始)

</div>

小破分站（Little Broken Station，缩写 LBS）是一个自托管的 B 站视频归档站。
把 B 站视频链接、收藏夹或 UP 主主页丢进去，它会把视频、弹幕、字幕、封面和元数据完整下载到本地，
前端按 B 站网页版的版式还原观看体验。整站单个 Docker 镜像、单端口、单数据目录，服务器在你手里。

## 它做什么

**入库**
- 粘贴 B 站链接投稿；UP 主主页一键整架导入（封顶 500 件，止损三连防灌水）
- 订阅更新：登记收藏夹链接或博主 MID，爬工每小时带随机抖动巡站，新 BV 自动投稿进站
- 台账自动去重，重复投稿只补档案不建重行；封面与真实投稿时间入站在即

**下载**
- 视频（可点单 avc/hevc/av1 编码加档）、弹幕、字幕、封面、元数据、评论快照全部本地化
- 断点续传、失败重试、卡死一键续下（软续优先，打不动删行重投）
- 下载控制板：全站暂停/继续（重启记得住）、取消排队区、逐件提交间隔（极速或 1/5/10/30 分钟，实发带 ±10% 随机抖动）

**账号与反爬**
- B 站扫码绑定登录，cookie 只进不出；采集走采集号池，优先站长席位的活号（全站单采集号，逐视频轮流的旧口径已废）
- 全局节流闸、风控熔断冷却（412/429/-352 自动避锋）、会话预热、浏览器指纹随机池、周期与任务间抖动，站长室实时显示冷却倒计时

**看站**
- 首页 B 站同款卡阵：新货置顶 + 组内随机洗牌 + 骰子换一批，无限滚动帘子，封面走 CDN 缩略
- 观看页：播放器、滚动/顶部/底部弹幕（带上下保护区）、站内弹幕发送、UP 名片、分 P 选集、相关推荐、接下来播放
- 弹幕治理：本人凭自己的访客签删自己的，站长席位删任意（无主老弹幕也归站长删）

**站长室**
- 柜台（家底与健康度）、进货口（下载 · 订阅 · 动态关注）、货架（悬停预览、下载设置、成员与公告、备份与搬家）、
  危险区另置；冷光极简风，不受复刻约束

## 快速开始

```bash
docker run -d --name lbs \
  -p 8090:8090 \
  -v lbs-data:/data \
  ghcr.io/dracl/littlebrokenstation:latest
```

浏览器打开 `http://你的NAS:8090` 即可，站长室在 `/admin`。
数据目录 `/data` 里有视频库、SQLite、cookies 和缓存，备份它等于备份整座分站。

> **首启铁律 · 先占席，再开门**：站上还没有主人的时候，第一位扫码/注册进站者**无条件继位站长**
> （拿到全站管理权）——这是设计内的 bootstrap，不是漏洞。所以公网暴露之前，先只在本地或局域网把站起来，
> 自己扫码把站长席位占了，再考虑对外；端口先上公网、席位后占，等于把站门钥匙插在门外等人来拧。

### 容器变量

镜像把该钉的都在 Dockerfile 里钉好了，**正常起站一个变量都不用填**（只改宿主访问端口连 `-e` 都不用，改 `-p` 左边就行）。特殊场景才需动的，全在这张表里：

| 变量 | 默认 | 什么时候动它 |
|---|---|---|
| `LBS_URLS` | `http://0.0.0.0:8090` | 换**容器内**监听端口时用（例 `-e LBS_URLS=http://0.0.0.0:9000`，`-p` 的容器侧要跟着换）。它优先于 `ASPNETCORE_URLS`：后者对本项目是死配置（`Program.cs` 里 `UseUrls` 只读 `LBS_URLS`），改了不生效。镜像里的 HEALTHCHECK 也跟着 `$LBS_URLS` 现推端口，换端口不会把健康检查换瞎。 |
| `LBS_TRUSTED_PROXY_CIDRS` | 空（谁都不信） | 挂 nginx/caddy 反代后必配，逗号分段 CIDR（例 `127.0.0.1/32,192.168.0.0/16`）。不配则访客真 IP 记成代理机、https 被当明文；局域网直连部署不用碰。 |
| `LBS_DATA_DIR` | `/data`（镜像钉死） | 全站数据根目录，库、视频、预览、备份、cookies 全在这棵树下。对外只靠 `-v 卷:/data` 这一根管子，变量本身别改。 |
| `DOWNKYI_DATA_DIR` | `/data/downkyi` | 下载引擎暂存，住在数据目录里，别改。 |
| `ASPNETCORE_CONTENTROOT` | `/app` | 前端静态根的钉子，改了整站 404，别改。 |
| `TZ` | ——不需要 | 周报、周桶全在代码里按 UTC+8 计算，容器时区是 UTC 也照发不误，不用设。 |

> **挂宿主目录的属主坑与选盘**：容器里跑站的是非 root 用户（uid 10001）。要用 `-v /volume1/lbs:/data` 这类 bind mount，宿主目录得先 `chown -R 10001:10001 /volume1/lbs`，否则分站起不来只会一路权限报错；命名卷 `-v lbs-data:/data` 不用管这一条，Docker 会照镜像内属主自动初始化。
> 另：数据目录请放 NAS 本地盘，不要把 SQLite 放到 NFS 或 SMB 共享挂载上，WAL 模式在网络文件系统上会坏库。

### 公网部署

分站自身只跑明文 HTTP，**公网暴露必须挂在 TLS 反向代理（nginx / Caddy）身后**；会话 cookie 已带上 Secure 语义
（只在 HTTPS 信道上传；判据是请求本身是不是 HTTPS，所以反代必须透传 `X-Forwarded-Proto`，否则 https 站点也会发出不带 Secure 的票），
明文直连公网等于把成员会话与站长锁一起裸奔。
挂反代必须配 `LBS_TRUSTED_PROXY_CIDRS`（见上表）：不配的话分站眼里所有人的 IP 都是代理机那一个，
小黑屋会变成「任何人输错 6 次锁全站」，扫码票的 IP 绑定也一并形同虚设。局域网直连用不着这一项。

## 本地开发

前置：.NET 10 SDK、Node 22、pnpm 12、ffmpeg（开发平台各装各的，容器内用系统 ffmpeg）。

```bash
# 后端（端口 8090）
dotnet run --project src/LBS.Server

# 前端（开发端口 5173，/api 代理到 8090）
cd web && pnpm install && pnpm dev

# 测试（与 CI 同一张嘴：同为 Release，少了 -c Release 就是两张嘴）
dotnet test tests/LBS.Tests -c Release --nologo
```

数据目录按 `LBS_DATA_DIR` → 工作目录下 `./data`（有库才算）→ 往上找最近一座 `data/station.db` 的顺序定，
都没才新建。`dotnet run` 会把工作目录切到 `src/LBS.Server`，靠这条兜底才不会在 `src/LBS.Server/` 下自举出一座空站；
真要不放心，起站前 `$env:LBS_DATA_DIR = "<绝对路径>"` 钉死，开站日志那一行也会把数据目录的来路念出来。

`tools/dev/` 是作者开发期的一次性脚本（图标抓取、素材生成之类），依赖被 `.gitignore` 挡在库外的本地目录
（如 `OH-Works/`），不保证在别人机器上跑得通，也不在 CI 覆盖范围内。

## 命名词源

「破站」一词源自早年 A 站用户自嘲 B 站服务器不稳定，后来被 B 站用户自嘲时笑纳，成了社区昵称。
本站名字里的「破」是对这个自嘲文化的致敬，「分站不破」则是我们的野心：跑在你自己手里的站，不该看别人脸色的服务器。

## 免责三件套

- 本项目是 unofficial 社区项目，与 bilibili 官方无任何关联、授权或背书关系。
- 仅供个人备份自己有权访问的内容与学习研究使用，请在下载后 24 小时内删除，版权与一切权利归内容原作者所有。
- B 站官方位图素材（logo 图片、icon sprite、截图）一个字节都不进本仓库，界面版式全部自行用 CSS 实现，
  标识位使用自绘替身并支持从本地文件载入你自己的素材。两处如实交代（出处见 THIRD-PARTY-NOTICES.md）：
  海报字体用华为 HarmonyOS Sans SC（依其字体许可免费商用，本仓库只放了自切的一份 woff2），
  默认头像是 2ch「捏他酱」形象的灰色剪影，两者版权均归原权利方。
- 一处如实说明：界面图标（播放页与互动区那十几枚）是自 B 站播放页渲染出的 DOM 复原的 **SVG 路径数据**，
  以 JS 源码形式随仓库分发（见 THIRD-PARTY-NOTICES.md 的「界面图标」节）。这是路径数据、不是官方位图或 sprite；
  图形权利归 bilibili，如权利人认为需要下架，开个 issue 即可，我们换成自绘替身。

## 许可与致谢

整站以 GPL-3.0 开源。下载引擎复用并移植自 [downkyicore](https://github.com/crazysmile-PhD/downkyicore)
（GPL-3.0，作者 crazysmile-PhD），详见 THIRD-PARTY-NOTICES.md。
上游发版勤快，我们的 fork 保持窄改动以便长期 rebase。

另有两处设计思路上的致谢（只借鉴思路，未搬代码）：

- 分享视频的海报绘图（动态卡片式推送图）参考了 [dynamic-bot](https://github.com/Colter23/dynamic-bot)
- 反爬机制（节流、熔断冷却、指纹与抖动的采集姿态）参考了 [RC-WebGallery](https://github.com/JDui/RC-WebGallery)

## 路线图

- ✅ M0 骨架：repo、后端壳、前端壳
- ✅ M1 下载核心：链接解析入库、收藏夹导入、下载队列、Cookie 登录（含 downkyicore Worker 移植）
- ✅ M2 复刻 B 站：顶栏、搜索、首页视频卡、播放页对基准并排比对
- ✅ M3 分站灵魂：本地弹幕、订阅爬工、UP 名片与互动档案、账号分流、反爬套件、暂停板
- ✅ M4 上线：README 定稿、站长室改走 owner 会话门（身份即钥匙，口令锁留档待公网形态）、台账定时备份、收藏夹订阅实测
- ✅ M4.5 容器化：Dockerfile + GHCR CI（已写就，单测 + 前端构建 + 起容器冒烟已并入 .github/workflows/oci.yml）
- ✅ M5 到站与周报：新货到站播送、信匣与三路静音闸、分站周报、分享海报与周报海报双款调参台
- 🔭 远期：接入 AI 功能

<div align="center">

<sub>分站不破，因为服务器在你手里。</sub>

</div>
