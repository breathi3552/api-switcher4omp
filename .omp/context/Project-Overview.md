# ProviderPriceSwitcher 项目概览

## 项目缘起与用户问题

ProviderPriceSwitcher 是一个面向 OMP 用户的 Windows 桌面工具。不同中转站可能为同一模型提供不同的分组倍率、输入价格、缓存读取价格和输出价格；用户需要在不手工核对多个公开接口、不直接编辑 OMP 配置的情况下，知道当前账户分组下哪个 Provider 更划算，并把已确认的选择安全地用于新的 OMP 进程。

工具解决的是“查询并比较，再由用户决定是否切换”的问题，而不是代替用户管理账户权限或自动改变正在使用的 OMP 会话。当前固定比较模型为 `gpt-5.6-sol`。

## 平台与技术范围

- 目标平台为 Windows 11；主要交互是 WPF 桌面 GUI，不以命令行为主要入口。
- 生产代码使用 .NET 8，解决方案由 Core、Application、Adapters、Infrastructure 和 App 五个项目组成；测试项目以各层 console runner 形式存在。
- Core 保存价格、快照、成本和推荐所需的环境无关领域模型与计算；Application 编排价格检查、设置管理和 OMP 切换/启动用例；Adapters 封装供应商协议；Infrastructure 提供文件、Windows 凭据、OMP 配置和进程等外部能力；App 负责 WPF 装配与呈现。
- 站点通过适配器注册表按 `SiteType` 接入。目前代码包含 New API、PawsAI 和需要认证的 Sub2API 价格适配器。适配器将认证失败、超时、请求错误、无效响应、模型/分组缺失和不支持的计费规则转换为结构化结果或失败。
- 本地设置和最后成功价格快照位于用户专用的应用数据根（测试可注入隔离 data root）；OMP 根目录和常用工作目录由用户维护。

## 核心实现目标与业务不变量

1. 对启用站点手动发起并发价格检查，逐站隔离失败；精确匹配目标模型、用户确认的 `CurrentGroup` 和可用分组交集。
2. 将输入、缓存读取、输出价格标准化为每百万 Token 的 `decimal` 金额，并按固定默认用量配置计算预计成本。缓存读取 Token 与未缓存输入 Token 是互斥计费桶，不重复计算。
3. 只把本轮检查成功、配置和当前分组匹配、价格有效的站点纳入自动推荐；同价时优先保留当前 Provider。更低分组只作价格提示，不把未验证的账户权限当成事实，也不按该分组替代当前分组计算。
4. 成功检查覆盖该站点的最后成功快照；失败时保留旧快照并显示失败/旧快照状态、时间和原因。旧快照不能成为自动推荐依据；用户是否继续使用它必须是明确的手动选择并承担相应风险。
5. 检查只读取和更新本地价格显示与快照，不修改 OMP 配置。只有用户明确执行“切换并启动 OMP”后，才验证并替换配置中的合法 Provider 引用，并在选定工作目录启动 OMP。
6. OMP 配置修改先备份再原子替换，写入失败时保持原文件；配置切换成功和进程启动是独立结果，启动失败不静默回滚已经完成的切换。
7. 访问令牌和 Cookie 使用 Windows 当前用户保护的安全存储；不得进入普通设置、价格快照、日志、错误消息、备份或仓库。凭据绑定动作本身不验证凭据；真实有效性只在价格探测时由 adapter 的认证结果反映。

## 当前已实现能力

- 以下是当前源码与已完成 runner/隔离验证所支持的能力；计划 07 已完成并已按 [`Quality-Debt-Register.md`](Quality-Debt-Register.md) 关闭。

