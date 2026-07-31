---
status: accepted
---

# 使用固定版本的 Bifrost 小型 fork 作为本地网关

本项目采用固定版本的 Bifrost 小型 fork 作为 OpenAI Responses 协议数据面，首版只承诺 OpenAI Responses，不实现 Chat Completions、Anthropic Messages 或跨协议转换；复用其已由隔离原型验证的 Responses、SSE、tool call/tool result、reasoning、ModelId 透传和请求级连接生命周期。fork 只补充原子 `RouteSnapshot`、Windows 私有 key resolver 与稳定的无活动路由错误；不在 WPF 前另建逐请求 HTTP 控制层，因为该层一旦负责 endpoint、Authorization 和流式连接，就会成为第二个协议网关并重复实现 Bifrost 已有能力。

## Consequences

生产分发必须固定 Bifrost fork 的源码版本与 Windows x64 二进制哈希，并维护上游安全修复和 Responses 回归矩阵。ProviderPriceSwitcher 是唯一用户控制面和凭据事实源；OMP 与普通网关配置均不保存真实推理 API key。sidecar 每个请求开始时按 `keyHandle` 获取推理 API key，请求完成后释放；更新或删除 key 对新请求立即生效。
