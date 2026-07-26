# 闭环状态

closed

（2026-07-26）已完成。背景问题已消除，目标 1—6 全部达成，具备自主验证充分证据。

## 闭环证据

- **Publish fail-fast/新产物证据**：`eng/Publish.ps1` 对 framework-dependent 与 self-contained 各自立即捕获 `$LASTEXITCODE`；首阶段非零直接停止。每阶段先移除旧 exe，再要求本次生成的 `ProviderPriceSwitcher.App.exe` 存在、非空且写入时间不早于阶段开始；固定产物目录和 profiles 不变。
- **隔离 fake 故障注入**：`eng/fixtures/publish/self-check.ps1` 使用临时 fake dotnet 与输出根。首阶段返回 `17` 时脚本非零、调用次数为 `1`、无第二产物；预置陈旧 exe 且 fake 返回 `0` 但不写新产物时失败；双成功时调用次数为 `2` 且两目录均有非空 exe。自检输出 `publish fixture self-check passed`，未执行真实 publish。
- **runner 隔离与引用**：OmpConfig/OmpProcess runner 每次创建唯一临时 root，并派生 data/OMP/working root 与 run-id sentinel；OmpConfig 的合成 settings 显式指向隔离目录，备份/原子写入保持在 OMP root；OmpProcess 使用可控短生命周期 PowerShell fake 子进程，按精确 PID 跟踪、Kill/Wait/Dispose，最外层 `finally` 删除目录。两个 runner 直接使用 Infrastructure 公开类型，故保留各自唯一 Infrastructure `ProjectReference`，未新增 Core/Application 直引。
- **canonical 验证**：使用 `eng/Verify.ps1 -Impact Behavior -Runner OmpConfig,OmpProcess`，固定 SDK `8.0.423`，完成静态依赖检查、solution `0` warning / `0` error build，并依次输出 `OMP configuration runner passed.`、`OmpProcess contract tests passed.`，退出码 `0`。
- **安全与未执行项**：未访问真实 Provider/凭据，未启动真实 OMP，未修改真实用户 settings、`%USERPROFILE%\.omp` 或 LocalAppData，临时目录清理；未执行正式 publish、上传或发布产物替换。LSP 未配置，以源码调用图、项目引用、编译器和 canonical runner 证明调用者/引用。
- **三问结论**：背景问题已消除；目标 1—6 全部达成；fake 首失败/陈旧/双成功、隔离 runner 行为和 canonical Verify 提供充分自主验证证据。

# 背景

执行者必须先阅读并遵守 `rule://build-release`、`rule://change-workflows`、`rule://architecture`、`rule://coding` 和 `rule://reuse`，然后重新核实源码、项目文件、runner 调用方及当前脚本；不得假设本计划所列现状仍未变化。任何公开符号或构造契约变更，实施前必须用 LSP `references` 查找全部调用者并完成 clean cutover。本计划只允许修改以下范围：`eng/Publish.ps1`、实际承载 OmpConfig/OmpProcess runner 的 `*.csproj`/`Program.cs`，以及计划05明确授权的唯一 manifest/Verify 接入口文件；不得修改其他生产代码、规则或发布产物。当前任务只创建本计划，执行者不得在编写计划阶段运行 formatter、build、runner、smoke 或正式 publish。

现状证据（实施者必须重新确认并记录行号/提交前后差异）：

- `eng/Publish.ps1` 已解析用户目录 `.dotnet\dotnet.exe`，但连续调用 framework-dependent 与 self-contained 两条 `dotnet publish`，没有在第一条 native command 后立即检查 `$LASTEXITCODE`，也没有明确的逐产物成功证据或失败后阻断第二条的契约。
- `ProviderPriceSwitcher.OmpConfig.Tests/Program.cs` 当前创建临时配置目录并在 `finally` 删除；`ProviderPriceSwitcher.OmpProcess.Tests/Program.cs` 当前创建临时目录并在 `finally` 删除，且 `FakeGateway` 只替身网关、不证明真实 `LocalAppData`、`UserProfile` 或 `%USERPROFILE%\.omp` 未被触及。
- 两个 runner 当前各自声明对 `ProviderPriceSwitcher.Infrastructure/ProviderPriceSwitcher.Infrastructure.csproj` 的直接 `ProjectReference`；该引用是否真正被其源码使用、是否因类型边界而必要，必须按编译器/LSP/项目依赖图重新核实，不能仅凭现有 `obj` 依赖缓存判断。不得删除 runner 实际需要的唯一引用；目标是删除不必要的直接引用或改为最小测试替身边界，而不是复制生产实现。
- `OmpProcessService` 通过 `IOmpProcessGateway` 注入替身，现有 runner 使用 `Process.GetCurrentProcess()` 作为提示样例并伪造启动成功；因此隔离证明必须覆盖可控 fake/loopback、进程句柄清理和根目录哨兵，而不把“runner 自身存活”当作无副作用证据。

