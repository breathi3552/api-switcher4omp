---
description: 常见架构、用例、设置、UI、OMP、日志与发布变更的执行流程
globs: ["**/*.cs", "**/*.xaml", "**/*.csproj"]
---

# 变更执行手册

本文件定义已经存在的架构边界之内，如何安全地实施和验收常见变化。边界、依赖方向和禁止的跨层调用以 [architecture.md](architecture.md) 为准；现有能力、注册入口和可复用契约以 [reuse.md](reuse.md) 为准。本文件只规定执行顺序、判断和完成判据，不重新定义架构，也不复制构建命令。

所有验证命令、固定 SDK、runner 矩阵、WPF 烟测和发布验收均 MUST 按 [build-release.md](build-release.md) 执行；本文只引用其影响分级，NEVER 以 `dotnet test` 替代 console runner。

## 通用执行规则

- 变更开始前 MUST 说明目标行为、影响层和回滚边界，并先阅读 Architecture Rules 与 Reuse Catalog；不得因为一个局部需求而改变二者规定的职责。
- MUST 先搜索现有入口和调用者，再决定新增还是复用；已有能力满足需求时 SHOULD 复用，NEVER 平行创建同义服务、异常、注册表或 UI 辅助类。
- 每个 playbook MUST 按“前置判断→实施步骤→必须验证→常见禁止做法”执行；实施中发现前置契约不成立时 MUST 停止本流程并先修复前置契约。
- 每项变更 MUST 建立范围基线：写明目标、允许触及的项目/文件和明确禁止顺手重构、依赖升级、发布；发现必要超范围时 MUST 先说明并重新确认范围。
- 验证按影响分级：文档-only 不跑代码验证；内部重构跑受影响 build/runner；行为/契约跑受影响 runner；WPF/OMP/外部副作用做隔离 smoke；完整八个 runner 仅跨层、发布候选或用户明确要求；publish 仅发布任务或用户明确要求。
- 通用 DoD MUST 包含：目标行为完成、受影响调用者迁移、适用验证通过、无真实数据副作用和无残留进程。证据格式 MUST 记录场景/命令、隔离 data/OMP root、结果、未执行项及原因、真实数据未触及和残留进程检查。
- 若变更改变规则归属、验证矩阵、项目事实、能力入口或安全/架构边界，MUST 更新对应治理文档；不得在 playbook 内复制另一规则的 canonical 内容。
- OMP 配置字段精确使用 `modelRoles` 与 `task.agentModelOverrides` 下的直接 provider/model 标量引用；不得把对象、数组或未验证的字段名当作现状事实。
- MUST 保持生产依赖图：Application→Core；Adapters→Application+Core；Infrastructure→Application+Core；App→Application+Adapters+Infrastructure+Core。验证结果未达到完成判据前 MUST NOT 发布。

## 新增 adapter

### 前置判断

- MUST 确认外部供应商或协议确实需要新的适配实现，并确认 Reuse Catalog 没有可复用的 adapter、descriptor 或 registry 入口。
- MUST 明确 adapter 提供的 Application 契约、支持能力、配置来源和失败类别；若需求需要改变公开契约，先按“修改公开契约”流程处理。
- MUST 确认实现只依赖允许的 Application/Core 契约，不把 WPF、Infrastructure 实现细节或 OMP 进程控制引入 Adapters。

### 实施步骤

1. 在 Adapters 中定义最小的供应商实现及其 descriptor；供应商协议映射、认证、超时和响应解析 MUST 留在 adapter 内。
2. 使用现有结构化失败契约表达认证、超时和可判断的供应商错误；不得把原始凭据、完整响应或不稳定异常文本泄露给上层。
3. 通过既有 registry/注册入口登记 adapter，补齐支持能力和配置校验；调用者 MUST 依赖契约或 descriptor，不得按供应商类型硬编码分支。
4. 仅在新增或改变可观察契约且现有覆盖不足时补充 runner 场景，覆盖成功、未支持能力、认证失败、超时和 malformed 响应等适用边界。

### 必须验证

- MUST 按 `rule://build-release` 的受影响级别构建并运行相关 runner；不得把局部 adapter 变更强制升级为完整八 runner。
- MUST 证明生产引用仍符合 Architecture Rules，且 registry 能发现新 adapter、未知 adapter 能产生可诊断失败。
- 若 adapter 影响 UI 可达路径或启动配置，MUST 额外执行指南规定的隔离 WPF smoke。
- 完成判据：实现、登记、失败映射和适用 runner 均通过，且无越层引用、凭据泄露或未处理的预期失败。

