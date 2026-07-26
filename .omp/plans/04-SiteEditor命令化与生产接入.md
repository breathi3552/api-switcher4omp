# 闭环状态
closed

（2026-07-26）已完成。背景问题已消除，全部目标达成，具备自主验证充分证据。

## 闭环证据

- **生产接入与 clean cutover**：`SiteEditorViewModel` 是 `SiteEditorDialog` 的真实 `DataContext`，XAML 绑定 `ProbeCommand`、`CancelProbeCommand`、`SaveCommand`。`App.xaml.cs` 装配 `PricingProbeUseCase`、`SiteEditorDialogFactory` 与 `SitesDialogFactory`；`MainViewModel`/`SitesDialog` 只消费工厂，不直接创建子窗口或业务用例。旧动态 `Content`、`ProbeAsync`、内部 `new PricingProbeUseCase`、旧构造及 VM-only 虚假断言已删除。
- **命令与生命周期契约**：输入属性变更重评估 CanExecute；`AsyncCommand` 拒绝 probe 重入；busy 时 Save 禁用、Cancel 启用；主动取消后恢复可重试且不通知错误。窗口关闭时先取消并等待活动 probe，再完成关闭，无未观察后台任务。成功 Save 仅在真实模态 dialog 中设置结果并返回结构化 `SavedSite`。
- **凭据与错误安全**：token/Cookie 不进入 ViewModel 字段、绑定状态或可序列化模型，只经命名 PasswordBox/TextBox 的 code-behind 一次性桥接传给安全 store，成功后立即清空；从不调用 `LoadCredential`。认证失败补入 `UserErrorMessages.ForProbeFailure` 的稳定脱敏映射；取消不通知，未知异常只显示中性文案。
- **固定 SDK 验证**：使用用户 `.dotnet/dotnet.exe`（SDK `8.0.423`）串行执行 solution build、format verify 与完整八 runner，build 为 `0` warning / `0` error，八 runner 均 `passed`。增强后的 App STA runner 再次独立通过，覆盖真实 dialog/DataContext/ICommand、synthetic credential 一次写入和清空、零原文读取、阻塞 fake 的重入拒绝/取消恢复/零错误通知、成功 probe 与模态 Save。
- **隔离 WPF smoke**：独立进程使用临时 data/OMP/working root、禁用合成站点和 loopback 不可达 URL启动；Win32 观察到 `ProviderPriceSwitcher` 窗口，隔离日志记录启动，`WM_CLOSE` 正常退出码 `0`，临时目录删除、无残留进程。
- **安全与未执行项**：未访问真实 Provider，未使用真实凭据，未触碰真实用户设置/OMP 配置/日志，未执行真实价格 probe、OMP 切换/启动或 publish。LSP 未配置，以全仓静态搜索、编译器和 runner 构造调用图替代；未声称覆盖纯视觉体验。
- **三问结论**：背景问题已消除；目标 1—10 全部达成；静态 clean cutover、真实 STA 控件/命令行为、完整验证链与隔离进程 smoke 提供充分自主验证证据。

# 背景

执行者开始前 MUST 先阅读 `rule://architecture`、`rule://reuse`、`rule://coding`、`rule://change-workflows` 和 `rule://build-release`，然后重新核实源码、项目引用、全部生产调用方及 runner；不得假设本计划中的现状证据仍未变化。计划 01（凭据隔离）已于 2026-07-26 完成并关闭；计划 03（composition root）仍是本计划前置条件，必须先完成其验收。本计划不得重复实现计划 01 的安全 store 和摘要边界。

现状证据（以执行时源码为准，以下是本次审查定位到的证据）：