影响层：发布脚本行为、测试 runner 工程边界、Infrastructure/OMP 外部副作用验证，属于跨层/发布候选质量门槛；代码完成后按 `rule://build-release` 的发布任务级验证执行，但本计划不得要求或执行正式 publish。计划05是前置依赖：若计划05验收未完成，先停止，不平行创建或复制 manifest/Verify 入口；若已完成，唯一复用计划05定义的 manifest/Verify 入口。

# 目标

1. `eng/Publish.ps1` 对每一条 native `dotnet publish` 独立捕获并检查 `$LASTEXITCODE`；第一条失败立即停止，第二条不得执行；脚本最终退出码不能掩盖任一失败。
2. framework-dependent 与 self-contained 仍分别输出到 `artifacts/publish/framework-dependent` 和 `artifacts/publish/self-contained`，并产生可审计的“双产物证据”：每条命令的顺序、退出码、目标目录以及目标目录内 `ProviderPriceSwitcher.App.exe` 存在且不是空文件。第一条失败时不得创建第二产物的成功证据。
3. 仅移除 OmpConfig/OmpProcess runner 不必要的 `ProjectReference`，保持 runner 可独立执行、仍能验证真实公开契约；禁止让测试依赖反向进入生产项目，禁止把生产实现复制进 runner。
4. 两个 runner 共享一次创建的临时 `dataRoot`、`ompRoot`、`workingRoot`（每次运行唯一、位于系统临时目录），在 `finally` 中有界清理；配置显式指向这些 root，并写入 sentinel 以检测越界写入。
5. OMP 启动路径只使用 fake/loopback，绝不访问真实 Provider、真实凭据、真实 `%USERPROFILE%\.omp`、`LocalAppData` 或 `UserProfile`；验证结束后无残留进程、无残留临时目录，且 sentinel/快照证明真实目录未被改动。
6. 若计划05已验收，计划06只消费其唯一 manifest/Verify 入口，不新增第二套 manifest、验证脚本或旁路成功判定；若计划05未完成，计划06不得假定其接口并须先等待前置验收。

不变量：两条 publish 串行；首失败短路；两个固定产物目录不变；SDK 入口固定为用户目录 `.dotnet\dotnet.exe`；验证不使用真实 Provider/凭据；临时 root 不得退化为默认值；`finally` 必须执行；无旧入口、旁路验证或重复 ProjectReference。

# 详细步骤

## 代码改动项

1. **前置核验与范围锁定**
   - 读取计划05文件和其验收结果；只有确认“唯一 manifest/Verify 入口”已完成，才把该入口作为本计划的唯一依赖。未完成时记录阻断并不修改计划05内容、不复制实现。
   - 重新读取 `eng/Publish.ps1`、两个 runner 的 `Program.cs`/`*.csproj`，用 LSP `references` 查找 `OmpConfigurationSwitcher`、`OmpProcessService`、`IOmpProcessGateway`、`OmpProcessStartRequest` 等公开符号调用方；检查完整 `.sln` 项目引用图。确认每个 `ProjectReference` 的直接使用者后，再决定删除不必要项。

2. **`eng/Publish.ps1` fail-fast**
   - 将每条 native publish 封装为明确的顺序步骤（可用本脚本内最小 helper，但不得引入第二个发布入口）：调用 `& $dotnet publish ...` 后立即把 `$LASTEXITCODE` 保存为局部值，输出结构化的阶段/目标/退出码证据，并在非零时 `throw`/返回非零；不得在两条命令之后才统一检查。
   - 先执行 framework-dependent；仅当退出码为 `0` 且其固定输出目录存在 `ProviderPriceSwitcher.App.exe` 且文件长度大于零时，才执行 self-contained。任一条件失败立即停止，第二条命令和其成功证据均不得出现。
   - self-contained 同样独立检查退出码和 exe 证据。脚本成功结束时只输出两条独立成功记录；失败时保留首失败阶段和退出码，不能被 PowerShell 后续语句覆盖。
   - 继续使用 `Join-Path` 和固定 SDK 路径；不要删除旧产物来“修复”失败，不得并行调用 publish，不得执行正式 publish 作为实施验证。

