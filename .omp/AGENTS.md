# ProviderPriceSwitcher 会话入口

ProviderPriceSwitcher 是 Windows WPF/.NET 8 工具：查询多个 OMP Provider 的价格，按统一业务规则提供最低价推荐；只有用户明确操作时才切换 OMP 配置并启动 OMP。

## 项目文档

- `CONTEXT.md`：领域术语、业务不变量、用户目标和产品边界。
- `docs/adr/*.md`：已接受的架构和产品决策。
- `.omp/context/Project-Overview.md`：当前产品事实、业务不变量和已实现能力。
- `.omp/context/UX-Decisions.md`：UX 交互决策，包含已收敛交互和延后运行状态页设计。
- `.omp/context/Future-Work.md`：未实现债务、未来方向和当前非目标。
- `.omp/context/Verification-Register.md`：验证状态、runner 场景和可重跑证据。
- `.omp/prototypes/`：原型调查、运行报告和临时设计材料，不作为正式架构决策。

`.omp/rules/` 仅保存跨版本稳定的架构、安全、复用、变更和验证原则；项目结构、实现入口和当前验证清单不在规则中维护。

源码、工程文件和验证结果是当前实现状态的依据；与概览或文档冲突时，以可复现的源码和实验事实为准。

## 按需规则路由

| 任务                                                                                                     | 开始前读取                     |
| ------------------------------------------------------------------------------------------------------ | ------------------------- |
| 分层、依赖方向、契约演进                                                                                           | `rule://architecture`     |
| 查找并复用现有能力、入口或测试                                                                                        | `rule://reuse`            |
| 编码、安全、持久化、日志、测试和 UI 细则                                                                                 | `rule://coding`           |
| 变更步骤、迁移、重构和验证流程                                                                                        | `rule://change-workflows` |
| .NET 构建、runner、WPF 烟测和发布                                                                               | `rule://build-release`    |