- `ProviderPriceSwitcher.App/SiteEditorViewModel.cs` 的 `SiteEditorViewModel` 目前只有 descriptor、认证模式、`IsProbing`/`CanSave` 和 `ApplySiteType`，没有生产 probe 命令、取消命令、结构化结果或凭据契约；`ProviderPriceSwitcher.App.Tests/Program.cs` 只直接实例化它并断言属性，不能证明窗口实际使用该 VM。
- `ProviderPriceSwitcher.App/SiteEditorDialog.xaml` 仍只有空 `Grid`；`SiteEditorDialog.xaml.cs` 自己创建控件和 `PricingProbeUseCase`，并在 code-behind 编排凭据更新/清除、探测、保存和错误提示。计划 01 已将 `ISiteCredentialStore` 从 composition root 注入，删除具体 store 创建、`LoadCredential` 和原始 Cookie 回填；本计划必须保留该安全边界，只迁移剩余 code-behind 业务编排。
- `ProviderPriceSwitcher.App/SitesDialog.xaml.cs` 在 `AddSite`/`EditSelected` 内部 `new SiteEditorDialog`，并把 repository、refresh service、adapter registry 等具体依赖向下传递；它必须迁移到计划03提供的生产装配入口和明确的 dialog/ViewModel 契约。
- `ProviderPriceSwitcher.App/App.xaml.cs` 是当前 composition root，但执行时必须以计划03验收后的最终装配为准；SiteEditor 依赖必须由此处或其明确的窗口工厂注入，不能回到窗口内部 `new`。
- `ProviderPriceSwitcher.App/Mvvm.cs` 已有 `AsyncCommand(Func<Task>, Action<Exception>, Func<bool>?)`，具有禁止重入、异常边界和 finally 恢复 `CanExecute` 的行为；不得另造命令实现。`RelayCommand` 仅用于同步取消/关闭等操作。
- `ProviderPriceSwitcher.App.Tests/Program.cs` 当前的 `SiteEditorViewModel` 断言是未接入生产窗口的虚假覆盖；迁移完成后必须删除这些脱离真实 VM 生产路径的断言，改为针对实际 DataContext、命令和对话框结果的可观察契约。

影响层：`ProviderPriceSwitcher.App` 的 XAML、ViewModel、dialog/code-behind、`SitesDialog` 调用方、`App` composition root 和 `ProviderPriceSwitcher.App.Tests` runner；跨越 Application 的 `PricingProbeUseCase`、Core 的站点/结构化失败类型以及计划01/02/03已经确定的契约，但本计划不重复修改其实现。生产依赖必须保持 `App → Application + Adapters + Infrastructure + Core`，不得让 Application、Infrastructure 或 Adapters 依赖 App。

# 目标

把 `SiteEditorViewModel` 作为 `SiteEditorDialog` 的真实 `DataContext`，让 XAML 通过现有 `AsyncCommand` 管理 probe、cancel、busy、`CanSave` 和结构化 probe 结果；code-behind 只保留窗口生命周期、纯视图构造/焦点和无法由绑定表达的 UI 细节，不再编排异步业务、凭据、网络或保存规则。

目标行为和不变量：

- Probe 只能通过现有 `PricingProbeUseCase` 及计划03装配的 registry 运行；不会在 VM、dialog 或 XAML 解析供应商协议或创建 adapter。
- 点击 Probe 后命令立即进入 busy，重复点击被 `AsyncCommand` 拒绝；Save 在 probe 期间不可执行，成功、结构化失败、用户取消和内部超时后按钮状态必定恢复。
- 用户主动取消保持取消语义：不当作错误、不弹错误通知、不保存站点或凭据；内部超时必须显示计划02定义的脱敏文案并保留结构化 `Timeout` 类别；认证失败显示计划02既有认证失败边界，不泄露 token、Cookie、Authorization、完整响应或供应商异常文本。
- probe 结果以稳定的结构化状态/失败类别绑定到 UI，再由计划02边界转换为用户文案；禁止按消息字符串分类，禁止静默成功、自动重试、自动切换或用旧结果伪装成功。
- 只有输入有效、当前不 busy 且没有未完成取消任务时 `CanSave` 为 true；保存调用既有站点管理契约，凭据仍只经计划01的 `ISiteCredentialStore`/安全摘要边界，不能写入 ViewModel 可序列化模型、settings、snapshot、日志或普通控件快照。
- 关闭窗口时停止/取消未完成 probe，并等待或有界确认任务结束，不留下后台任务、网络请求或进程；关闭/取消不产生站点保存副作用。保存成功才返回 `DialogResult=true` 和结构化站点结果，取消/关闭返回 false/null。
- SitesDialog 的新增和编辑均走同一个注入的 SiteEditor dialog/VM 工厂与既有 `SiteManagementUseCase`，成功后刷新列表，取消或失败不改变设置；旧的具体 repository/refresh/credential 构造路径全部删除。

非目标：不新增 provider、计费推断、凭据存储格式、错误分类、Application 用例、OMP 切换策略或发布流程；不执行真实 Provider probe、不执行真实凭据绑定/更新/清除、不执行正式 publish。若计划01或03未提供所需安全 store、结构化错误或工厂契约，必须停止并记录前置阻塞，而不是在本计划内造 fallback。

# 详细步骤

## 代码改动项

