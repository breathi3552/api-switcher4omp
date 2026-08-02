---
description: 稳定的分层、依赖方向与契约边界
---

# 架构规则

本文件只规定跨版本仍应成立的架构约束。当前项目结构、文件位置和实现入口以源码与 `AGENTS.md` 导航为准。

## 依赖方向
生产代码必须保持以下方向：

```text
Application -> Core
Adapters -> Application + Core
Infrastructure -> Application + Core
App -> Application + Adapters + Infrastructure + Core
```

- `Infrastructure` 不得依赖 `Adapters` 或 `App`。


- `Core` 不依赖其他生产层或外部环境。
- `Application` 负责可复用用例、端口和编排，不实现 UI、网络、文件、JSON、Windows API 或进程控制。
- `Adapters` 封装外部协议和数据格式，不承担 UI、持久化或进程生命周期。
- `Infrastructure` 实现 Application 端口和外部副作用。
- `App` 负责用户界面、组合根和呈现，不把业务规则下沉到窗口事件中。

不得通过复制代码、反射、静态全局或间接引用绕过依赖方向。

## 边界

- 业务规则放在 Core 或 Application，不放在适配器和 UI 中重复实现。
- 外部协议、文件格式、凭据、进程和系统 API 必须停留在对应适配层。
- 跨层能力通过最小、可注入的契约传递；内层不得依赖外层实现类型。
- 组合根集中创建和连接实现；窗口、用例和基础设施不得各自创建平行依赖图。
- 新增行为先检查已有契约；若现有契约不能表达需求，先扩展正确层的契约，再迁移调用方。

## 契约演进

- 仓库内部的接口变更必须一次性迁移所有实现、调用方和验证，并删除旧入口。
- 已有 JSON 数据、外部 API、跨进程协议和已发布接口属于外部契约；变更时不得直接破坏旧格式，必须先定义兼容或迁移方案。
- 契约不得向内泄露 HTTP、JSON、WPF、Windows 或供应商 DTO；用结构化结果表达状态、失败和取消。
- 任何新增依赖方向、职责越界或兼容路径都必须在变更说明中明确理由和影响。