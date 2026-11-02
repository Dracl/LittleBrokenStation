# Third-Party Notices

小破分站 Little Broken Station 依赖或复用的第三方组件及其许可。
本文件随依赖变化同步更新；官方二进制素材（B 站 logo、位图、图片、icon sprite、字体）不在本仓库分发。

## 代码复用与移植

| 组件 | 许可 | 说明 |
| --- | --- | --- |
| [downkyicore](https://github.com/crazysmile-PhD/downkyicore) | GPL-3.0 | 下载引擎。M1 起以 vendor/downkyicore 引入，Bilibili 协议、DownloadTask 状态机与下载 pipeline 由其移植而来。Copyright (c) crazysmile-PhD |
| [BBDown](https://github.com/aliveranme/BBDown) | MIT | 内置 HTTP range 下载管线（分段、worker 队列、可续传分块与卡死读处理）改自 BBDown。Copyright (c) 2020 nilaoda；Copyright (c) 2025 AliverAnme。署名与全文见 vendor/downkyicore/THIRD-PARTY-NOTICES.md |

## 界面图标

播放页与互动区的界面图标（点赞、投币、收藏、分享、弹幕开关/设定、播放器控制条等）是从 B 站播放页
渲染出的 DOM 快照里复原的 **SVG 路径数据**，以 JS 源码形式存在 `web/src/assets/bicons.js`，不是官方位图、
sprite 或字体文件。这些图形的权利归 bilibili 所有，此处仅作互操作与版式复原之用；如权利人认为需要下架，
请开 issue，我们会移除并改为自绘替身。仓库内不留官方二进制素材原件（`tools/dev/` 的历史抓取脚本里
的原始 SVG 源文也已移除）。

## 前端第三方

| 组件 | 许可 | 用途 |
| --- | --- | --- |
| [qrcodejs](https://github.com/davidshimjs/qrcodejs) | MIT | 登录对话框里的登录二维码。Copyright (c) 2012 davidshimjs。以压缩脚本内联于 `web/src/libs/qrcode.min.js`，文件头保留原始版权声明 |

## .NET 依赖包

以下版本以 `src/*.csproj` 与 `vendor/downkyicore/Directory.Packages.props` 为准。

| 组件 | 许可 | 用途 |
| --- | --- | --- |
| Microsoft.Data.Sqlite.Core | MIT | 上游 DownKyi.Infrastructure 使用的 SQLite 驱动（不含原生捆绑） |
| Microsoft.Extensions.*（Hosting.Abstractions、Logging、Logging.Abstractions、DependencyInjection、Http） | MIT | 后台 Host、依赖注入、日志与 HTTP 客户端，前后端与 Worker 共用 |
| Google.Protobuf | BSD-3-Clause | B 站 gRPC 接口的 protobuf 消息运行时（vendor/downkyicore/DownKyi.Core） |
| Grpc.Tools | Apache-2.0 | protobuf / gRPC 代码生成。`PrivateAssets="All"`，只在构建期跑，工具本体不进运行时 |
| Newtonsoft.Json | MIT | 上游 DownKyi 的 JSON 序列化（B 站接口字段兼容） |
| NLog | BSD-3-Clause | 上游 DownKyi 的日志后端 |
| CommunityToolkit.Mvvm | MIT | 上游 DownKyi.Worker 的 observable 对象基类 |
| Semver | MIT | 上游 DownKyi.Application 的版本号比较。Copyright (c) 2013-2024 Jeff Walker, Max Hauser |
| SQLitePCLRaw.bundle_e_sqlite3 | Apache-2.0 | 明文 SQLite 的原生绑定（src/LBS.Infrastructure） |
| SQLite3MC.PCLRaw.bundle | MIT | 加密 SQLite 的原生绑定，内含 SQLite3MultipleCiphers（Copyright (c) 2023-2025 Ulrich Telle）；绑定层借用 SQLitePCLRaw（Apache-2.0） |

上游 `vendor/downkyicore/src/DownKyi.Infrastructure/Settings/SettingsManager.cs` 里的
`"YO1J$4#p"` 是上游沿用的 sqlcipher 旧格式兼容密钥（用于解开历史版本导出的设置档），
不是本项目的凭据，也不用于本项目的任何数据；本项目的 cookies 与库文件按 `LBS_DATA_DIR` 明文落盘，
访问控制靠站外权限与站长室门禁。

## 运行时与框架

| 组件 | 许可 | 用途 |
| --- | --- | --- |
| .NET 10 / ASP.NET Core | MIT | 后端框架与后台 Worker |
| Microsoft.Data.Sqlite | MIT | SQLite 驱动 |
| SQLite | Public Domain | 元数据存储（WAL 模式） |
| Vue 3 | MIT | 前端框架 |
| Vue Router | MIT | 前端路由 |
| Vite | MIT | 前端构建（仅构建期） |
| Tailwind CSS | MIT | 前端样式（构建产物内联） |
| pnpm | MIT | 前端包管理（仅开发期） |

## 外部进程

| 组件 | 许可 | 用途 |
| --- | --- | --- |
| FFmpeg | LGPL/GPL（构建配置而定） | 封面抽取与预览条生成。容器内使用发行版系统包，仓库不打包分发 |

## 字体

容器镜像使用 Debian 仓库的 fonts-noto-cjk（SIL Open Font License 1.1）作为中文回退，
由系统包管理器安装，仓库不打包分发字体文件。