1. **前置核验和调用图基线**：先核对计划01/03验收证据、最终构造签名、`ISiteCredentialStore` 安全 root、计划02错误映射和计划03 composition 注册。使用 LSP 对即将修改/删除的公开构造函数、`SiteEditorViewModel`、dialog 工厂/接口和命令属性执行 `references`，再用源码搜索核对 `SiteEditorDialog`、`SitesDialog`、`App.xaml.cs`、App runner 的全部调用方；不得遗漏测试 runner。
2. **扩展 `SiteEditorViewModel`**：在 `ProviderPriceSwitcher.App/SiteEditorViewModel.cs` 保留 descriptor/认证模式等绑定状态，注入计划03提供的 `PricingProbeUseCase`、计划01凭据契约/摘要能力、计划02通知/错误边界及必要的窗口结果协作契约。增加适合 XAML 的输入属性（ProviderId、BaseUrl、Model、Group、倍率/币种等）和可断言的结构化 `ProbeState`/`ProbeResult`，不得把 token/Cookie 原文放入持久化或诊断结果。由 VM 创建并暴露 `ProbeCommand`、`CancelProbeCommand`、`SaveCommand`（以及需要的关闭结果命令），全部复用 `AsyncCommand`/`RelayCommand`；命令的 `CanExecute` 必须由 busy、输入校验、当前站点状态共同决定，并在属性变更时触发重评估。
3. **probe 生命周期**：在 VM 内创建链接的用户取消 token source，调用现有 `PricingProbeUseCase.ExecuteAsync` 并传递 token/请求超时契约；区分成功、`Timeout`、认证失败、其他结构化失败和主动取消。`finally` 清理 token source、恢复 `IsProbing`、`CanSave` 和按钮 `CanExecute`。异常只进入计划02既有脱敏边界和 `IUserNotificationService`，取消不通知；不能使用 `async void`（`AsyncCommand` 框架边界除外），不能比较异常消息。
4. **保存与验证**：将表单输入转换为 Core `SiteConfiguration`，复用既有站点管理用例/结果契约，不在 VM 复制去重、快照或倍率规则。Save 必须拒绝 probe 期间提交；凭据仅调用计划01契约并显示中性摘要，保存凭据不声称供应商认证成功。保存成功通过明确的 dialog result/回调交给 SitesDialog；失败使用计划02文案边界，保持窗口和按钮可重试。
5. **重写 `SiteEditorDialog.xaml`**：建立完整表单与命令绑定，显式绑定 `DataContext` 可追踪到 `SiteEditorViewModel`；Probe、Cancel、Save 的 `Command`、`IsEnabled`/`Visibility`、busy 指示和结构化结果展示均由 VM 属性驱动。沿用 `Styles.xaml`/`App.xaml` 共享资源，不在窗口复制通用样式；密码/token 控件只作为输入边界，禁止把原文回填到 TextBox、日志、异常或快照。
6. **收缩 `SiteEditorDialog.xaml.cs`**：构造函数只接收计划03工厂需要的最小契约和初始站点/窗口 owner，设置 VM/DataContext；保留 `InitializeComponent`、Window 生命周期、必要的 PasswordBox/焦点桥接和 `DialogResult` 收尾。删除 `PricingProbeUseCase`、`WindowsSiteCredentialStore`、具体 repository/refresh 依赖、`ProbeAsync` 业务编排、业务 `MessageBox`、重复 busy 状态、保存/清除凭据的直接实现以及旧动态控件事件；所有生产调用方同步迁移，禁止保留别名或 shim。
7. **迁移 `SitesDialog`**：在 `ProviderPriceSwitcher.App/SitesDialog.xaml.cs` 的 `AddSite`、`EditSelected` 使用计划03注入的 SiteEditor dialog/VM 工厂或明确 Application-facing dialog contract；不再传入 `JsonSettingsRepository`、`PricingRefreshService`、adapter registry 以供子窗口内部组装。新增/编辑返回结构化结果后调用既有 `SiteManagementUseCase`，更新 Settings、刷新 rows 和选择项；取消、关闭、probe 取消和失败均不写入设置。保持删除/启停确认与列表生命周期为纯 UI 边界，并清理迁移后无用字段/构造参数。
8. **接入 `App.xaml.cs` composition root**：按计划03最终装配创建唯一的 registry、probe 用例、站点管理用例、凭据安全实现、错误通知和 SiteEditor dialog/VM 工厂，并把工厂传给 MainWindow/SitesDialog。确认 App runner 使用同一路径而非 `new` 出旧 dialog/VM；移除窗口内部具体 Infrastructure 依赖。不得改变 OMP 自动切换、真实 Provider 默认禁用或 `--data-root` 语义。
9. **重写 App runner 覆盖生产路径**：在 `ProviderPriceSwitcher.App.Tests/Program.cs` 删除只实例化 `SiteEditorViewModel`、只断言属性而未打开实际 dialog 的虚假测试；保留并扩展现有 `AsyncCommand` 契约仅在仍有独立价值时。使用确定性 fake registry/probe/credential store/notification，在真实 WPF STA 线程构造 `App` composition 或 SitesDialog→SiteEditorDialog，断言实际 `DataContext` 类型、XAML 命令绑定、成功结果和窗口关闭结果。覆盖重复点击、用户取消、内部 timeout、认证失败、结构化结果、Save 恢复和关闭清理；禁止真实网络、真实凭据、真实 OMP root。
10. **清理和公开契约迁移**：对每个改变的公开构造函数、VM 属性/命令和 dialog factory 用 LSP 再查 references，迁移所有生产/runner 调用方；删除旧入口、旧事件处理器、旧字段、未使用 using、动态控件业务编排和仅服务旧路径的测试。不得以兼容重载、转发别名、双路径或反射保留旧契约。

