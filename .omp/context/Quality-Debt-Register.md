# Quality Debt Register and Runner Scenario Matrix

> **Canonical governance entry.** 本文件是仓库唯一的质量债务登记与八 runner 场景矩阵；计划 07 已完成代码、兼容性、runner、隔离 WPF smoke 与文档治理闭环，所有状态以以下实际证据为准。
>
> **共享实际验证证据（2026-07-26）。** `eng/Verify.ps1 -Impact CrossLayer -AllRunners` 使用固定 SDK `8.0.423` 完成 build（0 警告、0 错误）、format 与八 runner passed；静态搜索四个旧 refresh API 零引用。Application runner 覆盖 settings 公共数组不可变、`with` 更新不污染旧实例及调用方数组后续突变；Infrastructure runner 覆盖旧 `settings`/`ompWorkingDirectories` 数组读取、字段/顺序/形状往返、未知字段既有 ignore-on-save、迁移、损坏输入与原子写入；Refresh runner 证明 refresh 无持久化职责。独立 WPF smoke 使用临时 data/OMP/working root 和空合成 sites，窗口标题出现，WM_CLOSE 后 exit 0，隔离日志 1 份、无残留，临时目录删除。文档字段检查 14 条、9 字段完整、27 链接全部存在；重复链接是多个条目对同一 canonical 事实入口的合法引用。未访问真实 Provider、凭据、用户 root 或 OMP，未执行 publish。
>
> 证据使用合成数据、fake/loopback 和临时隔离 root；不记录真实用户路径、凭据、token、Cookie、完整响应或生产配置。

## Quality debt register

### QD-07-001 — LocalAppSettings 公共集合可变性

- **id:** `QD-07-001`
- **owner:** `Application maintainers`
- **status:** `closed`
- **priority:** `P0`
- **evidence:** 计划 07 的目标与迁移约束见 [`07-Application模型与文档治理收敛.md`](../plans/07-Application模型与文档治理收敛.md)；实现入口见 [`LocalAppSettings.cs`](../../ProviderPriceSwitcher.Application/LocalAppSettings.cs)。
- **DoD:** 已由共享实际验证证据闭环：Application runner 与隔离 UI 场景证明公共集合不可原地修改、`with` 更新不污染旧实例，并完成兼容 JSON 往返。
- **scope:** Application settings model、settings 用例、App 站点/设置展示和受影响 runner。
- **dependencies:** 计划 07 代码迁移；`ISettingsRepository` JSON 边界适配；Application/App runner。
- **last_verified:** `2026-07-26`（已验证）

### QD-07-002 — PricingRefreshService 持久化职责耦合

- **id:** `QD-07-002`
- **owner:** `Application maintainers`
- **status:** `closed`
- **priority:** `P0`
- **evidence:** 计划 07 的刷新职责收敛要求见 [`07-Application模型与文档治理收敛.md`](../plans/07-Application模型与文档治理收敛.md)；实现入口见 [`PricingRefreshService.cs`](../../ProviderPriceSwitcher.Application/PricingRefreshService.cs)。
- **DoD:** 已由共享实际验证证据闭环：Refresh runner 与 App runner 证明刷新服务不持有或转发快照 repository，旧成员零引用；快照读取/保存由既有 Application 端口或用例承接，成功、失败、超时和取消均无未声明持久化副作用。
- **scope:** Application refresh orchestration、pricing-check/site-management callers、composition root and runners。
- **dependencies:** 计划 03 composition root；`IPricingSnapshotRepository` caller migration；Refresh/App runner。
- **last_verified:** `2026-07-26`（已验证）

### QD-07-003 — Composition root 与 UI 外部实现边界

- **id:** `QD-07-003`
- **owner:** `App maintainers`
- **status:** `closed`
- **priority:** `P1`
- **evidence:** 装配边界规范见 [`architecture.md`](../rules/architecture.md)；计划 07 要求见 [`07-Application模型与文档治理收敛.md`](../plans/07-Application模型与文档治理收敛.md)。
- **DoD:** 已由共享实际验证证据闭环：App runner 与隔离 WPF smoke 证明 repository/端口由唯一 composition root 注入，窗口只读展示且不直接 new Infrastructure，受影响调用方已迁移。
- **scope:** `App.xaml.cs`、MainWindow view models/dialogs、Application ports and App runner.
- **dependencies:** 计划 03 验收；QD-07-001；QD-07-002。
- **last_verified:** `2026-07-26`（已验证）

### QD-07-004 — 凭据隔离与错误脱敏的持续回归风险

