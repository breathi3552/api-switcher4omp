# Verification Register and Runner Scenario Matrix


> **共享实际验证证据（2026-08-04）。** `powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng/Verify.ps1 -Impact CrossLayer -AllRunners` 使用固定 SDK `8.0.423` 完成静态清单与依赖检查、solution build（0 警告、0 错误）、格式检查和全部 runner。Adapters runner 证明 AIHub、SevnX 在配置存在旧正倍率时仍采用最新成功响应并输出自动倍率；App runner 通过真实 `MainViewModel.CheckCommand`、`SiteEditorViewModel.ProbeCommand`、AIHub adapter、合成 HTTP handler 和临时 settings/snapshot root，覆盖成功覆盖旧倍率、当前组也是最低组、失败或缺少当前组时保留旧倍率，以及选择探测候选后采用对应倍率。
>
> `powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng/Publish.ps1` 成功生成并验收 `artifacts/publish/framework-dependent`（exe 173568 bytes）和 `artifacts/publish/self-contained`（exe 106183996 bytes）；两个阶段均验证可执行文件存在且发布 sidecar 与 manifest SHA-256 一致。全部证据只使用合成凭据、内存/loopback HTTP 和临时 data root；未访问真实 Provider、真实凭据、真实用户配置或真实 OMP root。

## Quality debt register

### QD-07-001 — LocalAppSettings 公共集合可变性

- **id:** `QD-07-001`
- **owner:** `Application maintainers`
- **status:** `closed`
- **priority:** `P0`
- **evidence:** `LocalAppSettings.cs` 的当前实现与 Application runner 的可观察验证证据。
- **DoD:** 已由共享实际验证证据闭环：Application runner 与隔离 UI 场景证明公共集合不可原地修改、`with` 更新不污染旧实例，并完成兼容 JSON 往返。
- **scope:** Application settings model、settings 用例、App 站点/设置展示和受影响 runner。
- **dependencies:** `ISettingsRepository` JSON 边界适配；Application/App runner。
- **last_verified:** `2026-07-26`（已验证）

### QD-07-002 — PricingRefreshService 持久化职责耦合

- **id:** `QD-07-002`
- **owner:** `Application maintainers`
- **status:** `closed`
- **priority:** `P0`
- **evidence:** `PricingRefreshService.cs` 的当前实现与 Refresh/App runner 的可观察验证证据。
- **DoD:** 已由共享实际验证证据闭环：Refresh runner 与 App runner 证明刷新服务不持有或转发快照 repository，旧成员零引用；快照读取/保存由既有 Application 端口或用例承接，成功、失败、超时和取消均无未声明持久化副作用。
- **scope:** Application refresh orchestration、pricing-check/site-management callers、composition root and runners。
- **dependencies:** `IPricingSnapshotRepository` caller migration；Refresh/App runner。
- **last_verified:** `2026-07-26`（已验证）

### QD-07-003 — Composition root 与 UI 外部实现边界

- **id:** `QD-07-003`
- **owner:** `App maintainers`
- **status:** `closed`
- **priority:** `P1`
- **evidence:** 装配边界规范见 [`architecture.md`](../rules/architecture.md)；当前源码、App runner 与隔离 WPF smoke 的可观察验证证据。
- **DoD:** 已由共享实际验证证据闭环：App runner 与隔离 WPF smoke 证明 repository/端口由唯一 composition root 注入，窗口只读展示且不直接 new Infrastructure，受影响调用方已迁移。
- **scope:** `App.xaml.cs`、MainWindow view models/dialogs、Application ports and App runner.
- **dependencies:** QD-07-001；QD-07-002。
- **last_verified:** `2026-07-26`（已验证）

### QD-07-004 — 凭据隔离与错误脱敏的持续回归风险

- **id:** `QD-07-004`
- **owner:** `Infrastructure and App maintainers`
- **status:** `closed`
- **priority:** `P0`
- **evidence:** 安全规范见 [`coding.md`](../rules/coding.md)；既有凭据与 UI 事实入口见 [`Project-Overview.md`](Project-Overview.md)。
- **DoD:** 已由共享实际验证证据闭环：Infrastructure runner 与 App 隔离 smoke 使用 synthetic secret 验证 token/Cookie 不进入 settings、snapshot、日志、异常、备份或 UI 回填；真实凭据场景明确未执行。
- **scope:** Windows credential boundary, persistence/logging/error mapping, SiteEditor presentation.
- **dependencies:** QD-07-003；隔离 data root。
- **last_verified:** `2026-07-26`（已验证）

### QD-07-005 — Settings/snapshot 旧 JSON 兼容往返证据

- **id:** `QD-07-005`
- **owner:** `Infrastructure maintainers`
- **status:** `closed`
- **priority:** `P0`
- **evidence:** 持久化入口见 [`JsonPersistence.cs`](../../ProviderPriceSwitcher.Infrastructure/JsonPersistence.cs)；Infrastructure runner 的兼容性验证证据。
- **DoD:** 已由共享实际验证证据闭环：Infrastructure runner 在临时目录读取旧 settings/snapshot JSON，完成只读模型更新及写回，并断言字段名、数组形状、顺序、默认值、未知字段策略和无部分写入。
- **scope:** JSON persistence adapters and settings/snapshot schema boundary; no schema version change.
- **dependencies:** QD-07-001；existing migration/default behavior；Infrastructure runner。
- **last_verified:** `2026-07-26`（已验证）

