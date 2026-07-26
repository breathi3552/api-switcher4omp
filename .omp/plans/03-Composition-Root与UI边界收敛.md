# 闭环状态

open

# 背景

本计划只覆盖仓库内源码契约的 clean cutover：把 WPF 启动装配集中到 `ProviderPriceSwitcher.App/App.xaml.cs`，让 `MainViewModel` 和 `SitesDialog` 只依赖 Application 用例/窄查询契约，并删除 UI 内部对 Infrastructure 具体类型和旧装配路径的依赖。开始实施前，执行者 MUST 先阅读 `rule://architecture`、`rule://reuse`、`rule://coding`、`rule://change-workflows`、`rule://build-release`，再重新核实本计划列出的源码、所有构造调用方和 runner；不得假设本计划的现状证据仍然未变。任何公开构造函数、接口或用例签名变更前 MUST 使用 LSP `references` 查找全部实现与调用者，并在迁移后再次确认旧符号无生产引用。

前置计划状态：计划 01（凭据隔离）已于 2026-07-26 完成自动验证、用户手工验收并关闭；本计划只消费其已建立的凭据注入结果，不实现凭据存储、迁移或编辑器 probe Command。计划 04（编辑器命令化）不是本计划的前置条件；本计划不得改动 `SiteEditorDialog` 的 probe Command。

现状证据（实施前必须重新核实）：

- `ProviderPriceSwitcher.App/App.xaml.cs` 当前在 `OnStartup` 创建 `JsonSettingsRepository`、`JsonPricingSnapshotRepository`、`WindowsSiteCredentialStore`、`PricingAdapterRegistry`、`PricingRefreshService`，并在创建 `MainViewModel` 时直接 `new OmpConfigurationSwitcher()`、`new OmpProcessService()`。
- `ProviderPriceSwitcher.App/MainWindow.xaml.cs` 的 `MainViewModel` 构造函数当前接收 `JsonSettingsRepository`、`PricingRefreshService`、`IPricingAdapterRegistry`、`OmpConfigurationSwitcher`、`OmpProcessService`、设置和通知服务；构造函数内部创建 `PricingCheckUseCase`、`SettingsUseCase`、`SwitchAndStartUseCase`，并用 `OmpConfigurationService`/`OmpProcessLauncher` 包装具体 Infrastructure 类型。
- `MainViewModel.ReadCurrentProviderAsync` 当前通过 `AppPathDefaults` 计算路径，直接调用 `File.Exists`、`File.ReadAllTextAsync` 和 `OmpConfigurationSwitcher.Preview`；`LoadPersistedPrices` 当前通过 `PricingRefreshService.LoadSnapshots()` 读取快照。
- `MainViewModel.ManageSites` 当前把具体 repository/refresh service 传入 `new SitesDialog(...)`；`SitesDialog` 当前接收 `JsonSettingsRepository`、`PricingRefreshService`，并在构造函数内 `new SiteManagementUseCase(settingsRepository, refreshService.SnapshotRepository)`，刷新行时直接调用 refresh service 的快照读取。
- `ProviderPriceSwitcher.App.Tests/Program.cs` 当前直接构造旧签名 `MainViewModel`；`App.xaml.cs` 是生产唯一启动组合根，但所有 runner/UI 测试中的窗口与 ViewModel 构造也属于必须迁移的源码调用方。

影响层：`ProviderPriceSwitcher.Application` 新增最小查询端口/结果契约；`ProviderPriceSwitcher.Infrastructure` 提供 OMP 当前 provider 查询和站点快照查询的外部实现（不得引用 App 或 Adapters）；`ProviderPriceSwitcher.App` 调整 composition root、`MainViewModel`、`SitesDialog` 和全部构造调用方；受影响的 `ProviderPriceSwitcher.App.Tests` runner 更新其测试装配。生产依赖必须保持 `Application→Core`、`Infrastructure→Application+Core`、`App→Application+Adapters+Infrastructure+Core`，不得新增 `Infrastructure→Adapters`。