- **id:** `QD-07-004`
- **owner:** `Infrastructure and App maintainers`
- **status:** `closed`
- **priority:** `P0`
- **evidence:** 安全规范见 [`coding.md`](../rules/coding.md)；既有凭据与 UI 事实入口见 [`Project-Overview.md`](Project-Overview.md)。
- **DoD:** 已由共享实际验证证据闭环：Infrastructure runner 与 App 隔离 smoke 使用 synthetic secret 验证 token/Cookie 不进入 settings、snapshot、日志、异常、备份或 UI 回填；真实凭据场景明确未执行。
- **scope:** Windows credential boundary, persistence/logging/error mapping, SiteEditor presentation.
- **dependencies:** 计划 01/02/05 的既有安全事实；QD-07-003；隔离 data root。
- **last_verified:** `2026-07-26`（已验证）

### QD-07-005 — Settings/snapshot 旧 JSON 兼容往返证据

- **id:** `QD-07-005`
- **owner:** `Infrastructure maintainers`
- **status:** `closed`
- **priority:** `P0`
- **evidence:** 持久化入口见 [`JsonPersistence.cs`](../../ProviderPriceSwitcher.Infrastructure/JsonPersistence.cs)；兼容约束见 [`07-Application模型与文档治理收敛.md`](../plans/07-Application模型与文档治理收敛.md)。
- **DoD:** 已由共享实际验证证据闭环：Infrastructure runner 在临时目录读取旧 settings/snapshot JSON，完成只读模型更新及写回，并断言字段名、数组形状、顺序、默认值、未知字段策略和无部分写入。
- **scope:** JSON persistence adapters and settings/snapshot schema boundary; no schema version change.
- **dependencies:** QD-07-001; existing migration/default behavior; Infrastructure runner.
- **last_verified:** `2026-07-26`（已验证）

### QD-07-006 — 治理事实入口与验证闭环

- **id:** `QD-07-006`
- **owner:** `Repository maintainers`
- **status:** `closed`
- **priority:** `P1`
- **evidence:** 本文件为唯一登记入口；验证分级规范见 [`build-release.md`](../rules/build-release.md)；计划 07 文档验收见 [`07-Application模型与文档治理收敛.md`](../plans/07-Application模型与文档治理收敛.md)。
- **DoD:** 已由共享实际验证证据闭环：文档字段/链接检查与逐条审阅确认固定字段完整、相对链接可解析，且源码、八 runner、JSON 与隔离 smoke 证据已回填。
- **scope:** canonical register, Project Overview route separation, minimal rulebook links.
- **dependencies:** QD-07-001..005；代码执行者与主代理的最终验证证据。
- **last_verified:** `2026-07-26`（已验证）

## Eight runner scenario matrix

每个场景均要求 fake/loopback、合成输入和临时 data/OMP/work root；八个场景均已按共享实际验证证据完成，禁止真实 Provider、真实凭据或真实用户配置。

### Core runner

- **id:** `RUN-CORE-07-001`
- **owner:** `Core maintainers`
- **status:** `closed`
- **priority:** `P1`
- **evidence:** [`Core runner`](../../ProviderPriceSwitcher.Core.Tests/Program.cs)
- **DoD:** 运行 Core runner（`$dotnet run --project ProviderPriceSwitcher.Core.Tests/ProviderPriceSwitcher.Core.Tests.csproj`），确认既有成本、快照有效性和推荐边界不因模型/刷新职责迁移改变；记录 pass 输出。
- **scope:** Core domain regression (unaffected baseline).
- **dependencies:** QD-07-002
- **last_verified:** `2026-07-26`（已验证）

### Adapters runner

- **id:** `RUN-ADAPTERS-07-001`
- **owner:** `Adapters maintainers`
- **status:** `closed`
- **priority:** `P1`
- **evidence:** [`Adapters runner`](../../ProviderPriceSwitcher.Adapters.Tests/Program.cs)
- **DoD:** 运行 Adapters runner（`$dotnet run --project ProviderPriceSwitcher.Adapters.Tests/ProviderPriceSwitcher.Adapters.Tests.csproj`），使用 fake/loopback adapter registry 覆盖既有协议成功、失败、超时和复杂计费拒绝场景；记录无真实出网的 pass 输出。
- **scope:** Adapter contract regression (unaffected baseline).
- **dependencies:** QD-07-004
- **last_verified:** `2026-07-26`（已验证）

### Application runner

- **id:** `RUN-APPLICATION-07-001`
- **owner:** `Application maintainers`
- **status:** `closed`
- **priority:** `P0`
- **evidence:** [`Application runner`](../../ProviderPriceSwitcher.Application.Tests/Program.cs)
- **DoD:** 运行 Application runner（`$dotnet run --project ProviderPriceSwitcher.Application.Tests/ProviderPriceSwitcher.Application.Tests.csproj`），断言 settings 公共集合不可变、`with` 新集合不污染旧值；快照由用例/端口保存，刷新服务无 repository 转发；覆盖保存次数、失败、取消和空缺省设置。
- **scope:** Application model, use cases, refresh caller boundary.
- **dependencies:** QD-07-001; QD-07-002
- **last_verified:** `2026-07-26`（已验证）

### Infrastructure runner

