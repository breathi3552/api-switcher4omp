---
description: 固定 SDK、构建、八个 runner、隔离 WPF 烟测与双目录发布验收规则
globs: ["*.sln", "**/*.csproj", "eng/**"]
---

# .NET 构建、测试与发布规则

## 硬性规则

- MUST 在仓库根目录执行命令。
- MUST 使用 `$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'` 解析 SDK 入口，SDK 固定为 `8.0.423`（见 `global.json`）；不得把某台机器的用户名或绝对用户目录写入仓库。
- NEVER 使用裸 `dotnet` 或 `C:\Program Files\dotnet\dotnet.exe`；该入口只有 Runtime/Host，没有 SDK。
- 测试项目是 console 契约运行器，不是 `Microsoft.NET.Test.Sdk` 项目。NEVER 用 `dotnet test` 代替测试；它只会 restore，不执行断言。
- build、格式验收、contract runner 和 publish MUST 顺序执行；并行命令会争用共享的 `bin/obj` 文件。普通文档-only 不跑代码验证；局部任务只跑受影响 runner；完整八 runner 仅跨层、发布候选或用户明确要求。
- WPF/OMP/外部副作用 MUST 使用隔离 smoke；真实 Provider 默认禁止自动访问，runner/smoke 使用 fake/loopback。仅用户明确授权才可 live probe，且不得使用生产凭据。
- NEVER 为解决文件占用强删产物。先停止正在运行的 `ProviderPriceSwitcher.App.exe`，再重试。

本指南只负责构建、格式、契约 runner、WPF 烟测和发布验收；架构边界遵循 [`architecture.md`](architecture.md)，能力入口遵循 [`reuse.md`](reuse.md)，源代码与安全/持久化/UI 约束遵循 [`coding.md`](coding.md)，变更步骤遵循 [`change-workflows.md`](change-workflows.md)。这些文档负责各自职责，本指南不复制其内容。

## 环境检查

```powershell
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
& $dotnet --info
```

成功判据：输出 SDK `8.0.423`，Base Path 位于当前用户目录的 `.dotnet\sdk\8.0.423\`。

如需让当前 PowerShell 会话中的 `dotnet` 指向正确入口：

```powershell
powershell -ExecutionPolicy Bypass -File eng/Initialize-Development.ps1
```

自动化和代理仍 SHOULD 使用上述 `$dotnet` 解析方式，避免依赖会话级 `PATH`。

## 影响分级验证矩阵

| 影响 | 必须验证 |
|---|---|
| 文档-only | 不跑代码验证；检查受影响文档的事实、链接与规则一致性 |
| 内部重构 | 受影响项目 build 与受影响 runner |
| 行为/契约 | 受影响 runner；涉及 UI/OMP 时加隔离 smoke |
| 跨层/发布候选/用户明确要求 | build、完整八 runner；受影响 UI 做隔离 smoke，跨 UI 才完整 UI smoke |
| 发布任务/用户明确要求 publish | 先完成上行验证，再独立 publish 和双目录验收 |

所有命令串行执行。证据 MUST 记录场景/命令、隔离 root、结果、未执行原因、真实数据与残留进程检查。

## 分级验证链

验证按影响分级串行执行，后一层不得替代适用的前一层：

1. **build 层**：内部重构或更高影响确认受影响项目；跨层、发布候选或用户要求确认解决方案为 `0` 个错误、`0` 个警告。
2. **contract runner 层**：行为/契约变更运行受影响 runner；完整八个 runner 仅跨层、发布候选或用户明确要求。
3. **UI/副作用层**：WPF/OMP/外部副作用使用 fake/loopback 与临时 root 的隔离 smoke；仅受影响窗口/路径必须验证受影响路径，跨 UI 或发布候选才做完整 UI smoke。
4. **publish 层**：独立于普通任务，仅发布任务或用户明确要求；完成适用 build/runner/smoke 后再生成并验收两个发布目录。

跨层、发布候选或用户明确要求完整验证时，MUST 调用 canonical 入口；runner 名称、项目路径和顺序的唯一清单是 `eng/verify-manifest.json`，依赖方向机器契约是 `eng/verify-dependency-matrix.json`：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File eng/Verify.ps1 -Impact CrossLayer -AllRunners
```

