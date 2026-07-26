---
description: 现有能力、契约入口与复用门槛索引
globs: ["**/*.cs"]
---

# 能力复用目录

本目录是 ProviderPriceSwitcher 当前代码的复用索引。开发者在增加行为前，MUST 先按“需求→必须复用的契约/实现→禁止替代做法”查找已有入口；只有在现有入口无法表达需求时，才可按本文“新增实现门槛”扩展。本文只描述已经存在并可从源码、项目引用或 runner 验证的能力，不替代各模块的详细设计。验证范围与命令以 [build-release.md](build-release.md) 的影响分级矩阵为唯一来源。

## 使用规则与快速索引

| 需求 | 必须查找/复用 | 禁止平行实现 |
|---|---|---|
| 增加价格站点类型 | `PricingAdapterDescriptor`、`IPricingAdapter`、`PricingAdapterRegistry`（`ProviderPriceSwitcher.Application`）及 `ProviderPriceSwitcher.Adapters` 中现有 adapter | 在 UI、刷新服务或基础设施中维护站点类型字典；按字符串散落分支 |
| 手动检查价格并按实际变化保存自动倍率 | `PricingCheckUseCase` | 在 `MainViewModel` 或窗口中直接刷新、计算和保存 |
| 单站探测、超时和取消 | `PricingProbeUseCase` | 在窗口、adapter 或 `PricingRefreshService` 复制 timeout/probe 编排 |
| 站点新增、编辑、重命名、启停、删除 | `SiteManagementUseCase` | 对 JSON repository 直接写站点列表；在 Dialog code-behind 编排业务 |
| 设置目录保存与规范化 | `SettingsUseCase`、`ISettingsRepository` | 在设置窗口自行去重、选择默认项或直接写文件 |
| OMP 配置切换并启动 | `SwitchAndStartUseCase`、`IOmpConfigurationService`、`IOmpProcessLauncher` | UI 直接调用 `OmpConfigurationSwitcher`/`OmpProcessService`；自行决定保存、启动顺序或失败回滚 |
| 设置/价格快照持久化 | `ISettingsRepository`、`IPricingSnapshotRepository` 及 Infrastructure 的 JSON 实现 | 在 Application 或 UI 依赖文件名、JsonSerializer 或自建 repository |
| OMP 根目录和配置路径 | `IAppPathDefaults`、`AppPathDefaults` | 在用例、UI 或 adapter 拼接 `agent/config.yml`；新增第二套默认根目录 |
| 访问凭据 | Core 的 `ISiteCredentialStore`、Infrastructure 的 `WindowsSiteCredentialStore` | 将 token/Cookie 放进 settings、snapshot、日志、异常文本或普通 JSON；在窗口内部 `new` 存储实现 |
| 本地日志 | `Microsoft.Extensions.Logging.ILogger<T>`、Infrastructure 的 `RollingFileLoggerProvider` | `Console.WriteLine` 代替结构化日志；记录完整 settings、credential、Authorization/Cookie 或响应 body |
| 用户提示和确认 | App 的 `IUserNotificationService`、`WpfUserNotificationService` | 在 ViewModel/窗口静态调用 `MessageBox`；为每个窗口复制提示封装 |
| 同步按钮命令 | App 的 `RelayCommand` | 每个窗口自定义 `ICommand` 实现或在 code-behind 写业务事件 |
| 异步按钮命令、异常和取消 | App 的 `AsyncCommand` | `async void` 业务方法；重复设置 busy 状态、重复错误通知或吞掉异常 |
| WPF 共享样式 | `ProviderPriceSwitcher.App/Styles.xaml`，由 `App.xaml` 合并 | 每个窗口重复定义 Button/TextBlock/DataGrid 或表单样式；新增通用 UI framework/UserControl 仅为复用样式 |
| 隔离运行数据 | App 的 `--data-root` 参数及 settings/snapshot/log root 传递 | 测试或烟测写入真实 LocalAppData 或 OMP 配置；只替换 settings root 而遗漏其他数据；在当前凭据债务修复前执行绑定/更新/清除 |
| 验证变更 | 受影响的 runner、fake/loopback smoke 或发布验证 | 自行复制验证命令、固定 runner 清单或无影响依据地执行完整验证 |
***

## 1. Adapter descriptor 与 registry

### 需求→必须复用的契约/实现

新增或消费价格站点能力时，MUST 以 `ProviderPriceSwitcher.Application.PricingAdapterDescriptor` 描述 `SiteType`、`DisplayName`、`RequiresCredential` 和 `AuthenticationModes`，并通过 `IPricingAdapter.Descriptor` 暴露。价格获取实现 MUST 实现 `IPricingAdapter.FetchAsync(SiteConfiguration, CancellationToken)`，返回 `SitePricingResult`；失败 MUST 使用已有 `PricingAdapterFailure` 分类和 `PricingAdapterException`，不得另造同义异常分类。