- **id:** `RUN-INFRASTRUCTURE-07-001`
- **owner:** `Infrastructure maintainers`
- **status:** `closed`
- **priority:** `P0`
- **evidence:** [`Infrastructure runner`](../../ProviderPriceSwitcher.Infrastructure.Tests/Program.cs)
- **DoD:** 运行 Infrastructure runner（`$dotnet run --project ProviderPriceSwitcher.Infrastructure.Tests/ProviderPriceSwitcher.Infrastructure.Tests.csproj`），在临时目录验证旧 JSON 读取、数组往返、未知字段/默认值、原子写入与失败无部分写入；断言合成 secret 不出现在 JSON/日志/异常。
- **scope:** Settings/snapshot persistence and security boundary.
- **dependencies:** QD-07-001; QD-07-004; QD-07-005
- **last_verified:** `2026-07-26`（已验证）

### OmpConfig runner

- **id:** `RUN-OMPCONFIG-07-001`
- **owner:** `Infrastructure maintainers`
- **status:** `closed`
- **priority:** `P1`
- **evidence:** [`OmpConfig runner`](../../ProviderPriceSwitcher.OmpConfig.Tests/Program.cs)
- **DoD:** 运行 OmpConfig runner（`$dotnet run --project ProviderPriceSwitcher.OmpConfig.Tests/ProviderPriceSwitcher.OmpConfig.Tests.csproj`），使用唯一临时 OMP root 和 sentinel 验证配置读取/原子切换边界未被本次迁移扩大；不访问真实 OMP 配置。
- **scope:** OMP configuration regression (unaffected baseline).
- **dependencies:** QD-07-003
- **last_verified:** `2026-07-26`（已验证）

### OmpProcess runner

- **id:** `RUN-OMPPROCESS-07-001`
- **owner:** `Infrastructure maintainers`
- **status:** `closed`
- **priority:** `P1`
- **evidence:** [`OmpProcess runner`](../../ProviderPriceSwitcher.OmpProcess.Tests/Program.cs)
- **DoD:** 运行 OmpProcess runner（`$dotnet run --project ProviderPriceSwitcher.OmpProcess.Tests/ProviderPriceSwitcher.OmpProcess.Tests.csproj`），使用 fake/loopback process 和临时工作目录验证显式启动边界、正常关闭和无残留进程；不绑定真实 OMP。
- **scope:** OMP process lifecycle regression (unaffected baseline).
- **dependencies:** QD-07-003
- **last_verified:** `2026-07-26`（已验证）

### Refresh runner

- **id:** `RUN-REFRESH-07-001`
- **owner:** `Application maintainers`
- **status:** `closed`
- **priority:** `P0`
- **evidence:** [`Refresh runner`](../../ProviderPriceSwitcher.Refresh.Tests/Program.cs)
- **DoD:** 运行 Refresh runner（`$dotnet run --project ProviderPriceSwitcher.Refresh.Tests/ProviderPriceSwitcher.Refresh.Tests.csproj`），使用 fake/loopback registry 覆盖刷新成功、失败保留旧快照、超时、取消、推荐边界，并断言 refresh service 不直接 LoadAll/SaveAll；快照保存只由 caller port 完成。
- **scope:** Pricing refresh orchestration and persistence side-effect boundary.
- **dependencies:** QD-07-002; QD-07-005
- **last_verified:** `2026-07-26`（已验证）

### App runner

- **id:** `RUN-APP-07-001`
- **owner:** `App maintainers`
- **status:** `closed`
- **priority:** `P0`
- **evidence:** [`App runner`](../../ProviderPriceSwitcher.App.Tests/Program.cs)
- **DoD:** 运行 App runner（`$dotnet run --project ProviderPriceSwitcher.App.Tests/ProviderPriceSwitcher.App.Tests.csproj`）并执行隔离 WPF smoke，断言 composition root 注入 repositories/ports、窗口只读 settings/snapshot 展示、旧 refresh API 零引用和隔离启动路径；使用临时 roots、合成 settings、fake/loopback adapter 并正常关闭无残留。
- **scope:** App composition, MainViewModel/SitesDialog/SiteEditorDialog, isolated WPF behavior.
- **dependencies:** QD-07-001; QD-07-002; QD-07-003; QD-07-004
- **last_verified:** `2026-07-26`（已验证）

## Status update protocol

- 计划 07 的六项债务与八个 runner 均已依据共享实际验证证据关闭；后续状态变更必须以新的可观察源码、runner、兼容性或隔离场景证据为准。
- 未执行的外部边界仅包括真实 Provider、真实凭据、真实用户 data/OMP root 与正式 publish；这些不属于本次仓库闭环，也未被伪称为已验证。
- 本文件不得新增第二份债务登记或 runner 矩阵；规则文件只引用本文件。

> **底部证据索引（2026-07-26）。** 共享实际验证证据见文件顶部；本底部仅重复证据入口，避免各条目复制长文本。
