---
description: 编码、安全、持久化、异步、日志与 WPF 行为约束
globs: ["**/*.cs", "**/*.xaml"]
---

# 编码与安全规范

本文件是当前代码的长期可执行规范。它只约束已经建立的项目边界和可验证行为；架构边界见 [architecture.md](architecture.md)，能力入口见 [reuse.md](reuse.md)，变更流程见 [change-workflows.md](change-workflows.md)，构建、runner、WPF 烟测和发布验收见 [build-release.md](build-release.md)。

## 规范事实来源

- `.editorconfig` 是机器可执行的格式事实来源。贡献者和工具 MUST 以它为准，不得在本文件复制或另造缩进、换行、编码、花括号和 C# IDE 规则。
- 源文件 MUST 保持 `.editorconfig` 要求的 UTF-8 BOM、CRLF、文件末尾换行；C# MUST 使用四个空格缩进、类型声明前换行花括号和文件范围命名空间。
- `.editorconfig` 与本文件出现冲突时，格式问题以 `.editorconfig` 为准；架构、安全、行为规则仍以本文件及其引用文档为准。格式化工具 MAY 自动修复格式，但不得借格式化顺带改变行为。
- 审查标准：对格式争议能定位到 `.editorconfig` 的具体规则；对行为规则能指出代码入口、调用方向或契约测试证据。

## 命名、API 与依赖边界

- 公共类型、方法、属性和事件 MUST 使用清晰的 PascalCase；局部变量和参数 MUST 使用 camelCase；缩写仅在领域已约定且可读时使用。布尔成员 SHOULD 以 `Is`、`Has`、`Can` 或 `Should` 开头。
- 类型名 MUST 表达职责而非实现细节；用例使用 `*UseCase`，适配器使用 `*PricingAdapter`，持久化实现使用明确的 `Json*Repository`，不得以 `Manager`、`Helper` 掩盖边界。
- 公共 API MUST 明确输入、输出、失败契约和取消语义；参数校验 MUST 在边界完成，引用参数使用 `ArgumentNullException.ThrowIfNull`，字符串和数值使用相应的空白/范围校验。
- Application 只能依赖 Core；Adapters 依赖 Application 与 Core；Infrastructure 依赖 Application 与 Core；App 才能组合 Application、Adapters、Infrastructure 与 Core。新增跨层调用 MUST 通过已有契约，禁止 UI 新增对 HTTP、JSON 文件或 Windows 凭据实现的直接访问。
- 新增依赖 MUST 通过构造函数注入；可替换的网络、持久化、凭据、进程、日志和路径能力 SHOULD 使用接口契约。现有 App 中 ViewModel/对话框直接接收或创建具体 Infrastructure 类型的代码是待收敛债务，MUST NOT 作为新代码模板；触及相应构造契约时 SHOULD 向 composition root 收敛。
- 新增公共符号前 MUST 搜索并迁移全部调用方；不得保留未使用的别名、兼容转发或重复入口。公共 API 的删除、签名改变和错误契约改变 MUST 在同一变更中更新调用方与契约 runner。
- 审查标准：依赖图符合 [architecture.md](architecture.md)；构造函数能看出外部依赖；公共 API 的每个失败分支和调用方均可追踪。

## 不可变模型与集合

- Core 领域模型 MUST 优先使用 `record`/`record struct` 和 `init` 属性；更新模型 MUST 使用 `with` 或新实例，不得原地修改共享快照、配置或推荐结果。
- 领域对象的必需字段 MUST 使用 `required`；可选值 MUST 以 nullable 类型显式表示，禁止用空字符串、魔法数字或默认对象伪装缺失状态。
- 公开集合 MUST 暴露 `IReadOnlyList<T>`、`IReadOnlyDictionary<TKey,TValue>` 或不可变等价视图；构造和持久化边界可创建具体集合，但不得把内部可变集合泄漏给调用者。
- 比较和排序 MUST 明确稳定规则、同价规则及字符串比较方式；标识符和配置键默认使用 `StringComparison.Ordinal`，不使用当前区域性比较来决定身份、匹配或推荐。
- 审查标准：检查对象更新是否产生新值；检查可空字段是否由调用者显式处理；检查集合是否可能被外部修改；检查同价和标识符比较是否有明确 comparer。

