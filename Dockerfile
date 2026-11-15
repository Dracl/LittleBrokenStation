# syntax=docker/dockerfile:1
# 小破分站 Little Broken Station 单镜像交付。
# 构建验证由 .github/workflows/oci.yml 在 GHCR 流水线上实测；本地没装 Docker 时以 CI 绿为准。
# 红线：官方二进制素材一个字节不进镜像；ffmpeg 用发行版系统包。

# ---------- 前端构建 ----------
FROM node:22-bookworm AS web-build
WORKDIR /src/web
# pnpm-workspace.yaml 也要先拷：pnpm v11+ 的 allowBuilds（esbuild 二进制放行）只认这一份，
# 漏了它 pnpm install 会在构建脚本审批上卡住或静默不装 esbuild。
COPY web/package.json web/pnpm-lock.yaml web/pnpm-workspace.yaml ./
# pnpm 版本钉死与开发机一致，免得锁文件版本漂移现场猜谜
RUN corepack enable && corepack prepare pnpm@12.14.0 --activate && pnpm install --frozen-lockfile
COPY web/ ./
RUN pnpm build

# ---------- 后端发布 ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server-build
WORKDIR /src
# 版本号由 CI 用 git describe 递进来（见 .github/workflows/oci.yml），/api/health 报的就是它；
# 本地不传就吃 0.0.1 老默认，不致于因缺 tag 而构不出来。
ARG VERSION=0.1.1
# 编译图横跨 src → vendor/downkyicore（Engine 工程直弓 DownKyi.Worker），两棵树都得在
COPY lbs.slnx ./
COPY vendor/ vendor/
COPY src/ src/
RUN dotnet publish src/LBS.Server -c Release -r linux-x64 --self-contained true \
    -p:PublishReadyToRun=false -p:Version=$VERSION -o /publish
COPY --from=web-build /src/web/dist /publish/wwwroot

# ---------- 运行时 ----------
# 注意：.NET 10 已下掉 bookworm 变体（MCR 上 10.0 只有 noble/resolute/alpine 等），
# 硬写 10.0-bookworm 会在 load metadata 就 not found。选 noble（Ubuntu 24.04 LTS）：
# 官方镜像自带 universe 源，ffmpeg / fonts-noto-cjk / curl 都在，apt 行为与 Debian 系同构。
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble
# curl 既给 HEALTHCHECK 用，也给运维在容器里自查用
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg fonts-noto-cjk ca-certificates curl \
    && rm -rf /var/lib/apt/lists/* \
    && useradd -m -u 10001 lbs \
    && mkdir -p /data \
    && chown lbs:lbs /data

# ASPNETCORE_CONTENTROOT 必须钉在 /app：WORKDIR 是 /data，不钉的话 content root 跟着 /data 走，
# wwwroot 会被解析成 /data/wwwroot（空的），前端整站 404。
ENV LBS_DATA_DIR=/data \
    DOWNKYI_DATA_DIR=/data/downkyi \
    ASPNETCORE_CONTENTROOT=/app \
    DOTNET_gcServer=1
# ASPNETCORE_URLS 已从这页 ENV 摘除：Program.cs 的 UseUrls 只读 LBS_URLS，它是纯死配置，
# 留着只会让人改了它、端口纹丝不动地猜谜。听哪个端口全由 LBS_URLS 说了算，默认 8090。

VOLUME /data
# 默认监听端口（纯文档作用，docker 不靠它转发）：真听哪个端口由 $LBS_URLS 定，
# 换端口 -e LBS_URLS=http://0.0.0.0:9000，同时把 -p 的宿主侧也换掉。
EXPOSE 8090
COPY --from=server-build /publish /app
# 许可证随镜像分发（GPL-3.0 与上游 GPL 的合规义务）；.dockerignore 里给根 md 开了白名单才拷得到
COPY LICENSE THIRD-PARTY-NOTICES.md /app/licenses/
COPY vendor/downkyicore/LICENSE /app/licenses/downkyicore/
# 非 root 后 x 位得真给齐（root 有 CAP_DAC_OVERRIDE，以前就算 apphost 没 x 也能跳起来），
# 这里当场钉死，免得换用户后到运行那一刻才验出权限问题。
RUN chmod 0755 /app/LBS.Server
WORKDIR /data
USER lbs
# 健康检查的端口从 $LBS_URLS 现推（换端口的官方姿势就是覆盖这个变量）：硬编码 8090 的话，
# 站长一换端口健康检查就恒假，compose 的 health 条件与运维监控会集体误报。
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s \
    CMD sh -c 'port=$(echo "${LBS_URLS:-http://0.0.0.0:8090}" | sed "s/.*://"); curl -fsS "http://127.0.0.1:${port}/api/health" || exit 1'
ENTRYPOINT ["/app/LBS.Server"]
