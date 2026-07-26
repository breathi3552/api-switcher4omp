# 闭环状态

closed

> **最终闭环证据（2026-07-26）。** 目标 1：Application runner 证明 `Sites`/`OmpWorkingDirectories` 公共集合不可变、调用方数组后续突变不影响 settings、`with` 更新不污染旧实例，独立 WPF smoke 证明只读展示路径可启动并正常关闭。目标 2：静态搜索确认四个旧 refresh API 零引用，Refresh/App runner 证明 refresh 无持久化职责且快照由既有端口/用例承接。目标 3：Infrastructure runner 覆盖旧 settings/ompWorkingDirectories 数组读取、字段/顺序/形状往返、未知字段既有 ignore-on-save、迁移、损坏输入与原子写入。目标 4：文档字段检查 14 条、9 字段完整、27 个相对链接全部存在，重复链接均指向同一 canonical 事实入口。目标 5：固定 SDK `8.0.423` 下 `eng/Verify.ps1 -Impact CrossLayer -AllRunners` 完成 build（0 警告、0 错误）、format 与八 runner passed；WPF smoke 使用临时 data/OMP/working root 和空合成 sites，窗口标题出现，WM_CLOSE 后 exit 0，隔离日志 1 份、无残留，临时目录删除。三问均有正面、可追溯证据：背景问题已消除、五项目标已达成、证据充分。未访问真实 Provider、凭据、用户 root 或 OMP，未执行正式 publish；不伪称视觉布局或正式 publish。

# 背景

本计划是七项治理工作的第 07 项，前置依赖为计划 03（composition root）必须已完成并通过其验收；执行者不得假设前置工作存在，开工时必须先读取 `rule://architecture`、`rule://reuse`、`rule://coding`、`rule://change-workflows` 和 `rule://build-release`，重新核实计划 03 的最终源码、装配注册、全部生产调用方及 runner 调用方。若计划 03 未完成或验收证据不成立，停止本计划，不重复实现计划 03 的装配迁移。

当前源码证据（以开工时重新读取结果为准）：

- `LocalAppSettings` 已将 `Sites` 和 `OmpWorkingDirectories` 收敛为不可变公共读取契约；调用方通过新集合与 `with` 更新，JSON 边界保持外部数组兼容。
- `PricingRefreshService` 已移除快照 repository 持有、四个旧转发 API 及内部持久化读写；快照由既有 Application 端口/用例负责。
- `ISettingsRepository`、`IPricingSnapshotRepository` 与 Infrastructure JSON 边界继续保持既有外部 schema、迁移、默认值和原子写入语义。
- 计划 07 的 canonical 文档、八 runner 矩阵与受影响调用方已完成迁移和验证。
- 计划 07 的治理边界不扩展供应商能力、不改变价格算法、不改变 OMP 配置 schema、不执行发布。

影响层：Application 公共模型、Application 刷新编排、Infrastructure 持久化边界、App composition root 与窗口调用方、Application/Refresh/App/Infrastructure console runner、`.omp/context/Project-Overview.md` 及必要的治理索引。该计划不扩展供应商能力、不改变价格算法、不改变 OMP 配置 schema、不执行发布。

前置计划状态：计划 03 必须先完成；计划 01/02/05/06 不作为本计划的代码前置，但其最终事实（特别是凭据隔离、错误脱敏、唯一质量入口和 runner 约束）必须在开工时核实并引用，不能在本计划中重复实现。

# 目标

