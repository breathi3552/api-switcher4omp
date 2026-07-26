# 始终适用的核心约束

- 开始改动前先查现有能力、契约和调用方；能复用就不得平行实现。按任务读取 `.omp/rules/` 中对应 rulebook。
- 保持依赖方向：`Application→Core`；`Adapters→Application+Core`；`Infrastructure→Application+Core`；`App→Application+Adapters+Infrastructure+Core`；不得让 Infrastructure 依赖 Adapters。
- 检查只推荐，不自动切换；只有用户明确操作才改变 OMP 配置。默认禁止访问真实 Provider 网络和使用真实凭据；仅用户明确授权时才可 live probe，且不得使用生产凭据。
- 复杂计费条件无法确定时必须拒绝或明确标记，禁止猜测、静默降级为简单倍率或错误推荐。
- Token、Cookie、密码和其他凭据不得进入设置、快照、日志、备份、异常文本或仓库；只使用安全存储和脱敏诊断。
- clean cutover 仅适用于仓库内源码契约：一次性迁移全部实现、调用方和 runner，删除旧入口、别名、shim 和无效引用。持久化 schema、外部 API、已发布 CLI/扩展等外部契约，必须先定义兼容或迁移策略。
- 显著行为变更必须按影响范围用能证明可观察契约的验证完成闭环；不得以“能编译”替代行为验证。文档-only 不跑代码验证；发布不得默认执行，仅发布任务或用户明确要求时执行。
- 涉及 `.NET` 的构建、runner、烟测或发布操作，必须先读取 `rule://build-release`，并遵循其中的固定 SDK 和顺序。
- UI/WPF 烟测必须使用隔离的临时 data root、OMP root 和工作目录，禁止触碰真实用户数据、凭据或 OMP 配置。

## 按需规则

按任务读取对应 rulebook：架构与依赖读 `rule://architecture`；能力复用读 `rule://reuse`；编码与安全读 `rule://coding`；变更流程读 `rule://change-workflows`；构建发布读 `rule://build-release`。跨领域任务读取全部相关规则，不复制规则全文。