`PricingAdapterRegistry` 是运行时 adapter 的单一事实来源。它按传入顺序暴露 `Descriptors`，以 `StringComparer.Ordinal` 建立 `SiteType` 查找，并拒绝空类型和重复类型；消费方 MUST 使用 `IPricingAdapterRegistry.TryGet` 或 `Descriptors`，而不是自己维护映射。当前生产注册位于 `ProviderPriceSwitcher.App/App.xaml.cs`，注册 `NewApiPricingAdapter`、`PawsAiPricingAdapter`、`Sub2ApiPricingAdapter`；实现位于 `ProviderPriceSwitcher.Adapters`。`Sub2ApiPricingAdapter` 通过 Core 的 `ISiteCredentialStore` 获取凭据，其他当前 adapter 不要求凭据。

### 禁止替代做法

MUST NOT 在 `PricingRefreshService`、`MainWindow.xaml.cs`、对话框、持久化迁移代码中增加站点类型字典、display name、认证选项或 credential 判断。MUST NOT 把历史 JSON 中的 `SiteType` 迁移字符串误当作运行时注册；历史数据迁移仍属于 JSON settings load 的兼容逻辑。

### 验证 runner

按变更影响使用 [build-release.md](build-release.md) 的分级矩阵；本节只说明契约覆盖范围，不复制命令或固定 runner 清单。
***

### 新增 adapter 的标准扩展步骤

1. MUST 在 `ProviderPriceSwitcher.Adapters` 新建实现 `IPricingAdapter` 的类型，并明确一个稳定、非空、区分大小写的 `Descriptor.SiteType`；不得修改现有类型的语义来承载新服务。
2. MUST 在 descriptor 中填写真实显示名、是否需要凭据及认证模式；需要 token/Cookie 时只能依赖 `ISiteCredentialStore`，不得自行保存秘密。
3. MUST 将 HTTP、响应校验、价格单位/币种/分组和失败映射封装在 adapter 内，使用 `SitePricingResult` 和既有 `PricingAdapterFailure`；取消令牌 MUST 传入网络请求。
4. MUST 在 `App.xaml.cs` 的唯一生产 registry 注册该实现，并在所有相关 runner 的 fake registry 中显式注册；不得通过反射、扫描或 UI 临时实例化绕过 registry。
5. 仅在新增可观察能力且现有覆盖不足时增加 adapter runner 场景，覆盖 descriptor、成功响应、无效响应、取消/超时或认证失败（按该 adapter 能力适用）；具体验证范围按 [build-release.md](build-release.md) 的影响分级矩阵执行。
6. MUST 保持 Core 的 `SiteConfiguration.SiteType` 字符串和已有持久化格式兼容；如需历史值迁移，只在 `JsonSettingsRepository.Load` 的既有迁移点处理，不能把迁移规则变成动态注册。

## 2. Application 用例入口

### PricingCheckUseCase

需求是用户触发完整价格检查并更新自动倍率时，MUST 调用 `PricingCheckUseCase(PricingRefreshService, ISettingsRepository)`。它使用 `LocalAppSettings.DefaultUsageProfile`，返回 `PricingCheckOutcome(Settings, RefreshResult)`；只有成功结果导致 `CurrentGroupRatio` 或 `GroupRatioSource` 实际变化时才保存一次。无变化和用户取消 MUST 不保存。UI 只消费 outcome、刷新展示状态并处理取消，不得重新实现比较或保存条件。

验证：按 [build-release.md](build-release.md) 的影响分级矩阵选择受影响 runner；不在本节复制命令。
***

### PricingProbeUseCase

需求是站点编辑中的单站价格探测时，MUST 调用 `PricingProbeUseCase(IPricingAdapterRegistry).ExecuteAsync`。它通过 registry 查找 adapter，使用 request timeout 与用户 cancellation token 创建 linked token；未知类型返回既有 `PricingAdapterFailure.InvalidResponse`，内部 timeout 转为 `PricingAdapterFailure.Timeout`，用户主动取消保持取消语义。探测期间的保存按钮状态由 `SiteEditorViewModel`/`AsyncCommand` 管理，不能把 probe 重新放回刷新服务。

验证：按 [build-release.md](build-release.md) 的影响分级矩阵选择受影响 runner；不在本节复制命令。
***

### SiteManagementUseCase 与 SettingsUseCase

