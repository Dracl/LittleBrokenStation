# syntax=docker/dockerfile:1
# 小破分站 Little Broken Station 单镜像交付。
# M0 仅占位维护（随项目结构同步，未实测），M4 上线时实测并接入 GHCR CI。
# 红线：官方二进制素材一个字节不进镜像；ffmpeg 用发行版系统包。

# ---------- 前端构建 ----------
FROM node:22-bookworm AS web-build
WORKDIR /src/web
COPY web/package.json web/pnpm-lock.yaml ./
RUN corepack enable && pnpm install --frozen-lockfile
COPY web/ ./
RUN pnpm build

# ---------- 后端发布 ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server-build
WORKDIR /src
COPY fenzhan.slnx ./
COPY src/ src/
RUN dotnet publish src/LBS.Server -c Release -r linux-x64 --self-contained true \
    -p:PublishReadyToRun=false -o /publish
COPY --from=web-build /src/web/dist /publish/wwwroot

# ---------- 运行时 ----------
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-bookworm
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg fonts-noto-cjk ca-certificates \
    && rm -rf /var/lib/apt/lists/*

ENV LBS_DATA_DIR=/data \
    DOWNKYI_DATA_DIR=/data/downkyi \
    ASPNETCORE_URLS=http://0.0.0.0:8090 \
    DOTNET_gcServer=1

VOLUME /data
EXPOSE 8090
COPY --from=server-build /publish /app
WORKDIR /data
ENTRYPOINT ["/app/LBS.Server"]