## null、验证与比较

- 可空引用类型 MUST 保持开启语义；`null` 是契约状态时必须显式声明并处理，不得以 `!` 消除警告来绕过验证。只有已证明不可能为空且紧邻证明时 MAY 使用 null-forgiving。
- 外部输入（JSON、HTTP、XAML 参数、文件路径、用户输入、凭据元数据）MUST 在进入领域或基础设施操作前验证格式、范围、长度、协议和路径约束。
- URL、模型名、ProviderId、分组和配置键的等同性 MUST 使用领域规定的规范化与序数比较；不得用 `ToLower()`、当前文化或模糊包含替代精确匹配。
- Decimal 金额、倍率和价格 MUST 进行非负/正数边界验证；未知计费表达式 MUST 明确失败或警告，不得猜测降级为简单倍率。
- 审查标准：每一个外部可控 nullable、枚举、数值、URI 和集合都有边界检查；比较表达式写出 comparer 或使用已建立的序数规则。

## 异步、取消与线程边界

- 可等待的业务操作 MUST 返回 `Task`/`Task<T>`，并 MUST 接收并传递 `CancellationToken`；下游网络、刷新和进程操作不得丢弃令牌。
- 超时 MUST 通过链接调用方令牌的 `CancellationTokenSource` 实现；调用方主动取消 MUST 保留取消语义，内部超时 MUST 转换为结构化的 Timeout 失败契约，而不是靠消息字符串分类。
- 业务代码 MUST NOT 使用 `async void`。仅 WPF 事件处理器等框架强制入口 MAY 使用 `async void`，并必须把异常导入已有的异步命令/通知边界；不得让异常逃逸到未处理异常处理器。
- 不得在异步操作中阻塞等待（如 `.Result`、`.Wait()`）；库代码 SHOULD 使用 `ConfigureAwait(false)`，UI 回调必须在更新绑定状态前回到 UI 上下文或通过已有 UI 调度边界。
- 并发刷新 MUST 允许单站失败而不吞掉整轮结果；共享状态更新 MUST 在明确的串行边界完成，避免并发写同一持久化文件。
- 审查标准：沿调用链能看到 token 传递；超时、主动取消和成功路径可分别断言；全仓库不得出现业务 `async void` 或同步阻塞等待。

## 结构化错误与通知

- 可预期失败 MUST 使用结构化错误类型、枚举或结果契约表达，例如适配器的认证失败、超时、无效响应和网络失败；调用方 MUST 按类型/枚举分支处理。
- 禁止通过比较、解析或拼接消息字符串来分类错误；错误消息只用于展示或诊断，不是程序协议。消息 MUST 不改变错误类别，也不得包含凭据或完整响应正文。
- 不可预期异常 MUST 在应用边界映射为稳定的错误类别/状态，并记录安全诊断；若跨层结果携带错误信息，只能携带稳定 kind/status 等字段。需要保留因果关系时可将原始异常作为 inner exception，但不得因此把异常文本暴露给用户或结果契约；不得 `catch (Exception)` 后静默忽略、伪装成功或自动切换 Provider。
- 用户通知 MUST 说明可行动结果（成功、失败、需重新导入、超时或旧快照），但不得暴露令牌、Cookie、Authorization 头、密码、完整 URL 查询参数或敏感响应内容。
- 检查流程只推荐，不自动切换；只有用户明确操作才执行配置切换和启动。审查标准：错误分支按结构化契约测试；搜索不到“按消息文本判断类型”的代码；通知内容可证明已脱敏且不触发隐式副作用。

## ILogger、日志与脱敏

