# Build and Publish Guide

## 结论

本项目的标准 .NET 入口不是系统 `dotnet`，而是用户目录下的 `C:\Users\breathi\.dotnet\dotnet.exe`。

- 固定 SDK：`8.0.423`
- `global.json` 已锁定该版本。
- 历史脚本 `eng/Initialize-Development.ps1` 与 `eng/Publish.ps1` 都显式使用 `C:\Users\breathi\.dotnet\dotnet.exe`。

## 编译前检查

1. 确认以下路径存在：
   - `C:\Users\breathi\.dotnet\dotnet.exe`
   - `C:\Users\breathi\.dotnet\sdk\8.0.423\`
2. 不要优先使用 `C:\Program Files\dotnet\dotnet.exe`；该路径在本机可能只有 host/runtime，没有 SDK。
3. 如需初始化环境，先运行：

```powershell
powershell -ExecutionPolicy Bypass -File eng/Initialize-Development.ps1
```

该脚本会：
- 设置 `DOTNET_ROOT`
- 把 `C:\Users\breathi\.dotnet` 放到 `PATH` 前面
- 验证 SDK 版本

## 标准编译命令

在仓库根目录执行：

```powershell
C:\Users\breathi\.dotnet\dotnet.exe build
```

## 标准测试命令

本仓库当前至少验证以下契约测试：

```powershell
C:\Users\breathi\.dotnet\dotnet.exe run --project ProviderPriceSwitcher.Adapters.Tests
C:\Users\breathi\.dotnet\dotnet.exe run --project ProviderPriceSwitcher.Infrastructure.Tests
C:\Users\breathi\.dotnet\dotnet.exe run --project ProviderPriceSwitcher.Refresh.Tests
```

如需更完整验证，可继续补跑其他 `ProviderPriceSwitcher.*.Tests` 项目。

## 标准发布命令

优先方式：

```powershell
powershell -ExecutionPolicy Bypass -File eng/Publish.ps1
```

该脚本会发布两套产物：
- `artifacts/publish/framework-dependent`
- `artifacts/publish/self-contained`

等价的手动命令：

```powershell
C:\Users\breathi\.dotnet\dotnet.exe publish ProviderPriceSwitcher.App/ProviderPriceSwitcher.App.csproj --configuration Release --no-restore --property:PublishProfile=FrameworkDependent --output artifacts/publish/framework-dependent
C:\Users\breathi\.dotnet\dotnet.exe publish ProviderPriceSwitcher.App/ProviderPriceSwitcher.App.csproj --configuration Release --no-restore --property:PublishProfile=SelfContained --output artifacts/publish/self-contained
```

## 已知注意事项

1. `framework-dependent` 与 `self-contained` 发布不要并行写入同一 `obj/Release`/输出目录；会出现文件占用。
2. 若 `self-contained\ProviderPriceSwitcher.App.exe` 被占用，先释放占用后再重跑，不要在未确认的情况下强删用户正在使用的产物。
3. 遇到“系统 dotnet 无 SDK”时，先检查是否误用了 `C:\Program Files\dotnet\dotnet.exe`。

## 本次已验证结果

本次修改已使用以下命令成功验证：

```powershell
C:\Users\breathi\.dotnet\dotnet.exe build
C:\Users\breathi\.dotnet\dotnet.exe run --project ProviderPriceSwitcher.Adapters.Tests
C:\Users\breathi\.dotnet\dotnet.exe run --project ProviderPriceSwitcher.Infrastructure.Tests
C:\Users\breathi\.dotnet\dotnet.exe run --project ProviderPriceSwitcher.Refresh.Tests
C:\Users\breathi\.dotnet\dotnet.exe publish ProviderPriceSwitcher.App/ProviderPriceSwitcher.App.csproj --configuration Release --no-restore --property:PublishProfile=FrameworkDependent --output artifacts/publish/framework-dependent
C:\Users\breathi\.dotnet\dotnet.exe publish ProviderPriceSwitcher.App/ProviderPriceSwitcher.App.csproj --configuration Release --no-restore --property:PublishProfile=SelfContained --output artifacts/publish/self-contained
```
