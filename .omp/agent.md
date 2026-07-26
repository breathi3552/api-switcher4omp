# ProviderPriceSwitcher 项目入口

Windows WPF/.NET 8 工具：查询供应商价格，按用量模板推荐最低价 OMP Provider；用户确认后才切换配置并启动 OMP。

## 先读

- 当前功能、已知问题和 TODO：`.omp/context/PROJECT-STATUS.md`
- 业务规则：`.omp/context/Business-Implementation-Plan.md`
- 原始方案：`.omp/context/OMP-Provider-Price-Switcher-Plan.md`
- 凡涉及 `.NET` 编译、测试、启动、发布、SDK 路径或 publish profile，MUST 先读并严格遵循 `.omp/context/Build-and-Publish-Guide.md`；NEVER 使用裸 `dotnet`。

## 代码入口

- `ProviderPriceSwitcher.Core`：价格、快照、成本和推荐规则
- `ProviderPriceSwitcher.Adapters`：供应商价格适配器
- `ProviderPriceSwitcher.Infrastructure`：刷新、持久化、OMP 配置与进程
- `ProviderPriceSwitcher.App`：WPF 界面和用户工作流
- `ProviderPriceSwitcher.*.Tests`：各层契约测试运行器

## 关键约束

- 检查只推荐，不自动切换。
- 仅本轮刷新成功且快照有效的站点可自动推荐。
- 主表必须保留当前组与最低组。
- 复杂计费不能猜测或降级成简单倍率。
- 控制台 Token 不得写入设置、快照、日志或仓库。
