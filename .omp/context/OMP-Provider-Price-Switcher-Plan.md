# OMP Provider 价格比较与手动切换启动工具

## 1. 项目目标

开发一个面向 Windows 11 的桌面 GUI 工具。每次启动或由用户点击检查时，工具自动获取已配置中转站的最新价格，统一以 `gpt-5.6-sol` 为基准，识别当前实际可用价格最低的 provider 并给出推荐。检查本身不修改 OMP 配置；用户在下拉框中确认或改选 provider 和 OMP 工作目录后，显式执行“切换并启动 OMP”。

工具还要检测目标模型是否出现比当前账户分组更低的新分组。程序不假定现有 API key 已获得该分组权限，只展示当前分组与更低分组的价格差异，并继续按已确认的当前分组比价。该警告不阻断用户手动切换；用户调整分组后可更新 `currentGroup` 并重新抓取。

## 2. 已确认需求

### 2.1 平台与交互

- 仅支持 Windows 11。
- 使用 Windows 桌面 GUI，不采用命令行作为主要交互。
- UI 技术采用 WPF。
- 目标框架确定为 `.NET 8 WPF`。
- 软件应保留后续增加价格历史、余额、延迟、稳定性、定时检查、托盘等功能的扩展空间。

### 2.2 比价规则

- 当前只比较模型 `gpt-5.6-sol`。

- New API 站点从 `{base_url}/api/pricing` 获取公开价格。

- 不能只比较 `model_ratio`。同一模型在各站通常具有相同的模型基础价，真正区分价格的是账户当前分组倍率。

- 普通输入价格的核心关系：
  
  `基础单价 × model_ratio × current_group_ratio`

- 输出价格还要乘 `completion_ratio`。

- 缓存读取价格还要乘 `cache_ratio`。

- 对同一个模型，当各站的 `model_ratio`、`completion_ratio` 和 `cache_ratio` 相同时，可以直接比较当前分组倍率。

- 实现中仍应保存并显示完整价格字段，不能假设所有站的模型定价永远一致。

### 2.3 站点配置

`name` 同时作为：

- GUI 显示名称；
- OMP `models.yml` 中的 provider ID；
- 调用 provider 切换逻辑时传入的目标 provider。

因此不重复配置 `provider_id`，其一致性由用户保证。

每个站点至少配置：

```json
{
  "name": "ekti",
  "baseUrl": "https://chat.ekti.cc",
  "siteType": "new-api",
  "currentGroup": "gpt-plus",
  "currentGroupRatio": 0.12,
  "enabled": true
}
```

字段说明：

- `name`：站点名称及 OMP provider ID。
- `baseUrl`：站点根地址，不带 `/pricing`、`/api/pricing` 或 `/v1`。
- `siteType`：站点适配器类型，第一版支持 `new-api`。
- `currentGroup`：该站 API key 当前实际所属分组，由用户维护和确认。
- `currentGroupRatio`：上次确认或抓取到的当前分组倍率，运行后自动更新。
- `enabled`：是否参与本次检查和选择。

全局配置至少包括：