3. **可控失败命令替身验证入口**
   - 为脚本验证设计不触及生产目录的命令替身注入方式（例如在隔离临时目录提供 fake `dotnet.exe` 或可替换命令调用边界）；替身必须按调用次数记录参数、对第一次 publish 返回非零，并在第二次被调用时写入 sentinel/立即失败。
   - 只在验证脚本层证明：第一次 native publish 返回失败后第二次未被调用；成功场景由替身为两个输出目录写入非空 `ProviderPriceSwitcher.App.exe` 并返回 `0`，证明两条均有独立产物证据。该替身不得连接网络、读取凭据或写入真实用户目录。
   - 删除/清理旧的旁路成功判断、未使用 helper 和重复验证入口；若计划05提供 manifest/Verify，所有成功证据汇入该唯一入口，不新增第二份 manifest schema。

4. **OmpConfig/OmpProcess runner 项目引用瘦身**
   - 对照两个 `Program.cs` 的实际类型使用与 LSP 结果，删除仅由传递依赖或历史遗留造成的不必要 `ProjectReference`；若某一直接引用确实是 runner 使用的契约唯一来源，则保留并在计划执行记录中说明，不为“瘦身”强行删除。
   - 目标项目仍必须能以 console runner 形式编译和运行；测试替身放在测试项目内，不能把 fake 代码移入 Infrastructure，也不能将生产项目反向引用测试项目。修改后同步项目/solution 中无效引用和 runner 的 using，旧路径无生产引用。

5. **共享隔离 root、sentinel 与 finally**
   - 在 OmpConfig/OmpProcess runner 的共同测试装配边界创建一次唯一临时根，并派生 `dataRoot`、`ompRoot`、`workingRoot`；为每个 root 写入不可伪造的 sentinel（内容含 run id），在配置中显式设置 `OmpRootDirectory`、`OmpWorkingDirectories`、`LastOmpWorkingDirectory`，禁止依赖默认 `%USERPROFILE%`。
   - OmpConfig runner 只在 `ompRoot` 的 loopback 配置文件上执行 preview/switch/backup/atomic write，保留现有断言，并增加断言写入路径在 `ompRoot` 内、备份在隔离目录内、sentinel 未被破坏。
   - OmpProcess runner 使用 fake gateway 或 loopback executable；若启动真实子进程是验证必要条件，必须启动可控短生命周期的 fake，不得启动真实 `omp`/Provider，并在 `finally` 中先等待退出、按 PID 精确清理、确认无残留后再删目录。禁止按进程名杀全部同名进程。
   - 在最外层 `try/finally` 中执行检查：隔离 root 内无预期残留（允许在检查前收集证据），`LocalAppData`、`UserProfile`、真实 `%USERPROFILE%\.omp` 的 sentinel/基线未改变；清理失败必须令 runner 失败而非静默吞掉。不要在 runner 中记录凭据、完整环境变量或命令行敏感内容。

6. **调用方迁移与旧路径删除**
   - 迁移所有受影响 runner 的 `Main` 顶层代码、fake/loopback 工厂和项目引用；用 LSP references 复查公开符号没有遗漏调用方。删除旧的独立临时目录创建、默认 OMP root、真实 process gateway、旁路 manifest/Verify 和不再使用的 `ProjectReference`/using。
   - 不修改 OmpConfig/OmpProcess 生产实现，除非重新核实发现隔离契约无法从现有公开端口实现；若确需公开符号变更，必须扩大计划前先停工、记录影响并完成全部调用方 clean cutover。

## 其他改动项