### 常见禁止做法

- NEVER 在 App 或 Infrastructure 中直接调用供应商 SDK。
- NEVER 为单个 adapter 添加绕过 registry 的静态特判、隐式 fallback 或自动切换行为。
- NEVER 用异常消息比较业务结果，或把供应商原始响应直接作为 UI 文本。

## 新增 Application 用例

### 前置判断

- MUST 确认需求是可复用的业务动作，而不是单一窗口事件处理；先在 Reuse Catalog 查找已有端口、查询、检查、通知或配置能力。
- MUST 列出输入、输出、失败契约、副作用和取消边界，并确认 Core 类型足以表达业务规则；缺少 Core 概念时不得在 App 临时造替代模型。
- MUST 确认用例不会反向依赖 adapter、Infrastructure、WPF 或 OMP 进程实现。

### 实施步骤

1. 在 Application 定义最小用例契约，输入输出使用 Core 或 Application 的公开类型；将编排与业务判断集中在用例内。
2. 通过既有 adapter/Infrastructure 抽象获取数据或执行副作用；依赖 MUST 显式注入，异步操作 MUST 支持取消并保持一致的失败语义。
3. 将可复用的检查、probe、站点设置或 OMP 操作接入现有入口；App 只负责绑定、生命周期和用户反馈。
4. 仅在现有覆盖不足时，为成功、失败、取消、空结果和重复调用补充 Application runner 场景；不在用例中加入 UI 字符串格式化。

### 必须验证

- MUST 按 `rule://build-release` 的受影响级别验证 Application 及所有受影响层；不得只运行单个项目的编译，也不自动要求完整八 runner。
- MUST 检查生产引用方向和调用者迁移结果；旧的 App 业务实现、重复别名或未使用入口 MUST 清除。
- 完成判据：用例可由非 WPF 调用者独立执行，输入输出和失败均可断言，取消不会产生未声明副作用，适用 runner 全部通过。


### 常见禁止做法

- NEVER 把窗口事件、MessageBox、Dispatcher 或配置文件路径写入 Application。
- NEVER 以“兼容”为由保留两套同义用例、旧入口或隐式全局状态。
- NEVER 在 Application 捕获所有异常后返回成功或空结果。

## 修改 settings schema

### 前置判断

- MUST 说明字段的语义、默认值、必填性、版本兼容和敏感性，并确认该字段属于设置 schema 而非运行时临时状态。
- MUST 查明读写、迁移、校验、UI 绑定、启动参数和发布配置的全部调用者；公开 schema 变化按“修改公开契约”流程同步判断。
- MUST 确认敏感字段的存储与日志策略，且不得把真实用户文件作为验证数据。

### 实施步骤

1. 在现有 schema 模型和解析入口中以最小差异添加、删除或修改字段；沿用既有未知字段、缺省值和错误处理策略。
2. 更新集中式校验、迁移和序列化路径；旧版本输入 MUST 有明确结果（兼容读取、可诊断拒绝或显式迁移），不能静默改变含义。
3. 更新 Application 用例及 App 绑定，保持设置层不承载业务决策；敏感值只在需要的边界解密/使用。
4. 更新契约 runner 覆盖合法、缺省、非法、旧 schema、未知字段和敏感值脱敏场景。

### 必须验证

- MUST 按 `rule://build-release` 的受影响级别运行相关 runner，并按该规则执行涉及启动或 UI 的隔离 smoke。
- MUST 使用临时 data root 验证读写，不得覆盖真实设置；确认重启后值保持且非法输入不会产生部分写入。
- 完成判据：schema、迁移、校验、调用者和 UI 行为一致，旧输入行为有证据，日志与错误输出不含敏感值。

### 常见禁止做法

- NEVER 通过字符串拼接或手工 JSON 复制绕过 schema 模型。
- NEVER 把密码、token、完整连接串写入日志、异常或 UI 诊断。
- NEVER 在缺少迁移规则时静默删除字段或把非法值替换成看似成功的默认值。

## 修改 WPF 界面

### 前置判断

- MUST 确认变化属于展示、交互、绑定或窗口生命周期，并定位其对应 Application 用例；业务规则变化 MUST 先回到 Application 流程。
- MUST 复用现有 AsyncCommand、通知和对话框边界，确认线程、取消、重复点击和关闭窗口时的行为。
- MUST 明确真实用户设置、凭据和 OMP 配置不可用于测试：准备隔离 data root，在 `settings.json` 显式设置临时 `OmpRootDirectory`，并另设合法的临时 `OmpWorkingDirectories`/`LastOmpWorkingDirectory`。当前凭据编辑器尚未沿用 `--data-root`，迁移完成前烟测不得执行绑定、更新或清除凭据。

