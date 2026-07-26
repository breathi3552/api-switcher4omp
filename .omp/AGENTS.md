# ProviderPriceSwitcher 会话入口

ProviderPriceSwitcher 是 Windows WPF/.NET 8 工具：查询多个 OMP Provider 的价格，按统一业务规则提供最低价推荐；只有用户明确操作时才切换 OMP 配置并启动 OMP。`AGENTS.md` 是会话入口，会由 OMP 自动注入。

## 最小读取与规则加载

- `context/Project-Overview.md` 是项目事实入口，但不是每次任务都自动展开；仅当任务需要项目事实、能力边界或现状判断时显式读取。
- OMP 启动时扫描 `.omp/rules/` 的规则元数据；正文由模型按需通过 `rule://规则名` 读取。`globs` 只是提示/路径元数据，不是自动加载或自动选择正文的开关；Markdown 链接也不会自动展开。
- 遵循最小读取原则：先定位任务涉及的文件、符号和规则，只读取完成判断所需的范围；跨领域任务再读取全部相关 rulebook，不复制规则全文。
- 发生冲突时，以源码和可复现实验事实优先于概览陈述；`RULES.md` 是始终适用的 sticky 硬约束，详细领域规则不得与其冲突，若有歧义按更严格的安全边界执行。

## 代码地图

- `ProviderPriceSwitcher.Core`：价格、快照、成本、推荐等领域模型与规则。
- `ProviderPriceSwitcher.Application`：跨界面复用的用例、端口和业务编排。
- `ProviderPriceSwitcher.Adapters`：供应商价格与协议适配器。
- `ProviderPriceSwitcher.Infrastructure`：刷新、持久化、凭据、OMP 配置和进程等外部实现。
- `ProviderPriceSwitcher.App`：WPF 组合根、绑定、窗口和用户工作流。

## 按需规则路由

| 任务 | 开始前读取 |
|---|---|
| 分层、依赖方向、契约演进 | `rule://architecture` |
| 查找并复用现有能力、入口或测试 | `rule://reuse` |
| 编码、安全、持久化、日志、测试和 UI 细则 | `rule://coding` |
| 变更步骤、迁移、重构和验证流程 | `rule://change-workflows` |
| .NET 构建、runner、WPF 烟测和发布 | `rule://build-release` |

`.NET` 构建、runner、烟测或发布操作必须先读取 `rule://build-release`。验证命令与矩阵以 `build-release.md` 为准，流程与完成定义以 `change-workflows.md` 为准。

## 事实来源

本文件负责会话入口与导航；短而硬的始终适用约束见 `RULES.md`，详细领域规则以 `.omp/rules/*.md` 为准。项目事实从当前源码、按需读取的 `context/Project-Overview.md` 和适用 rulebook 获取，不从会话记录或临时产物推导。