- 仅在计划05已完成时，更新其既有唯一 manifest/Verify 的接入参数或证据字段，使其消费两条 publish 的退出码/非空 exe 证据；不得创建并行 Verify、重复 manifest 或新的发布入口。计划05未完成时不改其文件。
- 如上述行为改变了 `rule://build-release`、项目事实或验证入口，实施完成后才按治理归属更新相应规则/索引；本计划执行不得把治理文档改动与生产脚本/runner 混在同一边界内。
- 文档更新条件：只在已完成且已验证的事实发生变化时更新相关治理文档；不得提前声称 fail-fast、双产物、隔离 root 或无残留进程已成立。不得要求发布或修改发布说明。
- 非目标：不改变产物目录名、publish profile、应用功能、OMP 生产生命周期语义、真实凭据存储、Provider API、CI 触发策略或正式发布流程；不升级依赖、不重写测试框架、不增加自动切换/重试。

## 验证步骤

1. **脚本静态/替身契约验证（不正式发布）**
   - 在隔离临时工作目录使用可控 fake `dotnet` 命令替身；场景 A：第一次 publish 返回非零并写入 `first-failed` sentinel，第二次调用若发生则写 `second-called` sentinel。运行 `eng/Publish.ps1` 后断言脚本非零、输出含第一阶段独立退出码、`second-called` 不存在、第二产物成功证据不存在。
   - 场景 B：替身两次返回 `0` 并各写入非空 `ProviderPriceSwitcher.App.exe`；断言输出含两次独立退出码/目标目录/文件证据，两个目录路径保持不变。该验证只执行脚本和替身，不执行真实 `dotnet publish`，不接触真实用户目录或凭据。

2. **runner 行为与隔离验证**
   - 按 `rule://build-release` 的“行为/契约 + OMP 外部副作用”级别，使用固定 SDK 入口和根目录串行执行受影响的 OmpConfig/OmpProcess runner；不得使用裸 `dotnet`、`dotnet test`，不得并行 build/runner/publish。运行前后记录场景、命令、隔离 `dataRoot`/`ompRoot`/`workingRoot`、退出码和 `passed` 输出。
   - OmpConfig：断言 loopback 配置、备份、原子结果和 sentinel 全在隔离 `ompRoot`，真实 `%USERPROFILE%\.omp` 未创建/修改，真实 `LocalAppData`/`UserProfile` 基线未变化。
   - OmpProcess：断言 fake/loopback 只启动预期 PID，失败和停止路径均进入 `finally`，runner 退出前无残留子进程；确认隔离目录最终清理，真实 OMP/Provider 未启动。

3. **项目引用和适用质量门槛**
   - 以项目依赖图/LSP 结果证明不必要 `ProjectReference` 已删除、保留项均被直接使用、没有 Infrastructure→Adapters/App 或测试→生产反向违规；runner 的输出仍能断言公开契约。
   - 计划05已完成时，顺序调用其唯一 manifest/Verify 入口，确认 fail-fast 和双产物证据被同一入口消费；未完成时明确记录未执行原因，绝不平行创建替代入口。

4. **发布级验证边界**
   - 按 `rule://build-release`，此变更作为发布脚本/runner 质量改进，执行适用的 SDK 环境检查、受影响 build/runner/smoke 证据；若被纳入跨层发布候选，按规则顺序完成 build、格式验收、受影响/完整 runner 及隔离 smoke。所有命令串行，证据记录退出码、警告/错误、隔离 root、未执行项、真实数据未触及和残留进程检查。
   - **禁止正式 publish**：本计划的验收只用命令替身验证脚本控制流与证据契约；不调用真实 `eng/Publish.ps1` 进行发布、不上传、不替换正式产物。只有另一个明确发布任务才可在上行验证通过后执行真实双目录发布。

5. **回滚边界**
   - 若 fail-fast 或隔离 runner 验收失败，停止后续验证并回滚本计划改动到执行前版本；不得回退到“忽略第一条 `$LASTEXITCODE`”、默认真实 OMP root、真实 Provider 或保留旁路 Verify 的状态。若计划05唯一入口不满足前置验收，回滚本计划对其的任何接入，不修改计划05。