### QD-07-006 — 治理事实入口与验证闭环

- **id:** `QD-07-006`
- **owner:** `Repository maintainers`
- **status:** `closed`
- **priority:** `P1`
- **evidence:** 本文件为唯一登记入口；验证分级规范见 [`build-release.md`](../rules/build-release.md)；文档字段、链接与验证证据已回填。
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
- **DoD:** 运行 App runner（`$dotnet run --project ProviderPriceSwitcher.App.Tests/ProviderPriceSwitcher.App.Tests.csproj`）并执行隔离 WPF smoke，断言推荐供应商只进入待应用选择、活动供应商不变；底部目标供应商/应用供应商与 OMP 工作目录/启动 OMP 两组动作拥有独立状态；网关和接管状态点只读；编辑器包含基础、价格查询、模型推理三段，单一可编辑分组 ComboBox 在回填、探测刷新、焦点变化和重复探测中保留当前值；凭据摘要、推理 key 更新/删除/取消均不泄露原文；使用临时 roots、合成 settings、fake/loopback adapter 并正常关闭无残留。
- **scope:** App composition, MainViewModel/SitesDialog/SiteEditorDialog, Issue #6 isolated WPF behavior.
- **dependencies:** QD-07-001; QD-07-002; QD-07-003; QD-07-004
- **last_verified:** `2026-08-09`（Issue #6 实现后验证）

## Issue #5 takeover and repeat-launch evidence

- **status:** verified in isolation
- **last_verified:** `2026-08-09`
- **Application runner:** `OmpStartupUseCase` owns startup takeover checks, target-port migration and effective-port settings persistence; `OmpLaunchUseCase` proves an un-taken-over OMP cannot be bypassed, launch cancellation performs no write or process launch, and `OmpProcessFailureKind` reaches the Application outcome without collapsing to a boolean.
- **OMP configuration runner:** takeover requires every managed direct `modelRoles` and `agentModelOverrides` reference to use fixed `provider-price-switcher`; partial fast/override drift returns `NotTakenOver`. The runner also proves in-progress configuration reads honor cancellation, the sidecar endpoint parser stops at sibling providers and top-level mappings, no real provider or API key is written, and a complete backup created before a simulated replacement failure remains part of newest-five retention.
- **OMP process and Infrastructure runners:** with an existing-process hint, fake process launches from the same and a different working directory each create a new attempt. The Application `OmpRootDirectory` is translated by `AppPathDefaults` to the managed agent directory at the process boundary. The real Windows OMP/sidecar smoke holds the first isolated OMP instance active, then starts distinct second and third PIDs from the same and a different temporary working directory; all three reach loopback and exit successfully without changing `ActiveRoute`.
- **App runner:** WPF main-page launch/takeover interaction proves cancel and setup-and-start behavior, repeated launch is not coupled to routing, and the active supplier remains unchanged.
- **Cross-layer command:** `powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng/Verify.ps1 -Impact CrossLayer -AllRunners` 于 `2026-08-09` 最终通过 static manifest/dependency checks、solution build（0 warnings / 0 errors）、format verification 和全部 8 个注册 runner。
- **Publish command:** `powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng/Publish.ps1` 于 `2026-08-09` 成功生成 framework-dependent 与 self-contained 两个 win-x64 产物，并分别校验可执行文件和 sidecar manifest hash。
- **Final fixed-base dual-axis review:** 针对 `69b64086a20d616d304949c001a0438071f3018c...HEAD` 的 Standards 与 Spec 审查均无阻塞发现。

## Issue #6 homepage and editor evidence

- **status:** implementation complete; isolated App/Infrastructure evidence, final CrossLayer, dual-axis review and release evidence complete
- **scope:** MainWindow bottom action layout, recommendation pending selection, OMP working-directory selection, status dots, SiteEditorDialog sections, credential summaries, editable current-group candidates and inference key lifecycle.
- **evidence:** App runner covers the WPF seam with temporary data/root and synthetic credentials, including current/minimum row projection, pending recommendation versus unchanged active route, independent apply/start busy states, read-only status points, ComboBox backfill/focus/repeated-probe retention, short and long credential summaries, inference-key close/cancel/empty-update/delete behavior, and takeover prompt behavior. Infrastructure runner covers storage-boundary token/Cookie summaries and secret exclusion. Real providers, credentials and user configuration are not touched.

## Status update protocol

 - 既有治理工作的六项债务与八个 runner 均依据已记录证据关闭；2026-08-03 CrossLayer runner 额外通过 Application `ApplyActiveRouteUseCase` 与真实 sidecar 的原子 Route Snapshot 应用/持久化路径。
 - 未执行的外部边界包括真实 Provider、真实凭据和真实用户 data/OMP root；loopback 已覆盖真实 OMP 请求、无活动路由错误、路由切换和在途请求快照。Issue #4 路径不负责 OMP 接管或进程生命周期；Issue #5 的接管、端口、配置备份和重复启动证据见上节。
> **底部证据索引（2026-08-09）。** 共享实际验证证据见文件顶部；本底部仅重复证据入口，避免各条目复制长文本。
