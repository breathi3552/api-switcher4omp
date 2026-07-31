---
status: accepted
---

# 使用单一私有 Named Pipe 控制 Bifrost sidecar

ProviderPriceSwitcher WPF 与内置 Bifrost fork 使用一条仅限当前 Windows 用户访问的双向 Named Pipe，不开放 localhost 管理 API。该版本化会话承载 readiness、原子应用或清除 `RouteSnapshot`、稳定错误状态，以及 sidecar 按 `keyHandle` 向 WPF 请求推理 API key；用户不配置 IPC，raw key 不进入 OMP、命令行或普通网关配置。

## Consequences

WPF 是控制端和凭据事实源，Bifrost fork 是协议数据面；断线、重连或版本不兼容时不得沿用未确认的新路由，也不得自动绕过本地网关。首版只承诺 Windows，不为尚不存在的第二个平台增加 HTTP 控制面。
