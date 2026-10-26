分站不破，因为服务器在你手里。

把链接和收藏夹丢进去，长出一座属于自己的小站。

![复刻度](https://img.shields.io/badge/复刻度-0%25-informational)
![许可证 GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue)
![镜像](https://img.shields.io/badge/镜像-GHCR-cyan)

> 复刻度 badge 的数值在 M2 复刻开工时启用，每次发版更新。镜像 badge 在 CI 上线后指向 ghcr 仓库。

小破分站（Little Broken Station，缩写 LBS）是一个自托管的 B 站视频归档站。
把 B 站视频链接或浏览器导出的收藏夹文件丢进去，它会把视频、弹幕、字幕、封面和元数据完整下载到本地，
前端按 B 站网页版的版式 1:1 还原观看体验。整站单个 Docker 镜像、单端口、单数据目录，服务器在你手里。

## 它做什么

- 粘贴 B 站链接入库，支持导入浏览器导出的 Netscape 收藏夹文件并自动去重
- 下载视频、弹幕、字幕、封面与元数据，断点续传，失败重试
- Cookie 粘贴登录，容器数据全部落在一个 volume 里
- 本地观看页复刻 B 站：播放器、弹幕栏、右侧选集、点赞投币收藏关注操作栏
- 本地弹幕系统：入库、渲染、发送，样式对齐 B 站
- 站长室（管理后台）：登录管理、存储路径、皮肤开关、版本检查，冷光极简风，不受复刻约束

## 快速开始

镜像构建推送 GHCR 后，unRAID 或其他 Docker 环境一条命令起站：

```bash
docker run -d --name fenzhan \
  -p 8090:8090 \
  -v fenzhan-data:/data \
  ghcr.io/OWNER/fenzhan:latest
```

浏览器打开 http://你的NAS:8090 即可。数据目录 /data 里有视频库、SQLite、cookies 和缓存，备份它等于备份整座分站。
数据目录请放在 NAS 本地盘，不要把 SQLite 放到 NFS 或 SMB 共享挂载上，WAL 模式在网络文件系统上会坏库。

当前进度：M1 下载核心与 M2 观看页均已到站（视频、弹幕、字幕、封面、元数据、评论快照全部本地化），M3 分站灵魂与 M4 容器上线在路上。

## 本地开发

前置：.NET 10 SDK、Node 22、pnpm、ffmpeg（开发平台各装各的，容器内用系统 ffmpeg）。

```bash
# 后端（端口 8090）
dotnet run --project src/LBS.Server

# 前端（开发端口 5173，/api 代理到 8090）
cd web && pnpm install && pnpm dev
```

## 命名词源

「破站」一词源自早年 A 站用户自嘲 B 站服务器不稳定，后来被 B 站用户自嘲时笑纳，成了社区昵称。
本站名字里的「破」是对这个自嘲文化的致敬，「分站不破」则是我们的野心：跑在你自己手里的站，不该看别人脸色的服务器。

## 免责三件套

- 本项目是 unofficial 社区项目，与 bilibili 官方无任何关联、授权或背书关系。
- 仅供个人备份自己有权访问的内容与学习研究使用，请在下载后 24 小时内删除，版权与一切权利归内容原作者所有。
- 官方二进制素材（logo、图片、icon sprite、字体）一个字节都不进本仓库，界面版式全部自行用 CSS 实现，
  标识位使用自绘替身并支持从本地文件载入你自己的素材。

## 许可与致谢

整站以 GPL-3.0 开源。下载引擎复用并移植自 [downkyicore](https://github.com/crazysmile-PhD/downkyicore)
（GPL-3.0，作者 crazysmile-PhD），详见 THIRD-PARTY-NOTICES.md。
上游发版勤快，我们的 fork 保持窄改动以便长期 rebase。

## 路线图

- M0 骨架：repo、后端壳、前端壳（进行中）
- M1 下载核心：链接解析入库、收藏夹导入、下载队列、Cookie 登录（含 downkyicore Worker 移植）
- M2 复刻 B 站：顶栏、搜索、首页视频卡、播放页对基准并排比对
- M3 分站灵魂：本地弹幕、ffmpeg 封面与预览条、UP 主与合集页、到站通知
- M4 上线：设置页、GHCR CI、README 定稿