## 其他改动项

- 仅在代码事实确实改变时，按职责更新治理文档中的事实入口：如果计划03已经记录 composition root，则只补 SiteEditor 已实际接入的符号/调用方向；如能力入口、凭据隔离债务或错误边界发生变化，分别更新对应治理索引，不能在本计划重复定义规则。
- 若 XAML 绑定契约、App runner fixture 或现有测试说明发生变化，更新对应已有测试/治理说明；不新增与模板并行的文档，不创建发布说明，不修改 `.omp/rules`、脚本、生产代码以外的无关文件。
- 为 WPF smoke 准备临时 data root、临时 OMP root 和合法临时工作目录的脚本参数/fixture 只能复用现有 `--data-root` 入口；settings 必须明确 `OmpRootDirectory`、`OmpWorkingDirectories`、`LastOmpWorkingDirectory`，并使用 fake/loopback。计划 01 已验证凭据 store 隔离；本计划需要凭据交互时只允许使用 synthetic secret，不得使用真实凭据、真实 Provider 或真实用户目录。
- 不要求发布；本计划的脚本/runner 质量检查若需接入，必须使用计划05唯一入口，不能另造验证入口。

## 验证步骤

执行者完成代码后，按 `rule://build-release` 串行验证并记录场景、命令、退出码、关键输出、隔离目录、未执行项、真实数据检查和残留进程检查；本计划编写阶段不运行以下命令。