- 日志 MUST 通过注入的 `ILogger`/日志契约或现有日志边界写入；业务层不得直接写文件、控制台或静态全局日志器。
- 日志 SHOULD 使用结构化参数和稳定事件语义；禁止用消息字符串承担错误分类。异常对象只有在日志 sink 明确不会序列化 message、Data 或 stack 中的敏感内容时才可传入；否则仅记录异常类型名与稳定字段。事件级别 MUST 与可恢复性相符。
- 敏感信息 MUST NOT 写入日志：访问令牌、API key、Cookie、Authorization、密码、凭据原文、完整请求/响应体、可能包含凭据的查询参数和真实用户私密路径。需要关联时只能记录稳定的 ProviderId、脱敏状态、错误类别、HTTP 状态和不敏感计数。
- 日志文件 MUST 遵循既有本地滚动日志边界并写入应用数据目录；日志失败不得破坏核心业务结果，但应在可用边界报告诊断信息，不得回退到泄露敏感数据的输出。
- 审查标准：对每个新增日志事件逐字段检查来源和敏感性；对异常和网络响应检查是否可能被隐式序列化；确认异常原文不会进入结果、通知或日志，日志路径可由隔离数据根测试验证。

## 凭据安全

- 凭据 MUST 使用 Windows 安全存储（当前 `ISiteCredentialStore` 实现）保存；MUST NOT 写入站点设置 JSON、价格快照、备份、日志、错误消息、UI 快照或仓库。
- 新代码中 Token、Cookie 和 Authorization 信息 MUST 只在需要的凭据输入或 adapter 调用边界短暂存在，不得复制到 ViewModel、快照、诊断对象或可序列化模型；UI 只能显示凭据状态、过期时间等摘要。
- 凭据的绑定、保存和清除只负责按 ProviderId 存取并校验站点类型，不等同于供应商验证；绑定动作 MUST NOT 声称凭据有效。真实有效性只在价格探测/适配器调用期间由供应商认证结果反映，并 MUST 映射为结构化认证状态。
- `SiteEditorDialog` MUST 只使用注入的 `ISiteCredentialStore.GetSummary` 展示中性状态；MUST NOT 调用 `LoadCredential` 或把 token/Cookie 原文回填到控件。更新成功后必须立即清空一次性输入，清除必须经明确确认。烟测不得使用真实凭据、真实 Provider 或真实用户 data root。
- 凭据传输 MUST 使用 HTTPS/TLS 端点；不得把令牌拼进日志、异常文本、URL 查询字符串或进程命令行。测试 MUST 使用合成凭据，并断言其不出现在持久化和日志输出中。
- 审查标准：搜索序列化模型和日志调用的凭据字段；验证凭据 store 是唯一持久化入口；区分绑定/保存与供应商认证；检查异常、备份和临时文件内容均无敏感值。

## 外部网络与真实副作用

- runner 与 WPF smoke MUST 默认使用 fake/loopback 网络、隔离 data root 和无真实凭据的测试数据；MUST NOT 自动访问真实 Provider、真实用户数据、真实凭据存储或生产 OMP 配置。
- 真实 Provider 的 live probe 默认禁止自动执行。只有用户明确授权时才可执行，且 MUST 使用非生产凭据/非主账户、最小权限、受控端点与受控次数；不得使用生产凭据，并 MUST 记录实际出网事实（端点范围、次数、时间和结果类别），不得记录秘密或完整响应。
- smoke 只验证隔离环境中的可观察契约；不得借 smoke 执行凭据操作、发布、配置切换或其他未明确授权的外部副作用。
---

## JSON 持久化、原子性与显式迁移