### 实施步骤

1. 先调整 ViewModel/命令绑定，再调整 XAML；UI 只调用 Application 契约并把结构化状态转换为用户可理解的显示。
2. 保持异步操作在 UI 线程安全地更新状态，统一处理忙碌、取消、失败和可重试提示；不得在 code-behind 重建业务流程。
3. 对新增控件、对话框和设置项补充键盘、关闭、空数据和错误状态；所有用户可见文本沿用现有资源/风格约定。
4. 清理迁移后无调用的事件处理器、别名和重复状态，确保窗口关闭不会遗留后台任务或进程。

### 必须验证

- MUST 按 `rule://build-release` 的受影响级别执行验证；UI 变更实际启动应用完成受影响路径的隔离 WPF smoke，仅跨 UI 或发布候选执行完整 UI smoke。
- MUST 操作主窗口和受影响对话框路径；凭据编辑器在隔离债务修复前只验证无写入路径；确认日志只写入隔离目录。
- 完成判据：目标交互在真实启动进程中可操作，异步状态和错误显示正确，无真实用户数据副作用且无后台进程残留；任何无法安全隔离的写操作必须明确跳过并记录对应债务，不得伪称已验证。


### 常见禁止做法

- NEVER 在 XAML/code-behind 直接访问供应商 SDK、文件系统、OMP 进程或凭据。
- NEVER 用 `async void` 承载可复用业务动作，或吞掉任务异常。
- NEVER 以自动切换、静默重试或隐藏失败代替现有“只推荐不自动切换”规则。

## 修改 OMP 切换/启动

### 前置判断

- MUST 明确变化是 OMP 配置解析、进程启动、端口探测、切换请求还是停止/清理，并确认 OMP 实现边界属于 Infrastructure/OmpProcess，而非 UI。
- MUST 确认切换策略仍为显式用户意图；自动发现或建议不得变成自动切换。检查超时、退出码、占用端口和取消语义。
- MUST 复用 Reuse Catalog 中的 OMP 配置与进程入口，不得新增平行启动器。

### 实施步骤

1. 在既有 OmpConfig/OmpProcess 契约中修改最小的参数、生命周期或错误映射；将启动参数、环境变量和工作目录集中构造。
2. 使启动、探测、切换、停止和异常清理具有明确状态转换；失败 MUST 保留可诊断类别，取消 MUST 尝试有界清理。
3. Application 用例编排 OMP 操作，App 仅呈现状态和请求确认；真实进程的 stdout/stderr MUST 遵循日志脱敏规则。
4. 仅在可安全隔离且任务明确要求时执行配置切换或进程启动；未获授权时用 fake/loopback 验证状态转换。

### 必须验证

- MUST 按 `rule://build-release` 的受影响级别运行 OmpConfig、OmpProcess、Refresh 及其他受影响 runner。
- MUST 使用临时 OMP root/data root 做实际启动与停止 smoke；确认无自动切换、无真实配置污染、无孤儿进程。
- 完成判据：生命周期和失败状态可断言，启动/停止清理完成，显式切换才改变目标，隔离 smoke 成功。


### 常见禁止做法

- NEVER 把 OMP 进程调用散落在窗口事件或 adapter 中。
- NEVER 通过杀全部同名进程、无限等待或无界重试“修复”启动失败。
- NEVER 记录完整命令行、环境变量或包含凭据的进程输出。

## 修改日志字段或脱敏

### 前置判断

- MUST 先列出字段用途、稳定性、敏感级别、保留期限和所有消费方；确认变更不会把日志当作公开业务契约。
- MUST 复用现有本地日志入口和结构化字段命名，区分稳定诊断字段与用户可见错误；敏感字段必须有可验证的脱敏规则。
- 若外部工具依赖字段，先按“修改公开契约”流程评估兼容性，不得在日志层偷偷维持第二格式。

### 实施步骤

1. 在集中式日志入口添加、重命名或删除字段，更新字段构造和异常映射；字段值 MUST 来自受控上下文。
2. 对 token、密码、cookie、连接串、个人数据、完整请求/响应和路径中的敏感片段实施统一脱敏；脱敏后仍保留足够的类别、阶段和相关性信息。
3. 更新调用者和 runner 的日志断言，覆盖正常、失败、异常和空值；检查格式化不会在日志前发生泄露。
4. 清理旧字段、临时打印和重复 logger，确认日志目录仍由现有隔离配置控制。