1. 将 `LocalAppSettings.Sites` 与 `LocalAppSettings.OmpWorkingDirectories` 的公共读取契约收敛为只读/不可变集合：调用方只能枚举或通过 `with`/用例得到新 settings，不能通过公共属性原地增删改；所有生产和 runner 调用方完成迁移，旧的可变集合 API 和适配 shim 删除。
2. 将 `PricingRefreshService` 限定为价格探测、快照合并结果和推荐编排，不再暴露或转发持久化操作；删除 `LoadSnapshots`、`SnapshotRepository`、`DeleteSnapshot`、`SaveSnapshot` 及刷新服务内部的持久化读写。调用方改用既有 `IPricingSnapshotRepository` 或 `SiteManagementUseCase`/`PricingCheckUseCase` 等端口/用例，保持职责边界。
3. 保持持久化兼容：既有 settings JSON 的 `Sites`、`OmpWorkingDirectories` 字段名称、数组形状、缺省值、未知字段处理和 snapshot JSON schema 不变；只改变内存模型的集合暴露方式。旧 JSON 必须继续读取，写回仍生成兼容 JSON，不引入版本破坏、双写、静默字段删除或迁移 shim。
4. 建立唯一 canonical 质量债务登记和 runner/场景覆盖矩阵；每项至少包含 `owner`、`status`、`priority`、`evidence`、`DoD` 和相对链接，链接可解析且指向唯一事实入口。Project Overview 明确区分质量路线（架构、契约、安全、验证、治理债务）与产品路线（用户能力迭代），其他 rulebook 只保留规范和 canonical 文档引用。
5. 形成可观察验收闭环：集合不可变性、旧 API 零引用、刷新服务无持久化转发、兼容 JSON 往返、所有受影响 runner 场景、文档字段和链接一致性均有证据；不触碰真实 Provider、真实凭据、真实用户 data root 或真实 OMP root。

# 详细步骤

## 代码改动项

1. 前置基线与调用图
   - 先读取全部适用 rulebook 和计划 03 验收证据，记录允许修改的文件范围：`ProviderPriceSwitcher.Application/LocalAppSettings.cs`、`ProviderPriceSwitcher.Application/PricingRefreshService.cs`、必要的 Application 用例/契约、`ProviderPriceSwitcher.Infrastructure/JsonPersistence.cs`、App 的装配和窗口调用方、受影响 runner；禁止顺手升级依赖、改价格算法、改供应商协议或重构无关 UI。
   - 对 `LocalAppSettings`、`PricingRefreshService`、`IPricingSnapshotRepository`、`JsonPricingSnapshotRepository`、所有相关构造函数和四个待删除成员执行 LSP `references`；再用结构/文本搜索核对字符串反射、runner、测试替身和文档引用。输出迁移清单，未列出的调用方不得留到后续。

2. `LocalAppSettings` 只读/不可变集合契约
   - 在 `ProviderPriceSwitcher.Application/LocalAppSettings.cs` 将 `Sites` 和 `OmpWorkingDirectories` 的公共类型改为项目既有约定允许的只读/不可变表示（优先沿用现有项目已使用的 `IReadOnlyList<T>`/不可变快照模式；不得凭空并行引入第二种集合约定）。默认值必须仍为空集合，枚举顺序和重复项语义不变。
   - 确保反序列化入口能够构造该表示，并在 `JsonSettingsRepository.Load`/`ApplyDefaults` 边界完成一次受控 materialize；禁止让 JSON serializer、`List<T>` 的可变引用或调用方输入集合泄漏到公共 settings。若当前 serializer 需要私有 backing/转换器，只在现有 Infrastructure JSON 边界实现，不把 JSON 类型引入 Application。
   - 迁移 `SettingsUseCase`、`SiteManagementUseCase`、`SwitchAndStartUseCase`、`PricingCheckUseCase`、`MainViewModel`、`SettingsDialog`、`SitesDialog`、`SiteEditorDialog` 及 runner 中的列表操作：所有更新使用新数组/新列表再通过 `with` 生成 settings，不能对 `settings.Sites` 或 `settings.OmpWorkingDirectories` 原地修改。返回值和保存次数保持现有契约。
   - 删除为维持旧 `List<T>` 签名而新增的兼容属性、转换别名、可变暴露 backing field；不要保留隐式双向引用。确认 settings record 的 `with` 不会共享可被外部修改的可变集合。
   - 为不可变集合增加最小可观察 runner 场景（只在现有覆盖不足时）：构造后尝试通过公共契约修改应无法编译/不存在可变成员；用 `with` 更新不改变旧实例；站点顺序、目录去重和 `LastOmpWorkingDirectory` 规范化不变；空/缺省 settings 仍可安全读取。

