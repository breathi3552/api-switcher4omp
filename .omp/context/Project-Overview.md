# ProviderPriceSwitcher 项目事实

本文是当前产品目标、已实现能力、业务不变量和明确安全边界的唯一项目事实入口。未实现方向见 [`Future-Work.md`](Future-Work.md)，交互决策见 [`UX-Decisions.md`](UX-Decisions.md)，验证证据见 [`Verification-Register.md`](Verification-Register.md)。

## 项目缘起与用户问题

ProviderPriceSwitcher 是一个面向 OMP 用户的 Windows 桌面工具。不同中转站可能为同一模型提供不同的分组倍率、输入价格、缓存读取价格和输出价格；用户需要在不手工核对多个公开接口、不直接编辑 OMP 配置的情况下，知道当前账户分组下哪个 Provider 更划算，并把已确认的选择安全地用于所有 OMP 实例的后续请求。

工具解决的是“查询并比较，再由用户明确应用活动路由”的问题；价格检查和推荐不改变活动路由，切换只影响新请求，不改道在途请求。当前固定比较模型为 `gpt-5.6-sol`。

## 平台与技术范围

- 目标平台为 Windows 11；主要交互是 WPF 桌面 GUI。
- 生产代码使用 .NET 8，分为 Core、Application、Adapters、Infrastructure 和 App；测试项目以各层 console runner 形式存在。
- Core 保存价格、快照、成本和推荐所需的环境无关领域模型与计算；Application 编排价格检查、设置管理和活动路由应用/恢复；Adapters 封装供应商协议；Infrastructure 提供文件、Windows 凭据、OMP 配置、sidecar 和进程等外部能力；App 负责 WPF 装配与呈现。
- 本地设置和最后成功价格快照位于用户专用应用数据根；OMP 根目录和工作目录由用户维护，测试可注入隔离 data root。

## 核心业务不变量

1. 价格检查由用户发起，逐站隔离失败，精确匹配目标模型、用户确认的当前分组和可用分组交集。
2. 输入、缓存读取和输出价格标准化为每百万 Token 的 `decimal` 金额；缓存读取和未缓存输入是互斥计费桶。
3. 自动推荐只使用本轮检查成功、配置和当前分组匹配、价格有效的站点；同价时优先保留当前 Provider。未验证的低价分组只作提示。
4. 成功检查覆盖最后成功快照；失败保留旧快照并显示失败/旧快照状态。旧快照不能自动参与推荐。
5. 检查只更新本地价格显示与快照，不修改活动路由；只有用户明确应用供应商后才向固定 sidecar 提交原子 `RouteSnapshot`。
6. 活动路由持久化只保存非秘密 `ActiveProviderId`；恢复时供应商必须仍启用且 API key 仍存在并绑定当前分组，否则清空且不自动回退。OMP 接管和进程生命周期属于后续能力。
7. 访问令牌和 Cookie 使用 Windows 当前用户保护的安全存储；不得进入普通设置、快照、日志、错误消息、备份或仓库。凭据绑定不验证真实有效性，真实认证结果由价格探测反映。

- 启动时加载最后成功价格、倍率、来源和检查时间；不因启动自动联网。价格检查可取消并有逐请求超时。
- 支持供应商新增、编辑、删除、启用/禁用；保存当前分组倍率、配置页面地址和最后成功快照；首页展示当前组和最低倍率组，最低组只读。
- 站点编辑器的配置页面地址为空时，首页双击供应商默认打开 `Base URL/keys`；填写路径时拼接到 `Base URL` 后。
- New API、PawsAI、SevnX 和 AIHub 价格适配器已接入，已覆盖已知计费条件；无法可靠标准化的复杂计费拒绝猜测。
- 支持浏览器 access token/Cookie 的绑定、更新、清除和状态展示；凭据在编辑器中独立于价格查询和模型推理字段管理，原文不进入普通设置、快照、日志或 UI 回填。
- 支持每个供应商保存、更新、删除一个模型推理 API key，并绑定一个当前分组；界面只显示脱敏摘要，删除或重命名活动供应商会同步清除应用层和 sidecar 活动路由。
- 固定 Bifrost fork 已作为 Windows x64 sidecar 随应用发布并校验 SHA-256；当前用户私有 Named Pipe 提交不可变 `RouteSnapshot` 并按请求解析 `keyHandle`。OMP 使用固定 `provider-price-switcher` OpenAI Responses Provider，普通配置只含占位凭据。
- 生产 loopback runner 已验证真实 OMP 请求经过 sidecar，以及 Responses 非流式/流式 SSE、tools/tool results、reasoning、ModelId 透传、endpoint/key 隔离、路由切换、无活动路由错误和无残留进程；真实供应商与真实凭据未触及。
- 推荐和成本计算使用固定的 Codex 高缓存默认用量；站内计价单位与人民币统一换算尚未完成。
- 当前 #4 路径不修改 OMP 配置、不启动或重启 OMP；OMP 接管、重复启动和生命周期由后续能力负责。
- 生产 composition root 负责装配凭据、查询端口、repositories、adapter registry、推理 key 用例和活动路由状态，再注入窗口及 ViewModel。

## 文档边界

- 未实现债务、未来方向和当前非目标：[`Future-Work.md`](Future-Work.md)。
- 已收敛交互和延后 UX：[`UX-Decisions.md`](UX-Decisions.md)。
- runner、验证状态和可重跑证据：[`Verification-Register.md`](Verification-Register.md)。
- 架构决策理由：[`../../docs/adr/`](../../docs/adr/)。