站点新增、编辑、重命名、启用/禁用和删除 MUST 复用 `SiteManagementUseCase(ISettingsRepository, IPricingSnapshotRepository)`。该用例维护快照：重命名删除旧 provider 快照，匹配快照按 `PricingSnapshot.Matches`/`WithCurrentRatio` 应用手动倍率；每个有效设置操作按现有契约保存一次；目标不存在抛 `InvalidOperationException`。

设置保存 MUST 复用 `SettingsUseCase(ISettingsRepository).Save`。它以 `StringComparer.OrdinalIgnoreCase` 去重工作目录并保留首次拼写，优先保留等价的原默认目录，否则选择第一项或 null；不额外检查目录是否存在。新增 UI 流程不得直接写 JSON repository。当前 `SitesDialog`/`SiteEditorDialog` 仍在内部创建部分用例或具体凭据实现，这是待收敛债务，不是新增代码的模板。

验证：按 [build-release.md](build-release.md) 的影响分级矩阵选择受影响 runner；不在本节复制命令。
***

### SwitchAndStartUseCase

“用户明确执行 OMP 切换并启动” MUST 通过 `SwitchAndStartUseCase`，以 `ISettingsRepository`、`IOmpConfigurationService`、`IOmpProcessLauncher` 和 `ILogger<SwitchAndStartUseCase>` 注入。固定顺序是：配置切换失败则不保存、不启动；切换成功后将工作目录按 OrdinalIgnoreCase 去重并保存，再启动；启动失败不回滚已完成配置。消费方只处理 `SwitchAndStartStatus` 的 `ConfigurationFailed`、`Started`、`StartedWithExistingProcess`、`LaunchFailedAfterSwitch`。

Infrastructure 的 `OmpConfigurationService` 适配现有 `OmpConfigurationSwitcher`，保留预览、校验、备份、原子替换和取消/Error 行为；`OmpProcessLauncher` 适配现有 `OmpProcessService`。UI MUST NOT 直接拼接或编排这些底层调用。验证按 [build-release.md](build-release.md) 的影响分级矩阵执行。
***

## 3. 持久化、路径与凭据

### settings/snapshot repositories

Application 只依赖 `ISettingsRepository` 与 `IPricingSnapshotRepository`。Infrastructure 的 `JsonSettingsRepository` 和 `JsonPricingSnapshotRepository` 使用现有原子 JSON 写入和默认补齐逻辑；调用方 MUST 通过接口注入。设置文件和快照文件分别由 `AppDataPaths.SettingsFile`/`SnapshotsFile` 定位；不得在业务层复制文件名或 JSON 选项。

### path defaults

`IAppPathDefaults` 是 OMP 路径契约，`AppPathDefaults` 的默认 OMP 根目录为用户目录下 `.omp`，配置路径为 `<ompRoot>/agent/config.yml`，agent 目录为 `<ompRoot>/agent`。需要路径时 MUST 注入或调用该契约；不得把当前机器路径硬编码进 Application、测试或 UI。

### credential store

Core 的 `ISiteCredentialStore` 提供 `LoadCredential`、`SaveCredential`、`ClearCredential`、`GetSummary(providerId)`。Windows 实现 `WindowsSiteCredentialStore` 使用当前用户 DPAPI 保护，按 provider id 的 SHA-256 文件名保存到 credentials 子目录；摘要只返回状态和中性文案。凭据 MUST 不进入 `LocalAppSettings`、`PricingSnapshot`、普通 JSON、日志、错误消息或 OMP 配置。凭据绑定/保存只建立 ProviderId 到安全存储的关联并校验站点类型，不代表供应商已验证；真实认证结果仅在价格探测时由 adapter 反映。`App.xaml.cs` 创建遵守 `--data-root` 的唯一生产 store，并将同一实例注入 `Sub2ApiPricingAdapter` 与 `MainViewModel → SitesDialog → SiteEditorDialog`；编辑器只读取 `GetSummary`，不得调用 `LoadCredential` 或回填 token/Cookie 原文。

烟测和隔离运行 MUST 使用 fake/loopback、临时 data root 与合成凭据；不得打开含真实凭据的站点、截图或记录该界面。凭据更新/清除仅可在任务明确要求且隔离边界可证明时使用 synthetic secret 执行；否则记录人工验收点。验证按 [build-release.md](build-release.md) 的影响分级矩阵选择受影响 runner。
***

## 4. 日志、通知与命令

### ILogger/RollingFileLoggerProvider

