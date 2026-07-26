# .NET 构建、测试与发布规则

## 硬性规则

- MUST 在仓库根目录执行命令。
- MUST 使用 `C:\Users\breathi\.dotnet\dotnet.exe`，SDK 固定为 `8.0.423`（见 `global.json`）。
- NEVER 使用裸 `dotnet` 或 `C:\Program Files\dotnet\dotnet.exe`；该入口只有 Runtime/Host，没有 SDK。
- 测试项目是 console 契约运行器，不是 `Microsoft.NET.Test.Sdk` 项目。NEVER 用 `dotnet test` 代替测试；它只会 restore，不执行断言。
- build、测试和 publish MUST 顺序执行。并行命令会争用共享的 `bin/obj` 文件。
- WPF 行为变更 MUST 实际启动应用烟测；仅 build 或测试通过不等于应用能启动。
- NEVER 为解决文件占用强删产物。先停止正在运行的 `ProviderPriceSwitcher.App.exe`，再重试。

## 环境检查

```powershell
& 'C:\Users\breathi\.dotnet\dotnet.exe' --info
```

成功判据：输出 SDK `8.0.423`，Base Path 位于 `C:\Users\breathi\.dotnet\sdk\8.0.423\`。

如需让当前 PowerShell 会话中的 `dotnet` 指向正确入口：

```powershell
powershell -ExecutionPolicy Bypass -File eng/Initialize-Development.ps1
```

自动化和代理仍 SHOULD 使用上述绝对路径，避免依赖会话级 `PATH`。

## 固定验证链

先构建解决方案：

```powershell
& 'C:\Users\breathi\.dotnet\dotnet.exe' build ProviderPriceSwitcher.sln
```

再顺序运行全部六个契约 runner：

```powershell
& 'C:\Users\breathi\.dotnet\dotnet.exe' run --project ProviderPriceSwitcher.Core.Tests --no-build
& 'C:\Users\breathi\.dotnet\dotnet.exe' run --project ProviderPriceSwitcher.Adapters.Tests --no-build
& 'C:\Users\breathi\.dotnet\dotnet.exe' run --project ProviderPriceSwitcher.Infrastructure.Tests --no-build
& 'C:\Users\breathi\.dotnet\dotnet.exe' run --project ProviderPriceSwitcher.OmpConfig.Tests --no-build
& 'C:\Users\breathi\.dotnet\dotnet.exe' run --project ProviderPriceSwitcher.OmpProcess.Tests --no-build
& 'C:\Users\breathi\.dotnet\dotnet.exe' run --project ProviderPriceSwitcher.Refresh.Tests --no-build
```

成功判据：build 为 `0` 个错误，六个 runner 均以退出码 `0` 输出 `passed`。

WPF 变更还要启动应用并确认进程保持运行、主窗口出现且改动路径可操作：

```powershell
& 'C:\Users\breathi\.dotnet\dotnet.exe' run --project ProviderPriceSwitcher.App --no-build
```

烟测完成后正常关闭窗口，不保留后台进程。

## 发布

只有固定验证链通过后才发布。优先顺序执行：

```powershell
powershell -ExecutionPolicy Bypass -File eng/Publish.ps1
```

产物：

- `artifacts/publish/framework-dependent`
- `artifacts/publish/self-contained`

脚本失败时可顺序执行等价命令，NEVER 并行：

```powershell
& 'C:\Users\breathi\.dotnet\dotnet.exe' publish ProviderPriceSwitcher.App/ProviderPriceSwitcher.App.csproj --configuration Release --no-restore --property:PublishProfile=FrameworkDependent --output artifacts/publish/framework-dependent
& 'C:\Users\breathi\.dotnet\dotnet.exe' publish ProviderPriceSwitcher.App/ProviderPriceSwitcher.App.csproj --configuration Release --no-restore --property:PublishProfile=SelfContained --output artifacts/publish/self-contained
```

发布成功判据：两个目录均生成 `ProviderPriceSwitcher.App.exe`，命令退出码均为 `0`。

## 故障定位

- `A compatible .NET SDK was not found`：命中了系统 Runtime；改用用户目录绝对路径。
- `CS2012`、`MSB3021`、`MSB3027` 或文件占用：停止并行构建/测试/发布及运行中的应用，然后顺序重跑。
- `dotnet test` 只有 restore 输出：命令用错；改跑上述六个 console runner。