# 目标

- `App.xaml.cs` 成为唯一生产 composition root：解析 `--data-root`，创建日志、路径、repository、计划 01 的凭据 store、OMP Infrastructure gateway、adapter registry、Application 用例和查询端口，然后把已构造实例注入 `MainViewModel`、`MainWindow` 和 `SitesDialog` 所需的入口；Window/DataContext 来源明确可追踪。
- `MainViewModel` 不再持有或构造 `JsonSettingsRepository`、`OmpConfigurationSwitcher`、`OmpProcessService`、`PricingRefreshService`，不直接读取 OMP 文件；它只接收已构造的 Application 用例/端口、adapter registry（如其 UI 展示仍需要）、设置快照和通知服务。检查、设置保存、切换启动、当前 provider 查询和快照查询均经注入契约完成。
- 在 `ProviderPriceSwitcher.Application` 定义最小、无 WPF/Windows/JSON/File 类型的查询契约：一个用于读取当前 OMP provider 的异步查询端口（输入明确的 OMP root、输出中性当前 provider/状态结果、支持 `CancellationToken`），以及一个供站点列表/主窗口读取快照的窄查询端口（只暴露只读快照视图和必要的 `Load` 操作）。若重新核实发现现有 `IPricingSnapshotRepository` 已完全满足窄查询且不会把写入能力传给 UI，则复用它并仅通过 Application 窄接口暴露；不得再造同义查询服务。
- `Infrastructure` 以唯一实现承接 OMP 文件读取/预览和 snapshot 读取，负责结构化失败映射，不向 Application 泄露文件路径、JSON 或 `OmpConfigurationSwitcher` 以外的 UI 实现细节。
- `SitesDialog` 构造函数直接接收 `SiteManagementUseCase` 和窄快照查询契约（以及必要的 `IPricingAdapterRegistry`、设置、当前 provider；adapter registry 仅按现有 SiteEditor 入口传递），不再接收具体 repository/refresh service，也不在 UI 内部 `new SiteManagementUseCase`。
- 完成迁移后删除旧构造签名、旧字段、旧 UI 装配、无效 `using` 和所有仓库内生产/runner 调用方；不得保留别名、重载、转发或“临时” shim。

不变量：Application 不依赖 WPF、Windows、File API、JSON、Infrastructure 或 Adapters；检查只推荐不自动切换；只有用户明确执行切换/启动才产生 OMP 副作用；无效/陈旧快照不得参与自动推荐且当前组和最低组仍可展示；查询失败必须保留结构化失败状态，不得伪造成功或静默返回空业务结果；凭据只消费计划 01 的安全 store，不进入设置、快照、日志、异常、命令行或 UI 文本。

# 详细步骤

## 代码改动项

1. **建立基线与符号清单。** 在任何修改前按适用 rulebook 重新阅读 `App.xaml.cs`、`MainWindow.xaml.cs`、`SitesDialog.xaml.cs`、`PricingRefreshService.cs`、`PersistenceContracts.cs`、`OmpConfigurationSwitcher.cs`、`OmpApplicationAdapters.cs` 及 `ProviderPriceSwitcher.App.Tests/Program.cs`；使用 LSP `references` 查询 `MainViewModel`、`SitesDialog`、`PricingRefreshService.LoadSnapshots`、`SiteManagementUseCase`、`OmpConfigurationService`、`OmpProcessLauncher` 的全部调用方/实现。记录计划 01 已交付的 credential store 构造契约，禁止在本计划另行调整。

2. **在 Application 放置最小查询契约。** 在已有 Application 契约文件或职责最接近的文件中新增明确命名的 OMP 当前 provider 查询接口/不可变结果（例如 `IOmpCurrentProviderQuery` 与 `OmpCurrentProviderResult`）以及快照窄查询接口（例如 `IPricingSnapshotQuery`）；命名和最终文件以重新核实的复用入口为准，但只能保留一套契约。结果必须表达“配置文件不存在、未识别、读取失败、已识别 provider”等可观察状态，失败用结构化字段/错误类别，不以异常消息分类；异步 OMP 查询必须接收并传递取消令牌。契约不得出现 `FileInfo`、路径实现、JSON DTO、WPF 或 Infrastructure 类型。