3. 移除 `PricingRefreshService` 持久化转发
   - 在 `ProviderPriceSwitcher.Application/PricingRefreshService.cs` 删除字段 `_snapshots` 的持久化职责、构造函数中的 `IPricingSnapshotRepository` 参数及 `LoadSnapshots`、`SnapshotRepository`、`DeleteSnapshot`、`SaveSnapshot` 四个成员。构造函数改为只接收 `IPricingAdapterRegistry`、logger 和已有 recommendation 依赖，保持刷新输入/输出与取消语义。
   - `RefreshCoreAsync` 不再调用 repository 的 `LoadAll`/`SaveAll`。由既有 Application 端口/用例在正确边界负责快照读取、成功结果持久化和站点删除/重命名同步：`PricingCheckUseCase` 使用 `IPricingSnapshotRepository` 读取既有快照并在成功结果变更时按现有契约保存；`SiteManagementUseCase` 继续负责重命名/删除以及手动倍率快照更新；刷新服务只消费调用者提供的前置快照（如需调整，定义最小 Application 输入契约并一次性迁移所有调用者），不得在 service 内另造 repository。
   - 保持 `PricingRefreshResult.LatestSnapshots`、失败时保留旧快照、旧快照不得参与自动推荐、成功结果合并和取消不产生未声明持久化副作用等业务不变量。推荐判断必须仍基于本轮有效数据和调用者提供的旧快照，不能静默改为“刷新即保存”。
   - 更新 `App.xaml.cs` 的 composition root：分别装配 `JsonPricingSnapshotRepository` 与 Application 用例，禁止把 repository 作为 `PricingRefreshService` 参数；不得让 Infrastructure 反向依赖 App/Adapters。
   - 更新 `MainWindow.xaml.cs`：`MainViewModel`、`SitesDialog`、`SiteEditorDialog` 不再通过 `_refreshService.LoadSnapshots()`/`.SnapshotRepository` 读取或构造管理用例；它们改接收既有 `IPricingSnapshotRepository` 或由计划 03 已注入的 Application 用例/端口。UI 不得直接 `new` Infrastructure repository（若计划 03 已完成，则只消费其注入结果）。
   - 对 SiteEditor 的初始倍率、Sites 列表快照显示和站点管理保存路径分别迁移到快照查询端口/`SiteManagementUseCase` 的现有契约；不重复实现匹配、重命名删除或保存逻辑。完成迁移后删除所有旧调用和无效字段。
   - 更新 `ProviderPriceSwitcher.Application.Tests/Program.cs`、`ProviderPriceSwitcher.Refresh.Tests/Program.cs`、`ProviderPriceSwitcher.App.Tests/Program.cs` 及其他引用：测试替身直接注入新的最小契约；移除 refresh 构造时的 repository 参数和任何对删除成员的断言。保留 fake/内存 repository 只作为用例边界测试依赖，不把它塞回 refresh service。

4. 持久化兼容与边界验证实现
   - 在 `ProviderPriceSwitcher.Infrastructure/JsonPersistence.cs` 保留现有 settings/snapshot 文件路径、数组 JSON 形状、默认补齐、PawsAI/SevnX 等历史 settings 迁移和原子写入策略；只增加必要的集合读写适配，不改变字段名或历史值迁移语义。
   - 明确兼容策略：旧版本生成的 JSON 可读；读取后得到不可变内存 settings；保存再读取后字段、站点顺序、目录规范化和非敏感值等价；未知字段按原策略处理；非法 JSON/非法值仍按既有结构化错误和无部分写入规则处理。不得把集合类型名称写入 JSON schema，不得新增 schema version 但不提供迁移。
   - 若 serializer 无法直接处理只读接口，采用单一边界 DTO/转换路径并在 `JsonPersistence` 内转换，禁止在 Application 定义 JSON DTO，禁止双写旧/新字段或留下旧属性别名。用合成数据验证，不使用真实 settings、快照或凭据。