```json
{
  "model": "gpt-5.6-sol",
  "requestTimeoutSeconds": 10,
  "ompConfigPath": "%USERPROFILE%\\.omp\\agent\\config.yml",
  "ompModelsPath": "%USERPROFILE%\\.omp\\agent\\models.yml",
  "ompWorkingDirectories": [
    "%USERPROFILE%\\.omp\\agent"
  ],
  "lastOmpWorkingDirectory": "%USERPROFILE%\\.omp\\agent"
}

API key 不写入比价工具配置。现有 key 继续由 OMP 的 `models.yml` 引用 `%USERPROFILE%\.omp\agent\.env` 中的环境变量。

## 3. New API 数据解析

第一版 New API 适配器读取：

- `data[].model_name`
- `data[].quota_type`
- `data[].model_ratio`
- `data[].completion_ratio`
- `data[].cache_ratio`
- `data[].billing_mode`
- `data[].billing_expr`
- `data[].enable_groups`
- `group_ratio`
- `usable_group`

处理步骤：

1. 请求 `{baseUrl}/api/pricing`。
2. 检查 HTTP 状态、JSON 格式和 `success`。
3. 精确定位 `model_name == "gpt-5.6-sol"`。
4. 只接受按 token 计费的目标条目；异常的 `quota_type` 或复杂计费表达式应明确标记。
5. 计算有效分组集合：`enable_groups ∩ group_ratio.keys`。
6. 在有效分组中读取 `currentGroup` 的实时倍率。
7. 更新本地 `currentGroupRatio`。
8. 在有效分组中查找最低倍率，不能从全站所有分组直接取最小值。
9. 保存输入、输出、缓存读取的标准化价格及抓取时间。

当返回 `billing_mode=tiered_expr` 或 `billing_expr` 时，不能盲目套用旧倍率公式。第一版应识别并显示表达式；如表达式包含 `service_tier=priority` 等条件，则普通请求和 priority 请求分开标识。

## 4. 当前分组与最低分组工作流

### 4.1 正常情况

当 `currentGroup` 存在且支持目标模型时：

- 更新当前倍率和标准化价格；
- 将站点加入跨站价格比较和 provider 候选列表；
- 若它是有效候选中的最低价，作为推荐 provider；同价时推荐保持当前 provider。

### 4.2 当前倍率变化

当公开接口中的当前分组倍率与配置值不同：

- GUI 显示旧值、新值和变化幅度；
- 自动把 `currentGroupRatio` 更新为接口中的实时值；
- 使用新值继续比较；
- 写入审计日志。

### 4.3 出现更低分组

当目标模型支持一个比 `currentGroup` 更低的分组：

- 在警告区显示站点、当前分组、当前倍率、最低分组、最低倍率和差价；
- 继续按 `currentGroup` 的实际标准化价格参与比较和推荐；不假定 API key 已获得更低分组权限；
- 此警告不阻断用户从有效 provider 下拉框中手动选择并执行切换；
- 用户在中转站调整 API key 分组后，可在 GUI 中更新或确认新的 `currentGroup`，再重新请求价格接口；
- 重新检查时确认新分组仍存在且支持目标模型，随后更新 `currentGroupRatio` 并重新执行完整比价。

公开 `/api/pricing` 一般不能证明某个 API key 实际属于哪个分组。因此，用户更新 `currentGroup` 是用户声明，程序只能验证分组及倍率存在，不能把它当成服务端权限证明。未来可为支持用户信息 API 的站点增加鉴权验证。

### 4.4 异常情况

以下站点不得作为推荐 provider，也不得出现在可切换 provider 下拉框中：

- 请求超时或 TLS/网络错误；
- 返回非预期 JSON；
- 找不到目标模型；
- 当前分组不存在或不支持目标模型；
- 价格字段缺失、为负数或无法解析；
- 复杂计费规则无法可靠标准化；
- provider 未在 OMP `models.yml` 中定义。

GUI 必须显示排除原因，不得静默回退到错误价格。

## 5. Provider 切换与启动 OMP

第一版将现有 `switch_provider.py` 的 `modelRoles` 替换语义迁入 C# 服务层，不依赖 Python 运行时。迁移实现必须覆盖并验证全部 `modelRoles`，而非做全文件字符串替换。

第一版集成流程：

1. 完成所有站点检查并过滤失效站点。
2. 按当前实际分组的标准化价格计算推荐 provider；价格相同时，推荐当前 provider。
3. 以推荐 provider 预选有效 provider 下拉框；用户可改选任一有效 provider。
4. 用户显式点击“切换并启动 OMP”后，才开始修改配置；仅完成检查不会自动切换。
5. 若用户选择的 provider 已是当前 provider，不改文件；否则 C# 切换服务验证目标 provider 后更新 `config.yml`。
6. 切换成功后，以用户从已保存常用目录下拉框选择的 OMP 工作目录启动 `omp`；启动前验证目录存在，并记住本次选择供下次预选。
7. 若已有 OMP 进程正在运行，不强杀当前会话；GUI 提示配置只会对新进程生效，并允许启动新实例或等待用户退出旧实例。

写配置必须具备：

- 原子写入；
- 修改前备份；
- 失败时保持原文件；
- 明确错误信息；
- 不在日志中记录 API key。

## 6. GUI 设计

主窗口采用工作型界面，不做营销首页：

- 顶部工具栏：检查价格、管理站点、切换并启动 OMP、打开设置。
- 主表格：站点、状态、当前分组、当前倍率、最低分组、最低倍率、输入价、输出价、缓存价、更新时间。
- 状态区：当前 OMP provider、推荐 provider、最近一次切换结果。
- 切换区：有效 provider 下拉框（推荐项预选）、OMP 工作目录下拉框和“切换并启动 OMP”按钮；目录列表可配置，记住上次选择并在启动前校验。
- 警告区：更低分组、倍率变化、模型缺失、抓取失败、复杂计费规则；更低分组仅作警告，不阻断手动切换。
- 站点编辑对话框：增删改站点配置并即时校验 URL 和必填字段。
- 分组编辑/确认对话框：展示更低分组差异；用户调整 API key 分组后可更新 `currentGroup` 并重新检查。

推荐和实际修改配置必须是两个可见阶段：检查只更新价格与推荐；仅用户显式点击“切换并启动 OMP”才修改配置。

## 7. 架构与扩展性

建议项目分层：

```text
ProviderPriceSwitcher.App
  WPF 窗口、ViewModel、命令和提示