3. **在 Infrastructure 提供唯一实现。** 新增或调整 Infrastructure 的适配类：OMP 查询复用 `OmpConfigurationSwitcher.Preview` 和 `IAppPathDefaults`，由 Infrastructure 读取配置并映射为 Application 结果；快照查询复用现有 `IPricingSnapshotRepository`/`JsonPricingSnapshotRepository`，只提供 `IReadOnlyDictionary<string, PricingSnapshot>` 或既有只读等价视图。不得让 `MainViewModel` 直接调用 repository，也不得将 `PricingRefreshService` 重新包装成第二个平行业务服务。保留原有备份、原子替换、取消和结构化错误行为；本计划不改变 OMP 切换本身的顺序和安全规则。

4. **收紧 `MainViewModel` 构造与字段。** 删除 `JsonSettingsRepository`、`OmpConfigurationSwitcher`、`OmpProcessService`、`PricingRefreshService` 字段及 `AppPathDefaults`/File 相关直接依赖；将其所需行为改为注入已构造的 `PricingCheckUseCase`、`SettingsUseCase`、`SwitchAndStartUseCase`、OMP 当前 provider 查询、快照窄查询、adapter registry、初始 settings 和通知服务。用例只在 `App.xaml.cs` 创建，ViewModel 构造函数不再 `new` Application 用例或 Infrastructure 实现。`ReadCurrentProviderAsync` 只调用 OMP 查询并按结构化结果更新 UI；`LoadPersistedPrices` 只调用窄快照查询。UI 文本可以格式化结果，但不得根据异常消息判定协议状态。

5. **收紧 `SitesDialog` 构造与查询。** 删除 `JsonSettingsRepository`、`PricingRefreshService` 字段和对应 `using`；构造函数接收预先构造的 `SiteManagementUseCase` 和窄快照查询契约。`RefreshRows` 只通过查询契约获取快照；`SaveSite`、`ToggleSelected`、`DeleteSelected` 继续经注入的 `SiteManagementUseCase`。`SiteEditorDialog` 的 probe Command、凭据注入和现有 editor 行为不在本计划改动范围；传递其现有必要参数时不得新增内部 Infrastructure 装配。

6. **迁移 composition root。** 在 `App.xaml.cs` 按依赖方向先创建 data paths、settings/snapshot repository、计划 01 的 credential store、日志和 OMP gateway，再创建 adapter registry、refresh service、Application 用例和查询端口实现，最后构造 `MainViewModel`/`MainWindow`。构造 `SitesDialog` 的责任由 `MainViewModel` 改为接收一个已构造的站点管理入口或通过明确的窄 dialog factory 注入；不得在 ViewModel 内恢复具体类型装配。`MainWindow` 保持显式 `DataContext = viewModel`。启动失败仍经现有通知边界处理，不泄露凭据、完整路径或原始敏感异常。

7. **迁移全部调用方并删除旧路径。** 按 LSP references 更新 `App.xaml.cs`、`MainWindow.xaml.cs`、`ProviderPriceSwitcher.App.Tests/Program.cs` 以及重新搜索发现的其他 runner/测试构造；对公开构造函数变更逐一确认无遗漏。删除旧构造重载、旧字段、内部 `new SiteManagementUseCase`、内部 `new SwitchAndStartUseCase`、`new OmpConfigurationService`/`new OmpProcessLauncher` 和 UI 里的具体 Infrastructure 类型引用；清理无效 `using`。不得留下兼容别名、re-export、旧参数顺序重载、双路径查询或反射装配。