### 必须验证

- MUST 按 `rule://build-release` 的受影响级别验证相关 runner；WPF/OMP 路径 MUST 进行对应隔离 smoke。
- MUST 以专门构造的敏感样例检查日志输出中不存在原文，并确认非敏感诊断字段仍可定位失败。
- 完成判据：字段来源、命名、脱敏和调用者一致，旧格式无意外残留，敏感值在异常和 stdout/stderr 路径均不可见。

### 常见禁止做法

- NEVER 使用“先完整记录、后处理”的异步脱敏方式。
- NEVER 在 catch、调试输出或 runner 失败消息中打印原始凭据和响应。
- NEVER 把日志 schema 变成跨层传递业务对象的替代品。

## 修改公开契约

### 前置判断

- MUST 明确契约消费者、语义变化、兼容要求和迁移窗口；先使用代码智能工具查找所有引用、实现和 re-export。
- MUST 判断变化属于 Core/Application/Adapter descriptor/配置 schema 哪一类，并确认 Architecture Rules 允许该契约所在层承载它。
- MUST 先分类消费者：仓库内源码契约默认 clean cutover；持久化 schema、外部 Provider API、已发布 CLI/扩展必须明确版本、兼容和迁移窗口。

### 实施步骤

1. 更新契约定义及其实现者，保持名称、类型、取消、错误和异步语义一致；删除已废弃的别名、重载和旧分支。
2. 按依赖方向迁移全部生产调用者、runner 和必要的配置/UI 绑定；不得通过 `object`、字符串或运行时反射逃避类型迁移。
3. 若跨进程或持久化边界变化，补充明确版本/迁移策略和拒绝错误；公开错误类别 MUST 稳定且不泄露内部细节。
4. 检查治理文档中的事实入口，仅在契约、能力或兼容策略发生变化时同步更新。

### 必须验证

- MUST 按 `rule://build-release` 的受影响级别运行所有受影响 runner，并以编译结果证明无遗漏调用者。
- MUST 验证成功、错误、取消、边界输入和兼容/拒绝路径；涉及 UI、OMP 或发布契约时执行指南对应 smoke 或发布验收。
- 完成判据：所有实现与调用者已迁移，旧契约无生产引用，行为和错误可断言，适用验证通过。


### 常见禁止做法

- NEVER 只修改接口声明而把实现者或调用者留到“以后”。
- NEVER 保留未使用的兼容 shim、别名或静默适配层来掩盖迁移不完整。
- NEVER 以扩大契约职责解决跨层耦合问题。

## 发布前检查

### 前置判断

- MUST 确认没有计划文件、临时脚本、真实用户数据、凭据、调试输出或未完成占位实现进入交付范围。
- MUST 确认发布是明确的发布任务或用户明确要求；普通文档/局部代码任务不得被升级为 publish。

### 实施步骤

1. 复核 Architecture Rules 的生产依赖图和 Reuse Catalog 的入口，确认没有新增越层依赖、重复能力或未登记实现。
2. 复核公开契约、settings schema、日志字段/脱敏、OMP 生命周期和 WPF 关闭清理；逐项对照本文件相应完成判据。
3. 按 `rule://build-release` 的环境检查、影响分级验证和（仅适用时）发布流程执行；命令只引用该规则，不在此文件复制。
4. 记录每项验证的场景/命令、退出码、关键成功输出、隔离目录、未执行原因、真实数据与残留进程检查；失败时 MUST 停止发布并修复根因后从受影响步骤重验。

### 必须验证

- MUST 证明适用影响级别的 build/runner/smoke 已通过；只有发布任务才完成两个发布目录验收。
- MUST 在发布前再次确认应用已关闭、没有孤儿进程，真实用户设置、凭据、OMP 配置和日志未被修改。
- 完成判据：所有受影响 playbook 的完成判据满足，适用验收项有记录；发布任务还必须证明两个发布产物可交付，否则 MUST NOT 发布。

### 常见禁止做法

- NEVER 并行 build、runner 或 publish；NEVER 以单个项目通过代替受影响验证。
- NEVER 在验证失败时跳过适用步骤、降低警告标准、删除产物解决文件占用，或直接发布旧产物。
- NEVER 把未验证的手工环境状态、真实用户目录或“本机可运行”作为发布证明。