6. **目标—证据映射与自主闭环判断**
   - 执行者必须逐项填写目标 1—6 的证据映射：目标 1 对应场景 A 的 fake 首次非零、脚本非零退出及第二次未调用；目标 2 对应场景 B 的两条独立退出码、固定目录和两个非空 `ProviderPriceSwitcher.App.exe`；目标 3 对应项目依赖图/LSP 与 runner 运行结果；目标 4 对应共享唯一 `dataRoot`/`ompRoot`/`workingRoot`、每个 root 的 run-id sentinel 和 `finally` 清理；目标 5 对应 fake/loopback 的预期 PID、进程退出/精确清理、真实目录基线与 sentinel 未变化；目标 6 对应计划05唯一 manifest/Verify 的消费结果或其未完成阻断记录。
   - 对每个目标记录实际命令、隔离路径、退出码、关键输出和产物/快照路径；静态检查只能证明脚本/引用结构，不能替代 fake 故障控制、产物文件证据或 runner 行为证据。fake 脚本验证不得标记为正式发布成功，也不得把未执行真实 publish 写成已发布。
   - 验证结束后执行者必须明确回答：**背景问题是否消除？每项目标是否达成？是否具备自主验证充分证据？** 任一答案为“否”或证据缺失，闭环状态必须继续为 `open`，并记录具体阻塞/未通过项；不得改为 `closed`。
   - 只有代码改动、适用静态检查、fake fail-fast/双产物证据、受影响 runner 与隔离 smoke 均通过，且上述三个判断均为“是”，才允许把本节的 `open` 更新为 `closed`。2026-07-26 的独立发布任务已在上行验证通过后分别执行两条真实 publish，framework-dependent 与 self-contained 均退出码 `0`，两个固定目录的 `ProviderPriceSwitcher.App.exe` 均非空且可读取；这证明当前 SDK/打包环境可生成双产物，但不替代本计划尚未实施的 `eng/Publish.ps1` fail-fast 故障控制和 runner 隔离验收。
   - 独立真实发布验收点已完成；本计划仍保持 `open`，阻塞项是计划 05 前置、发布脚本首条失败时第二条不得执行的 fake 故障场景、逐产物证据契约和 OmpConfig/OmpProcess runner 隔离尚未实施。关闭前仍须按本计划目标 1—6 完成这些代码改动与验证；不得仅凭本次两次手工分开 publish 成功关闭计划。

# 为什么选择此技术路线

PowerShell 的 native 命令退出码只可靠存在于紧随调用之后的 `$LASTEXITCODE`；逐条保存并在第一条后短路，才能证明第二条没有被错误执行。以固定输出目录中的非空 `ProviderPriceSwitcher.App.exe` 作为第二层证据，可避免“退出码为零但产物缺失”或“只看脚本最终退出码”的误判。可控命令替身能覆盖失败控制流而不产生正式发布副作用。

runner 复用现有 `IOmpProcessGateway` 替身和 OMP 配置公开契约，在测试边界注入 fake/loopback，比修改生产服务或连接真实 Provider 更能隔离网络、凭据和进程风险。共享临时 root 加 sentinel、基线比较和最外层 `finally` 同时证明路径正确、清理完整、真实用户目录未被误触。先以 LSP/依赖图确认直接引用，再删除不必要 `ProjectReference`，避免用项目瘦身名义破坏 runner 的真实契约覆盖。复用计划05唯一 manifest/Verify 可避免两个成功判定来源漂移。

# 注意事项

- 本文件是实施计划，不是实施结果；新会话必须重新核实所有文件、调用方、计划05状态和规则版本。不得把现状证据直接当作无需复查的事实。
- 计划执行者必须先读适用 rulebook；公开符号变更先跑 LSP `references`。任何必要范围超出本计划时先停工，不得擅自修改生产 OMP 实现、规则或其他计划。
- 绝不访问真实 Provider、真实凭据、真实 `%USERPROFILE%\.omp`、`LocalAppData` 或 `UserProfile` 写入路径；不得把 token、cookie、密码、完整命令行/环境变量写入日志、manifest、快照或异常。
- `finally` 清理必须有界且可观察；不得强删被占用的正式产物，不得杀全部同名进程。发现残留进程时先按精确 PID 停止并记录失败，再判定 runner 失败。
- 不得并行 build、runner、publish；不得执行正式 publish。fake/loopback 验证失败不允许被“跳过”或降级成脚本最终退出码检查。
- 回滚仅限本计划所改脚本、runner 工程/程序和计划05的接入点；不得删除用户数据、真实配置、凭据或其他 agent 的未相关改动。