8. **补齐契约可观察性但不扩大范围。** 若当前 runner 没有覆盖新查询契约，则只在受影响 runner 增加 deterministic fake/loopback 场景：OMP 配置存在并识别 provider、文件不存在、格式/预览失败、主动取消；快照查询成功且保留当前/最低组、读取失败为结构化失败。纯构造迁移不新增与行为无关的测试。不要在本计划实现计划 04 的 SiteEditor probe Command。

## 其他改动项

- 仅在代码事实确实改变时更新对应治理索引（例如 `reuse.md` 需要记录新的 Application 查询入口或 composition root 注册）；若既有规则已经准确描述该入口，则不重复改写规则。任何文档更新必须在代码和验收事实完成后进行。
- 不修改 `.omp/plans/模板.md`、其他计划、发布脚本、CI、持久化 schema、Provider API、凭据 schema 或 OMP 切换策略；不得借本计划顺手升级依赖、格式化全仓库或重排无关 UI。
- 文档更新条件：只有当查询契约名称/位置、生产注册入口或架构事实发生变化时，更新对应治理文档；变更记录须说明旧 UI 具体依赖已删除和新入口，不新增与规则重复的设计文档。
- 回滚边界：这是仓库内源码契约的 clean cutover；若实现失败，只能在当前变更范围内恢复原始构造/调用图并同步恢复所有调用方，不能保留新旧双路径。不得回滚计划 01 的凭据安全结果、已发布持久化 schema 或外部 Provider API；发现这些边界被触及时先停止并重新制定兼容策略。

## 验证步骤

1. **静态依赖边界与 clean cutover 核验。** 使用 LSP `references` 和 diagnostics 确认 Application 只依赖 Core，Infrastructure 只依赖 Application/Core，App 才依赖 Application/Adapters/Infrastructure/Core，且不存在 Infrastructure→Adapters 反向引用。逐一核对 `MainViewModel`、`SitesDialog`、所有公开构造函数和查询端口的实现与调用方；搜索 `ProviderPriceSwitcher.App`、App runner/UI 测试及其他 runner，确认旧构造签名、旧字段、`new SiteManagementUseCase`、`new SwitchAndStartUseCase`、`new OmpConfigurationService`、`new OmpProcessLauncher`、UI 内 `File.Read*`/具体 Infrastructure 类型和兼容 shim/别名均无残留。静态证据必须证明所有调用方已一次性迁移，而不是仅证明主启动路径可编译。