Application 和 Infrastructure 业务服务 MUST 依赖 `Microsoft.Extensions.Logging.ILogger<T>`，使用结构化、有限字段日志；日志实现由 `RollingFileLoggerProvider` 提供。它按日写入 `app-YYYYMMDD.log`，超过 `MaxFileBytes` 滚动并保留既有 retention 规则；允许的结构化字段是 `Operation`、`ProviderId`、`SiteType`、`FailureKind`、`ElapsedMilliseconds`。provider 会对 Authorization、Cookie、access_token、refresh_token、Bearer 等内容脱敏，日志失败不应使主流程崩溃。

MUST NOT 记录完整 settings、credential、Authorization/Cookie header、响应 body 或把秘密拼到异常文本。若日志目录需要隔离，必须使用 `AppDataPaths.GetRoot(dataRoot)` 下的 logs。

验证按 [build-release.md](build-release.md) 的影响分级矩阵选择受影响 runner；不得把 fake 存在表述为覆盖所有映射。
***

### IUserNotificationService

App 的 `IUserNotificationService` 只有 `ShowWarning`、`ShowError`、`Confirm`；生产实现 `WpfUserNotificationService` 统一封装主流程的 MessageBox。新增 ViewModel 级错误通知 MUST 注入该接口并只处理一次；取消不显示错误。现有对话框对输入校验和删除确认直接调用 `MessageBox`，允许作为纯 UI 边界保留，但不得进入 Application 或复制出新的业务通知机制。

验证按 [build-release.md](build-release.md) 的影响分级矩阵选择受影响 runner；新增错误/取消/确认行为是否需要断言由可观察契约变化决定。WPF smoke 使用 fake/loopback 与隔离 root。
***

### AsyncCommand/RelayCommand

同步操作复用 `RelayCommand(Action, Func<bool>?)`；异步操作复用 `AsyncCommand(Func<Task>, Action<Exception>, Func<bool>?)`。`AsyncCommand` 是 `ICommand.Execute` 的 async void 边界：执行期间禁止重入，异常调用一次 `onError`，finally 必定恢复 CanExecute；取消是否通知由注入的 `onError`/上层处理。新增主窗口或 ViewModel 异步按钮 MUST 复用它或经审查的等价契约；纯 programmatic 对话框现有事件不得成为复制业务编排的理由。

验证按 [build-release.md](build-release.md) 的影响分级矩阵选择受影响 runner；只有可观察契约新增或改变且覆盖不足时才增加断言。
***

## 5. WPF 共享资源与 data root

`ProviderPriceSwitcher.App/Styles.xaml` 是应用级资源，由 `App.xaml` 合并。现有资源包括 Accent/Danger brushes、Button/TextBlock/DataGrid 默认样式、PrimaryButton、SecondaryButton、DangerButton、FormField、FormLabel、FormControl、SectionHeader 和 DialogActionPanel。新增窗口或字段 MUST 优先使用这些资源；只有确有不同语义才新增资源，并放在 Styles.xaml，不得在窗口内复制样式或建立平行控件框架。

App 启动参数 `--data-root <path>` 由 `App.xaml.cs` 解析，并传递到 settings、snapshot、logger 和唯一的 credential store root；同一 store 同时注入 adapter 与站点编辑 UI。所有隔离运行 MUST 传临时 data root，并显式准备合法的临时 OMP root/工作目录；不得输入真实凭据或访问真实 Provider。具体验证分级见 `rule://build-release`。
***

## 6. 允许新增实现的门槛

新增实现只有在以下条件全部满足时才允许：

1. MUST 先证明现有契约不能表达需求，并在变更说明中指出尝试复用的入口；不能仅因调用位置不同而复制实现。
2. MUST 选择正确层：跨 UI 的业务编排进入 Application 用例；外部 HTTP/文件/Windows 适配进入 Adapters 或 Infrastructure；领域规则进入 Core；WPF 绑定和资源留在 App。
3. MUST 先定义或扩展一个可注入契约，再提供唯一生产实现和 fake/runner 覆盖；不得通过静态全局、隐式扫描或窗口内部 `new` 绕过组合根。
4. MUST 保持现有依赖边界：Application→Core；Adapters→Application+Core；Infrastructure→Application+Core；App→Application+Adapters+Infrastructure+Core。不得让 Infrastructure 依赖 Adapters。
5. 仅当新增或改变可观察行为且现有覆盖不足时，MUST 为对应 console runner 增加断言；纯重构或文档变更不得强加测试。验证范围按 [build-release.md](build-release.md) 分级，不得复制其命令。
6. 仅当实现改变了本文记录的事实入口（契约、生产注册、路径、现状债务或可观察能力）时才更新本目录；不得为一般重构或文档变更重复改写索引。使用相对文件名交叉引用，不得留下与现有入口并行的别名、转发层或废弃 shim。
***