5. canonical 文档治理实现
   - 在 `.omp/context/` 建立唯一质量债务登记文件（文件名由开工时按仓库现有命名核实；若不存在则创建一个明确的 canonical 文件，例如 `Quality-Debt-Register.md`），登记本计划涉及的集合可变性、刷新服务持久化耦合、composition root 边界、凭据/错误脱敏等仍存在债务。每条记录固定字段：`id`、`owner`、`status`、`priority`、`evidence`、`DoD`、`scope`、`dependencies`、`last_verified`；`evidence` 与 `DoD` 必须是可点击相对链接或可复现命令/场景，不能写“以后补”。不将凭据、token、cookie、真实路径写入登记。
   - 建立唯一 runner/场景覆盖矩阵（可与质量债务登记同一 canonical 文档或明确单一链接的治理文档，禁止多份并行矩阵），按 Core、Adapters、Application、Infrastructure、OmpConfig、OmpProcess、Refresh、App 八个 runner 分列场景、owner、status、priority、evidence、DoD 和关联债务 ID。覆盖本次集合不可变性、JSON 旧 schema 往返、刷新成功/失败/超时/取消、快照由端口保存、旧 API 不存在/零引用、UI 只读展示与隔离启动路径；每个场景注明 fake/loopback 和临时 root 要求。
   - 更新 `.omp/context/Project-Overview.md`：在“当前已实现能力/质量与验证路线”中以最终源码事实描述只读 settings、刷新服务职责和实际 runner 覆盖；新增清晰的“质量路线”与“产品路线”区分。质量路线只列边界收敛、契约/安全、验证矩阵和治理债务；产品路线只列用户能力方向，不把计划中的治理工作描述为已完成产品能力。
   - 对 `architecture.md`、`coding.md`、`reuse.md`、`change-workflows.md`、`build-release.md` 只做必要规范引用或事实入口链接修正：不得复制质量登记、runner 全矩阵或项目路线；若既有内容与最终源码冲突，修改为规范性表述并链接 canonical 文档。文档 owner/status/priority/evidence/DoD 字段和相对链接保持唯一且一致。
   - 文档只能在代码收敛完成并由验证证据证明最终事实后更新；不可先改 Overview 声称功能完成。计划文件本身不要求发布，也不改变其他计划边界。

## 其他改动项

- 只允许编辑上述 Application、Infrastructure、App、受影响 runner 和 `.omp/context` canonical 文档/Overview；本次执行不得修改计划 01/02/03/04/05/06、生产之外的规则内容（除必要引用修正）、发布脚本或无关测试。
- 不新增第三方依赖、不修改外部 Provider API、不修改 settings/snapshot JSON 字段名或文件位置、不删除历史迁移逻辑、不改变推荐/成本/超时算法、不引入自动刷新或自动切换。
- 文档变更必须建立链接一致性检查：所有 `evidence`、`DoD`、owner/status/priority 字段完整；每个相对链接目标存在；计划、Overview、canonical 登记和 rulebook 的引用不互相循环或指向已删除旧 API；旧 `PricingRefreshService` 成员名称不得在“现状已实现”文档中残留。
- 若发现计划 03 的最终装配仍未完成，不能在本计划偷偷代做；登记阻塞证据并停止代码收敛，待计划 03 验收后重新核实。

## 验证步骤