1. 先以固定 SDK 入口检查环境：`$dotnet = Join-Path $env:USERPROFILE '.dotnet\\dotnet.exe'`，确认 SDK `8.0.423`；禁止裸 `dotnet`。
2. 这是跨层行为/契约变更（App XAML/VM/dialog、composition、Application probe、凭据和错误边界）：先顺序执行受影响 build 与格式验收，再按规则顺序执行完整八个 console runner；不得用 `dotnet test` 替代。若计划03明确将其降为仅受影响 runner，仍至少执行 App、Application、Adapters、Infrastructure 中受影响 runner，并以计划03验收结论为依据记录范围。
3. App runner 必须使用 fake/loopback probe 和合成凭据，断言：实际 `SiteEditorDialog.DataContext` 是目标 VM；初始 descriptor/认证模式正确；Probe 重复触发只有一次执行；Cancel 后不通知错误、不保存；内部 timeout 的结构化类别为 `Timeout` 且文案脱敏；认证失败分类稳定且不含 token/Cookie/响应；成功后结果可见、Save 恢复；失败/取消后可重试；关闭期间取消任务并正常返回、无后台任务。
4. 执行受影响窗口的隔离 smoke：使用临时 data root、显式临时 `OmpRootDirectory`、合法临时 `OmpWorkingDirectories`/`LastOmpWorkingDirectory` 和 fake/loopback，不触碰真实 `%USERPROFILE%\.omp`、真实 Provider 或凭据。WPF STA runner 直接构造 SitesDialog→SiteEditorDialog 真实生产路径，执行实际 `ICommand`，泵 Dispatcher，读取 DataContext、控件属性、绑定/ViewModel 状态、`AutomationPeer`、`KeyboardNavigation`/`FocusManager` 元数据、fake 调用次数和窗口结果；凭据交互仅使用 synthetic secret，并继续验证不回填、更新后清空和明确清除确认边界。独立进程 smoke 使用固定用户 `.dotnet\dotnet.exe` 与 `--data-root`，通过 Win32 `EnumWindows`/标题确认窗口创建，发送 `WM_CLOSE`，检查退出码、隔离日志/文件内容和无残留 App/OMP 子进程；不得仅以进程存活证明 UI 行为。
5. 验收必须同时证明：生产装配没有窗口内部 `new` 具体 Infrastructure/adapter；Application 依赖方向未改变；所有公开符号调用方已迁移；旧 code-behind probe/save 路径和虚假 VM-only 覆盖无残留；不变量（不自动切换、取消不保存、错误脱敏、凭据不落盘）均有可观察证据。
6. 回滚边界：若失败，只回滚本计划新增/迁移的 SiteEditor XAML/VM/dialog、SitesDialog 调用方、App composition 和 App runner 变更；不得回滚或覆盖已验收的计划01/03安全存储、错误边界或 composition 基础，也不得恢复旧的凭据泄露/窗口内部业务编排作为“临时兼容”。
7. **按目标逐项闭环映射**：验证记录必须逐项关联本计划目标和证据，而不是只报告 VM 单元结果：DataContext/生产接入由真实生产装配和 STA runner 证明；Command/输入由实际 ICommand、控件属性、绑定状态和 Dispatcher 断言证明；重入由阻塞 fake 与执行次数证明；取消/关闭由任务、DialogResult、设置副作用、退出码和残留进程证明；超时/认证由结构化类别和脱敏文案证明；保存/回归由管理用例和文件状态证明。
8. **自主验证结论**：执行者完成静态检查、受影响 runner、STA runner 和隔离进程 smoke 后，必须明确回答背景问题、每项目标、证据是否充分。仅 VM/命令孤立测试不得闭环；不可自动验证的视觉体验不阻塞源码计划，但不得声称覆盖未测视觉。
9. **闭环状态迁移规则**：本计划初始保持 `open`；仅当代码实现、适用验证全部完成，背景问题消除、每项目标达成、生产路径证据充分时，才把第一节改为 `closed`。任何阻塞、失败、未执行或未验事项都必须具体记录并保持 `open`。

# 为什么选择此技术路线

`AsyncCommand` 已经集中提供禁止重入、异常回调和 finally 恢复能力，使用它可让 probe 的 busy/CanSave/取消语义成为 VM 可观察状态，避免 XAML 与 code-behind 各自维护按钮状态。`PricingProbeUseCase` 已封装 registry 查找、超时和取消分类，复用它能保持 Application/Adapters 边界及结构化失败语义，不把供应商实现搬进 WPF。把 DataContext 和所有外部实现集中到计划03的 composition root，能完成 clean cutover，SitesDialog 与 SiteEditor 不再自行创建具体 Infrastructure；凭据继续由计划01安全 store 管理、文案继续由计划02边界生成，避免凭据和内部异常穿透到 UI。最后以真实 dialog DataContext 和隔离 WPF smoke 验证，而不是只测一个未被生产使用的 VM，才能证明用户点击路径、窗口关闭和副作用边界确实成立。

# 注意事项

- 前置计划状态是硬门槛：计划01、03必须先验收；执行时必须重新读取适用 rulebook、源码和调用方，公开符号变更必须先后使用 LSP `references`。
- 只编辑本计划所列文件和必要的既有治理/runner 事实；不得顺手重构 MainWindow、OMP、计费、provider adapter、持久化 schema 或发布脚本。不得运行 formatter、build、runner、smoke 或 publish（这些仅由后续执行本计划的 agent 按“验证步骤”运行）。
- Token、Cookie、密码、Authorization、完整 URL 查询参数、供应商响应和原始异常不得进入 settings、snapshot、备份、日志、异常、测试输出、UI 快照或仓库；测试只能使用合成凭据，且应断言敏感值不存在。
- 不得使用真实 Provider 网络或真实凭据；不得静默重试、自动切换、把认证失败/超时当成功或按错误文案字符串分支。复杂计费无法判断时必须沿既有结构化失败拒绝。
- 关闭窗口、用户取消和 probe 失败必须是可重复的有界状态转换；不能阻塞 UI、调用 `.Result`/`.Wait()` 或留下未观察任务。`AsyncCommand.Execute` 作为 WPF `ICommand` 框架边界的 `async void` 例外，业务方法仍必须返回 `Task`。
- 文档最终只记录已完成且已验证的事实；未执行的验证必须明确写出原因，不得把编译、进程存活或只测试 VM 属性描述为端到端通过。