2. **目标—证据映射与验证级别。** 执行者必须为每项目标建立可追溯记录：composition root 唯一性由静态调用图、App runner 和真实 WPF 启动路径共同证明；ViewModel/Dialog 无 Infrastructure/File 依赖由静态边界和构造调用检查证明；查询结果状态、取消、快照保留/失败语义由 Application/Infrastructure/App runner 的 deterministic fake/loopback 场景证明；Window/DataContext、当前 provider、价格快照、站点打开/刷新/关闭由 STA runner 直接构造窗口、执行命令、泵 Dispatcher 并读取控件/绑定/ViewModel 状态证明，独立进程 smoke 仅补充真实组合根、窗口标题、文件/日志、退出码和残留进程证据；所有调用方 clean cutover 由 LSP references、全仓源码搜索和 runner 构造成功共同证明。不得以“能编译”替代这些行为证据，也不得仅以进程存活证明 UI 行为。
3. **跨层 runner 与契约场景。** 按 `rule://build-release` 的固定 SDK 和顺序执行受影响项目 build、格式验收及完整八个 runner（不得使用裸 `dotnet`，不得使用 `dotnet test`）；当前计划创建阶段不执行这些命令。实施验收时至少覆盖 Application、Infrastructure、App，并按跨层级别覆盖 Core、Adapters、OmpConfig、OmpProcess、Refresh runner，断言 OMP 配置成功识别、文件不存在、未识别、结构化失败、主动取消，以及快照成功、保留当前/最低组、读取失败结构化返回。所有 runner 使用 fake/loopback，禁止真实 Provider 网络和真实凭据。
4. **隔离的真实 WPF 路径 smoke。** 按 `rule://build-release` 使用临时 data root、临时 OMP root、合法临时 working directory 和隔离日志目录；在隔离 `settings.json` 显式设置 `OmpRootDirectory`、`OmpWorkingDirectories`、`LastOmpWorkingDirectory` 及至少一个合成站点，并通过固定用户 `.dotnet\dotnet.exe` 与 `--data-root` 启动真实 App。STA runner 直接构造并显示 `MainWindow`/`SitesDialog`，断言 DataContext、绑定、查询状态、站点打开/刷新/关闭和设置路径；独立进程 smoke 通过 Win32 `EnumWindows`/窗口标题确认窗口创建，发送 `WM_CLOSE`，检查退出码、隔离文件/日志和无残留 App/OMP 子进程。不得打开含真实凭据的站点，不得绑定/更新/清除凭据，不得访问真实 Provider，不得执行真实 OMP 配置切换或启动；不得仅以进程存活证明 UI 行为。
5. **闭环判断与状态迁移。** 执行者完成静态检查、runner 和隔离 smoke 后，必须明确回答：“背景问题是否消除？每项目标是否达成？是否具备自主验证充分证据？”并逐项记录命令/场景、退出码、关键 `passed`/状态结果、data/OMP/log root、未触及真实数据与凭据、退出码和残留进程检查。不可自动验证的视觉布局、渲染体验或未覆盖的焦点感受不阻塞源码计划闭环，但不得声称已覆盖；可编程的命令、控件属性、`AutomationPeer`、`KeyboardNavigation`/`FocusManager` 元数据和 Dispatcher 状态必须纳入断言。只有代码实施完成、适用验证通过、背景问题消除、每项目标达成且证据充分时才可将本节状态改为 `closed`；否则保持 `open` 并记录具体阻塞/未通过项。除本计划明确范围外不执行 publish；只有单独发布任务或用户明确要求才按规则进入 publish 验收。

# 为什么选择此技术路线

把装配集中到 `App.xaml.cs` 是现有架构规则规定的 composition root 方向，能让 Application 保持可替换、Infrastructure 不依赖 UI，并一次迁移所有仓库内源码调用者。将 OMP 文件读取建模为 Application 查询端口而不是把 `File` 或 JSON 推入 ViewModel，可保留当前 preview 的结构化状态并使非 WPF runner 能独立验证。将站点管理动作和快照读取拆成“用例 + 窄查询”避免向 UI 暴露 repository 写入能力，也避免把 `PricingRefreshService` 误当作查询门面，从而减少耦合和未来误触发刷新/副作用的风险。以 clean cutover 删除旧构造和内部装配，能够让依赖图可静态审查，避免兼容 shim 长期隐藏边界债务；凭据和 editor probe 分别交给计划 01、04，保证计划边界互斥且不会重复实现。

# 注意事项

- 本文件是执行计划，不是实施结果；执行者不得把现状证据当作无需复核的事实。
- 不得把 `Application` 查询契约设计成 `IServiceProvider`、`object`、字符串错误或暴露 Infrastructure repository 的宽接口；结果必须是最小、可断言、无敏感信息的 Application 类型。
- 不得让 `MainViewModel` 通过静态全局服务定位器、反射、默认构造函数或隐藏 factory 重新取得具体实现；所有依赖必须显式注入。
- 不得改变“检查只推荐、不自动切换”、快照陈旧限制和 OMP 显式操作规则；查询失败不得清空或覆盖最后成功快照。
- 不得把 token、Cookie、Authorization、密码、真实路径或完整异常写入日志、设置、快照、错误文本、runner 输出或窗口状态记录。
- 不得运行真实 Provider、使用真实凭据、触碰真实用户 data/OMP root，或执行正式 publish；计划创建阶段严格不运行 formatter、build、runner、测试或 smoke。
- 计划 04 负责 `SiteEditorDialog` probe Command；若实现本计划时发现需要改动 probe 或 credential editor，停止并移交计划 04/01，不在本计划留下半迁移 shim。