- 首页启动时加载并展示已保存的最后成功价格、倍率、来源和检查时间；不会因为启动而发起网络检查。价格检查由用户点击触发，可取消，并设有逐请求超时。
- 支持站点新增、编辑、删除、启用/禁用；保存站点设置、当前分组倍率、可配置的配置 API 地址（默认 `/keys`）及最后成功价格快照。当前首页展示当前分组和最低倍率分组；双击当前组行会用系统默认浏览器打开该站点的配置 API 页面，最低分组不触发导航。最低分组仅供用户手动选择，不参与自动推荐，也不声称账户已具备该分组权限。
- New API 公开价格查询、PawsAI 价格文件解析和 Sub2API 鉴权价格查询已经接入；支持浏览器 access token/Cookie 的绑定、更新、清除和状态展示。绑定动作不会验证凭据；价格探测时才由 adapter 认证结果反映真实有效性。已处理真实计费表达式中的已知条件，并对无法可靠标准化的复杂条件拒绝猜测。
- 推荐和成本计算使用固定的“Codex 高缓存”默认用量：未缓存输入 200,000、缓存读取 800,000、输出 100,000 Token。站内计价单位与人民币统一换算尚未完成。
- 可从 OMP `config.yml` 的 `modelRoles.default` 识别当前 Provider；切换服务统一处理 `modelRoles` 与 `task.agentModelOverrides` 下的直接 provider/model 标量引用，保留模型名，并通过与资源管理器右键菜单同源的 Windows Terminal/PowerShell 启动形态在选定目录启动 OMP。已检测到已有 OMP 进程时不会强杀旧会话。
- Core、Adapters、Application、Infrastructure、OMP 配置、OMP 进程、Refresh 和 App 八个契约 runner 已通过；计划 07 的 settings 不可变性、刷新职责、JSON compatibility、调用方迁移和隔离 WPF smoke 证据，以 [`Quality-Debt-Register.md`](Quality-Debt-Register.md) 的矩阵和共享证据为唯一入口。
- `App.xaml.cs` 负责生产 composition root；凭据、查询端口、repositories、adapter registry 和 Application 用例由启动装配，再注入窗口及 ViewModel。凭据原文不作为普通 settings/snapshot/logging 数据。

## 质量路线

质量路线不是产品功能清单，专注于可验证的工程质量：

- 计划 07 已闭环：Application settings 公共集合为不可变读取契约，调用方通过新集合/`with` 更新；`PricingRefreshService` 仅负责价格探测、结果合并与推荐编排，不承担快照持久化；既有 settings/snapshot JSON 外部字段、数组形状、迁移、默认值、未知字段策略和原子写入保持兼容。
- `PricingRefreshService` 不持有 settings/snapshot repository；`PricingCheckUseCase` 在用例边界读取并保存 snapshot，并在自动倍率变化时保存新 settings。settings 模型是不可变读取值，更新由 Application 用例产生新值。OMP 配置仍只有用户明确执行“切换并启动 OMP”时才修改。
- 八 runner、静态旧 API 零引用、JSON 往返和隔离 WPF smoke 均已验证；详情与状态只见 [`Quality-Debt-Register.md`](Quality-Debt-Register.md)。

## 产品路线

产品路线只描述用户能力方向，不把治理债务或计划工作标为现有产品能力：

- 为每个站点/分组配置独立用量，按分组计算成本；补充币种、站内额度到人民币的换算，并按统一人民币成本推荐。
- 保存并浏览目标模型的完整分组价格；增加余额展示、日志覆盖范围和缓存命中率分析。
- 对供应商支持的接口增加账户分组实际验证；补充 Token 过期、401/403、Cloudflare、模型或分组下架等真实环境验收。
- 增加价格历史与趋势、延迟和健康度等只读分析；在明确用户授权后再考虑定时检查、通知、托盘和多模型加权。

## 明确非目标与安全边界

- 不自动刷新、不开机联网检查、不定时执行，不用后台任务替用户选择 Provider；`PricingRefreshService` 无持久化职责，持久化仅在显式 Application 用例边界发生；历史快照没有按时间 TTL 自动过期机制。
- OMP 配置修改仍仅由用户明确操作触发；不通过 `models.yml` 做候选过滤，也不把公开列出的分组当作 API Key 权限证明；Provider 目标来自应用内维护的站点配置。
- 不进行全文件字符串替换，不修改 OMP 规定配置域之外的文本；不自动回滚已成功的配置切换，不强制终止已有 OMP 进程。
- 当前不是账户管理、余额结算、充值汇率核算、日志核账或实际权限审计工具；不会把 API 凭据写入普通配置或日志，也不直接拿用户配置做破坏性验收。
- 当前只比较 `gpt-5.6-sol`，不把未实现的多模型、价格历史、定时检查或稳定性评分描述为现有能力。

## 质量与能力事实入口

- 质量债务、八 runner 场景、状态和可重跑证据：[`Quality-Debt-Register.md`](Quality-Debt-Register.md)。
- 架构和依赖规范：[`architecture.md`](../rules/architecture.md)。
- 代码、安全、持久化和 UI 规范：[`coding.md`](../rules/coding.md)。
- 能力复用入口：[`reuse.md`](../rules/reuse.md)。
- 变更流程：[`change-workflows.md`](../rules/change-workflows.md)。
- 构建、runner、隔离 smoke 和发布验收：[`build-release.md`](../rules/build-release.md)。