1. 静态契约和链接核验：已完成旧 API 零引用与文档链接核验；27 个相对链接全部存在。
2. 跨层验证：使用固定 SDK `8.0.423` 执行 `eng/Verify.ps1 -Impact CrossLayer -AllRunners`，build 0 警告、0 错误，format 通过，八 runner passed。
3. runner/场景证据：Application、Infrastructure、Refresh、App 及其余 runner 均已通过；覆盖不可变 settings、旧 JSON 兼容、刷新成功/失败/超时/取消、快照端口边界、旧 API 零引用和隔离 WPF 路径。
4. UI/OMP 隔离 smoke：使用临时 data/OMP/working root、空合成 sites；窗口标题出现，WM_CLOSE 后 exit 0，隔离日志 1 份、无残留，临时目录删除。
5. 持久化兼容：Infrastructure runner 已证明旧 settings/snapshot JSON 可读取、写回字段/数组形状与顺序兼容，未知字段按既有策略处理，损坏输入和失败写入无部分写入。
6. 文档验收：14 条字段检查、9 字段完整、27 链接存在；Quality-Debt-Register 为唯一事实入口，Overview 质量路线和产品路线分离。
7. 安全与未执行项：验证使用合成数据、fake/loopback 和临时隔离 root；未访问真实 Provider、真实凭据、真实用户 data/OMP root，未执行正式 publish，未伪称视觉布局。
8. 目标—证据闭环映射：目标 1–5 均由上述静态检查、runner、JSON 往返、隔离 smoke 与文档检查覆盖。
9. 自主验证判断：背景问题已消除；每项目标已达成；具备充分且可追溯的自主验证证据。
10. 自动治理验收：固定字段、相对链接、状态和路线分离均已检查通过。
11. 关闭条件与未通过处理：不可变性、旧 API 零引用、刷新无持久化转发、JSON 兼容、文档闭环和隔离安全条件均已满足；真实外部边界与正式 publish 保持未执行。
12. 计划状态：本计划已 closed；后续变更需新增可观察证据，不回退为 open。

# 为什么选择此技术路线

- 只读/不可变集合解决的是 Application 公共模型的别名和原地修改风险，同时不改变 JSON 的外部表现；在 Infrastructure JSON 边界做单一转换可以把持久化兼容与内存契约分开，符合 `Application→Core`、`Infrastructure→Application+Core` 的依赖方向。
- 删除刷新服务的持久化转发而复用 `IPricingSnapshotRepository`、`PricingCheckUseCase` 和 `SiteManagementUseCase`，是 clean cutover：职责归属清晰、调用方可追踪、无平行 repository 或兼容 shim，并保持刷新失败/旧快照/推荐不变量。
- 先完成代码和可观察 runner 证据，再更新文档，避免治理文档把计划状态误写成源码事实；将质量债务和覆盖矩阵集中到单一 canonical 入口，Project Overview 只做路线导航，rulebook 只做规范，降低重复和链接漂移。
- 依赖计划 03 的 composition root 验收后再迁移调用方，可避免在窗口内部继续创建 Infrastructure 实例；所有跨层行为用现有八 runner 和隔离 smoke 验证，既满足质量证明，又不触碰真实 Provider、凭据和用户配置。

# 注意事项

- 不得把“只读”误实现为仅返回 `IReadOnlyList<T>` 但底层仍由外部持有的可变 `List<T>`；必须证明公共 settings 不可通过别名原地改变，更新返回新集合/新 record。
- 不得为了让 serializer 方便而把 `List<T>` 重新暴露为公共属性、增加旧属性别名、双写字段或改变 JSON 数组字段；外部 schema 兼容优先于内部类型便利。
- 不得保留 `LoadSnapshots`、`SnapshotRepository`、`DeleteSnapshot`、`SaveSnapshot` 的空实现、弃用标记、转发别名或反射入口；删除前必须迁移全部调用方并用 LSP references 证明零引用。
- 不得让 `PricingRefreshService` 重新获得文件、JSON 或 repository 依赖；刷新服务不能因 UI 读取方便而承担快照持久化。
- 不得把旧快照自动推荐、刷新失败静默保存、无效分组猜测、自动切换 OMP、真实 Provider live probe 或真实凭据操作加入迁移。
- 所有 token/Cookie/密码、真实路径和生产配置均不得进入计划证据、日志、快照、JSON、备份、异常文本或仓库；证据使用合成值并脱敏。
- 计划 01/02/05/06 的专属代码和脚本边界不得重复实现；质量登记可以引用其最终证据，但 owner/status 必须反映真实完成状态。计划 06 若仅涉及发布脚本质量改进，也只能验证脚本，不执行正式 publish；本计划不要求发布。
- 文档更新条件是“最终源码事实已完成并验证”，不是“计划已写完”；任何未完成项必须保留 `status`、阻塞证据和明确 DoD，不得用“基本完成”掩盖缺口。