- JSON 序列化选项 MUST 集中于已有持久化边界；模型字段、枚举表示、大小写策略和版本兼容行为 MUST 可审查，不得在各业务调用点分散配置。
- 写入 MUST 先创建同目录唯一临时文件，以写穿方式完成序列化和 flush，再备份现有文件并原子替换目标；临时文件 MUST 在成功或失败后清理。不得直接截断覆盖生产 JSON。
- 读取损坏、格式错误或不支持的 JSON MUST 转换为结构化 `JsonDataException` 等数据错误并保留 inner exception；不得返回看似有效的空设置掩盖损坏。
- 数据迁移 MUST 显式、确定性、可重复安全且只在读取边界执行；每个旧字段/旧站点类型的迁移条件、目标值和不变条件 MUST 可由契约测试覆盖。不得用隐式猜测迁移未知数据。
- 保存模型 MUST 不含任何凭据字段；设置与快照必须分离，失败刷新保留旧快照并标记陈旧，不得用失败结果覆盖最后成功数据。
- 审查标准：断言临时文件清理、备份/替换顺序、损坏数据错误类型、旧格式到新格式的精确映射；检查迁移不会在每次保存中重复改变数据。

## WPF DataContext、Command、code-behind 与样式

- 使用 XAML Binding 的窗口/对话框 MUST 通过明确的 ViewModel 或现有应用工作流设置 `DataContext`；纯 programmatic code-behind 对话框可以不设 `DataContext`，但不得声明无数据源的 Binding。绑定属性和命令 MUST 可追踪到 UI 所属 ViewModel。
- ViewModel 的 `ICommand` MUST 负责可执行条件、执行中状态和异常/通知边界；异步操作 MUST 使用现有 `AsyncCommand` 形态或经审查的等价契约，禁止在业务 code-behind 中新增 `async void` 编排。
- code-behind SHOULD 只处理 WPF 生命周期、纯视图交互、输入校验、确认、焦点/窗口关闭和无法由绑定表达的 UI 细节；业务规则、网络、持久化、凭据和进程控制 SHOULD 经 Application/Infrastructure 契约执行。当前 programmatic 对话框和内部具体依赖属于待收敛债务，不得扩大。
- UI 异步操作 MUST 防止重复执行，完成或失败后 MUST 恢复命令或控件状态；取消和异常必须更新可见状态或通知，不得静默吞掉。UI 更新必须遵守 WPF UI 线程边界。
- 样式、模板和通用控件资源 MUST 集中在现有 `Styles.xaml`/资源边界；页面不得复制通用颜色、间距、按钮状态或字体规则。已有一次性布局值可以保留；新增可复用样式 SHOULD 使用既有资源键或在 `Styles.xaml` 增加语义化键。
- 审查标准：从 XAML 命令绑定可找到 ViewModel/DataContext；纯 code-behind 控件不声明悬空 Binding；不新增跨层业务分支；重复点击、失败、取消和窗口关闭均有可观察结果；通用样式来自集中资源。

## 测试契约与质量规则

- 测试项目是 console 契约 runner，不是 `Microsoft.NET.Test.Sdk` 项目；MUST 使用现有 runner 入口验证行为，MUST NOT 使用 `dotnet test` 作为替代。验证分级、命令和 runner 矩阵以 [build-release.md](build-release.md) 为唯一来源。
- 每个测试 MUST 保护可观察契约：输入边界、成功/失败转换、取消/超时、结构化错误类别、不可变更新、持久化原子替换、显式迁移、凭据不泄露、推荐不自动切换和 UI 命令状态。
- 测试 SHOULD 使用确定性 fake/stub、loopback 和临时隔离目录；不得访问真实站点、真实用户数据、真实凭据存储或真实 OMP 配置。敏感值测试必须是合成值，并断言日志、JSON、备份和错误消息不含该值。
- 测试断言 MUST 检查失败原因和边界，而非只检查“不抛异常”或消息片段；错误类别 MUST 通过类型/枚举/结果字段断言，禁止按消息字符串分类。
- 仅当新增或改变可观察契约且现有覆盖不足时，才 MUST 增加对应契约 runner 断言；纯重构或文档变更不得强加测试。重构若保持契约 SHOULD 保留覆盖边界的测试并删除过时断言。测试失败时只修复当前变更涉及的契约，不以放宽断言掩盖实现错误。
- 审查标准：每个新测试都能说明防止的具体回归；测试隔离且可重复；测试不会把网络、时间、线程调度或用户环境作为未控前提；runner 退出码和 `passed` 输出可作为验收证据。