ProviderPriceSwitcher.Core
  价格模型、比较规则、分组检测、选择决策

ProviderPriceSwitcher.Adapters
  IPricingAdapter
  NewApiPricingAdapter
  未来的 DeepKey 特殊适配器或其他站点适配器

ProviderPriceSwitcher.Infrastructure
  HTTP、JSON、配置持久化、日志、OMP 配置和进程控制
```

核心接口建议：

```csharp
public interface IPricingAdapter
{
    string SiteType { get; }
    Task<SitePricingResult> FetchAsync(
        SiteConfiguration site,
        string model,
        CancellationToken cancellationToken);
}
```

增加新站点框架时实现新的 `IPricingAdapter`，不修改核心比较流程。

后续扩展方向：

- 定时检查和 Windows 通知；
- 系统托盘和开机启动；
- 余额及充值汇率比较；
- API 实测延迟和健康检查；
- 价格历史和趋势图；
- 多模型或按实际 token 用量加权；
- 稳定性、失败率和价格综合评分；
- API key 实际分组验证；
- provider 配置完整性检查；
- 插件式站点适配器。

## 8. 实施阶段

### 阶段一：开发环境与项目骨架

- 安装 `.NET 8 SDK`。
- 创建 WPF 项目和分层工程。
- 建立配置 schema、错误模型和日志规范。
- 验证 Debug/Release 编译和最小发布产物。

### 阶段二：价格抓取与比较核心

- 实现 New API 适配器。
- 实现目标模型定位和有效分组交集。
- 实现当前分组倍率更新和最低分组检测。
- 实现复杂计费规则的拒绝或明确标记。
- 用 Ekti、Code28、DeepKey 的真实公开接口端到端验证。

### 阶段三：GUI 工作流

- 实现站点列表和编辑对话框。
- 实现检查进度、取消、超时和逐站状态。
- 实现更低分组警告、分组更新与重新检查流程。
- 实现推荐 provider、有效 provider 下拉选择、常用 OMP 工作目录管理和价格明细展示。

### 阶段四：OMP 集成

- 读取并验证 `models.yml` provider。
- 将现有 provider 切换语义迁入 C#，验证全部 `modelRoles`，移除 Python 运行依赖。
- 备份和原子更新 `config.yml`。
- 实现“切换并启动 OMP”、OMP 进程检测、常用工作目录选择与上次选择记忆。
- 端到端验证“检查 → 警告/推荐 → 用户选择 provider 与目录 → 切换 → 启动”。

### 阶段五：发布

- 发布 framework-dependent 和 self-contained 两种构建，比较大小及依赖。
- 验证 Windows 11 干净环境启动。
- 保留用户配置和日志在 `%LOCALAPPDATA%` 或 `%APPDATA%`，程序升级不覆盖数据。
- 提供版本号、错误日志入口和配置迁移机制。

## 9. 验收标准

- 用户可通过 GUI 配置、启用和停用多个站点。
- 程序能从三个已知站点获取 `gpt-5.6-sol` 价格。
- 程序只在该模型的有效分组中寻找最低倍率。
- 当前分组倍率变化后配置会自动更新。
- 出现更低分组时程序明确警告，但仍按已确认的当前分组比价，且不阻断用户手动切换。
- 用户更新 `currentGroup` 后程序重新抓取，不使用旧缓存直接继续。
- 无异常时推荐当前实际价格最低的 provider；同价时推荐当前 provider。
- 检查完成后不会自动修改 OMP 配置；用户可从有效候选中改选 provider。
- provider 切换失败时不启动 OMP，并保留原配置。
- 切换成功时按用户选择的有效工作目录启动 OMP，新进程读取新 provider；软件记住上次目录选择。
- 任一站点失败不会导致使用零价、陈旧价格或未授权分组，也不会进入有效 provider 下拉框。
- 日志不包含 API key。

## 10. DeepKey 调研记录（2026-07-21）

DeepKey 实际提供了 New API 风格的公开接口：

- `https://deepkey.top/api/status`
- `https://deepkey.top/api/pricing`

