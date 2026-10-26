# Third-Party Notices

小破分站 Little Broken Station 依赖或复用的第三方组件及其许可。
本文件随依赖变化同步更新；官方二进制素材（B 站 logo、图片、icon sprite、字体）不在本仓库分发。

## 代码复用与移植

| 组件 | 许可 | 说明 |
| --- | --- | --- |
| [downkyicore](https://github.com/crazysmile-PhD/downkyicore) | GPL-3.0 | 下载引擎。M1 起以 vendor/downkyicore 引入，Bilibili 协议、DownloadTask 状态机与下载 pipeline 由其移植而来。Copyright (c) crazysmile-PhD |

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