局部内部/行为变更必须显式选择 manifest 中的受影响 runner，例如：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File eng/Verify.ps1 -Impact Behavior -Runner App
```

`Document` 只执行 manifest、solution 与依赖矩阵静态一致性检查，不调用 SDK/build/format/runner；`Internal`/`Behavior` 要求至少一个显式 `-Runner`；`CrossLayer` 必须使用 `-AllRunners`。成功判据：入口退出码为 `0`，完整 build 为 `0` 个错误和 `0` 个警告，所运行 runner 输出 `passed`。不得在规则、CI 或其他脚本复制 runner 清单。

## WPF 隔离烟测与行为验收

WPF 变更仅验证受影响窗口/路径，跨 UI 或发布候选才执行完整 UI smoke。所有 WPF/OMP/外部副作用 MUST 使用临时 data root、临时 OMP root 及 fake/loopback；真实 Provider 默认禁止自动访问。隔离 `settings.json` MUST 至少含一个站点，并 MUST 显式把 `OmpRootDirectory` 设为临时 OMP root；`OmpWorkingDirectories` 与 `LastOmpWorkingDirectory` MUST 另设为合法的临时进程工作目录。不得依赖缺省值，否则可能命中真实 `%USERPROFILE%\.omp`。

启动参数沿用现有 `--data-root`：

```powershell
& $dotnet run --project ProviderPriceSwitcher.App --no-build -- --data-root '<临时 data 目录>'
```

- SiteEditorDialog 只使用 composition root 注入的隔离 credential store，并且不得调用 `LoadCredential` 或回填原文。烟测不得使用真实凭据或真实 Provider；凭据更新/清除仅在任务明确要求且临时 data root、合成站点及 synthetic secret 均可证明时执行。
- WPF 验证 MUST 优先由 STA runner 直接驱动 `ICommand`/`Dispatcher`，读取 `DataContext`、控件绑定值和 ViewModel 状态；不得依赖 computer use、人工点击、截图或肉眼观察。需要独立进程 smoke 时，必须使用固定 dotnet 命令、隔离 data/OMP root、Win32 `EnumWindows`/`WM_CLOSE`、隔离日志、退出码和残留进程检查。
- 受影响路径的绑定成功 MUST 由可观察 UI 状态和日志/文件断言证明，不能只以进程存活判定；烟测完成后正常关闭窗口，不保留后台进程。


## 发布

Publish 独立于普通验证，仅发布任务或用户明确要求才执行。开始前 MUST 完成适用影响级别的 build、runner 和 smoke；发布命令按顺序执行，NEVER 并行。

首选脚本按顺序调用 framework-dependent 与 self-contained 两次 publish，但当前脚本未显式检查第一条 native command 的 `$LASTEXITCODE`；不得只凭脚本最终退出码判定两次发布都成功：

```powershell
powershell -ExecutionPolicy Bypass -File eng/Publish.ps1
```

产物目录 MUST 保持不变：

- `artifacts/publish/framework-dependent`
- `artifacts/publish/self-contained`

需要逐条可靠记录退出码，或脚本输出无法证明两次命令均成功时，应顺序执行以下等价命令，并在每条后检查 `$LASTEXITCODE`；NEVER 并行：

```powershell
& $dotnet publish ProviderPriceSwitcher.App/ProviderPriceSwitcher.App.csproj --configuration Release --no-restore --property:PublishProfile=FrameworkDependent --output artifacts/publish/framework-dependent
& $dotnet publish ProviderPriceSwitcher.App/ProviderPriceSwitcher.App.csproj --configuration Release --no-restore --property:PublishProfile=SelfContained --output artifacts/publish/self-contained
```

发布成功判据：两个目录均生成 `ProviderPriceSwitcher.App.exe`，两次 publish 均有退出码 `0` 的独立证据；应用已关闭、无残留进程，真实数据与凭据未触及。
## 故障定位

- `A compatible .NET SDK was not found`：命中了系统 Runtime；改用用户目录绝对路径。
- `CS2012`、`MSB3021`、`MSB3027` 或文件占用：停止并行构建/格式验收/runner/publish 及运行中的应用，然后按适用的分级验证顺序重跑。
- `dotnet test` 只有 restore 输出：命令用错；改跑上述八个 console runner。
- UI 进程存活但表格、站点或按钮状态不正确：不能视为烟测通过；检查隔离配置是否含站点、Provider 和工作目录是否存在，并重新完成 UI 行为层验收。