`gpt-5.6-sol` 的公开价格数据为：

```text
model_ratio      = 2.5
completion_ratio = 6
cache_ratio      = 0.1
```

该模型支持的分组包括：

```text
gpt-enterprise, gpt-kiro, gpt-kiro-power, gpt-openai,
codex, codex-k12, gpt, gpt-azure
```

其中当前公开最低倍率是：

```text
codex-k12 = 0.06
```

按 New API 的基准换算：

```text
输入：$5.00 × 0.06 = $0.3000 / 1M tokens
输出：$30.00 × 0.06 = $1.8000 / 1M tokens
缓存读取：$5.00 × 0.1 × 0.06 = $0.0300 / 1M tokens
```

使用真实浏览器在 DeepKey 定价页搜索 `gpt-5.6-sol` 后，页面明确显示：

```text
输入 $0.3000 / 1M tokens
输出 $1.8000 / 1M tokens
读缓 $0.0300 / 1M tokens
```

结论：公开证据不支持“DeepKey 没有缓存优惠”或“缓存按完整输入价收费”。相反，公开 API 和网页均声明缓存读取为普通输入价格的 10%。仅凭定价页不能证明每一笔账单实际严格按该规则结算；要判断是否存在实际多扣费，需要从 DeepKey 用量日志选取一笔包含 `cached_tokens` 的请求，对照输入、缓存、输出 token 数和扣费金额复算。

另外，DeepKey 公告显示 `codex-k12` 价格和可用性变动频繁，并曾标注“不保证稳定性”。价格最低不等于稳定性最高，这也是后续增加健康检查和稳定性评分的理由。

DeepKey 实际用量日志核账不纳入第一版；第一版以公开 API 和定价页声明的价格为比较依据。账单复算保留为后续独立功能。

## 11. 已确认的第一版决策

- 使用 `.NET 8 WPF`。
- 检查只生成价格结果和推荐，不自动切换；用户从有效 provider 下拉框选择后显式执行“切换并启动 OMP”。
- 更低分组仅作警告，不阻断手动切换；比较仍使用用户已确认的 `currentGroup`。
- 相同价格时推荐保持当前 provider，用户仍可改选其他有效 provider。
- OMP 工作目录维护为可配置的常用目录列表，启动前下拉选择并校验，软件记住上次选择。
- 第一版将 provider 切换逻辑迁入 C#，不依赖 Python；保持现有 `modelRoles` 替换语义，并增加备份、原子写入和失败保护。
- DeepKey 实际用量日志核账不纳入第一版。