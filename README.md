# BepInExSecondaryLoader

为 **Nuclear Option** 的 BepInEx 插件提供最大努力式的 Steam 创意工坊支持。
Best-effort Steam Workshop support for BepInEx plugins in **Nuclear Option**.

[中文文档](#中文文档) · [English Documentation](#english-documentation)

---

## 中文文档

### 简介

Nuclear Option 只内置了“任务”和“机模涂装”两类创意工坊内容。本 Mod 为它补上第三类：**BepInEx Plugin**。

玩家可以像订阅其它工坊内容一样订阅插件包，并通过“二级加载器”在游戏启动时从工坊目录直接加载插件，无需手动把 DLL 放进 `BepInEx/plugins`。

### 功能概览

| 功能 | 说明 |
| --- | --- |
| 新工坊条目类型 | 上传界面中的 `BepInEx Plugin` 类型，标签为 `BepInEx Plugin` / `BepInEx` |
| 包标记文件 | 包根目录中的 `Plugin.json`，其所在文件夹即 mod 包的根目录 |
| 完整目录上传 | 从包根开始保留全部子目录与文件结构 |
| 两阶段加载 | 启动时先按本地记录加载（不需要 Steam），Steam 就绪后再同步订阅并补载 |
| 加载顺序管理 | 面板内上移/下移调整优先级，依赖始终优先于用户顺序 |
| 预设 | 保存多套“启用插件组 + 顺序”，一键切换 |
| 配置导入/导出 | 以创意工坊 ID 索引、不含本机路径，可跨机器迁移；缺失条目自动下载 |

### 安装

1. 安装 **BepInEx 5.4.x**。
2. 将 `BepInExSecondaryLoader.dll` 放入 `BepInEx/plugins/`。
3. 启动游戏。

> 本 Mod 自身也是一个 BepInEx 插件，需要手动放置，不能通过工坊分发（否则形成循环依赖）。

### 用户指南

#### 管理面板

- 默认快捷键：`Ctrl + F9`。
- 可在 `BepInEx/config/experimental.assassin1076.BepInExSecondaryLoader.cfg` 的
  `[Interface]` → `Toggle Workshop Plugin Manager` 中修改。
- 面板只管理**工坊插件**；手动放进 `BepInEx/plugins/` 的插件由 BepInEx 自身的 chainloader 管理，不受此面板控制。
- 浏览与搜索请在 Steam 工坊中完成，订阅后即可在游戏内使用。

#### 加载顺序

列表**从上到下**即加载顺序（数值越小越先加载）。

- 独立的插件严格按你的顺序加载。
- 若 A 依赖 B，则 B 会排在 A 之前——依赖关系优先于手动顺序。
- 实际生效顺序会写入 `BepInEx/LogOutput.log`，便于排查。
- **顺序与启用状态的修改在下次启动游戏后生效**（已实例化的插件无法重排）。

#### 预设

- `保存当前为预设`：把当前“已启用的插件及顺序”存为一套命名预设。
- `更新当前预设`：仅在预设与当前列表不一致时可用，用于显式覆盖。
- `删除当前预设`：删除后回到手动模式。
- `(手动模式)`：编辑列表不会写入任何预设。
- 预设模式下新订阅的插件默认**不**启用；手动模式下新订阅的插件默认启用并追加到末尾。

#### 导入 / 导出

- 两个按钮都会弹出 Windows 原生文件对话框。
- 导出内容以**创意工坊 ID** 为主键，**不含任何本机路径**，可直接复制到另一台机器。
- 导入时按 ID 合并：已存在的更新状态与顺序，本地独有的保留并追加到后面。
- 导入文件同时兼容导出格式与本地记录文件 `workshop-plugins.json`。
- 遇到本地不存在的创意工坊 ID，会调用 `SteamWorkshop.DownloadItem` 自动订阅并下载，面板显示进度。
- 若 Steam 尚未初始化，下载会被跳过并给出提示，可在 Steam 就绪后重新导入。

<details>
<summary>导出格式示例</summary>

```json
{
  "FormatVersion": 1,
  "ActivePreset": "Combat",
  "Mods": {
    "1234567890": { "Name": "My Mod", "Enabled": true, "LoadOrder": 0 }
  },
  "Presets": {
    "Combat": [ { "WorkshopId": 1234567890, "LoadOrder": 0 } ]
  }
}
```

</details>

### Mod 作者指南

#### 包根目录与 Plugin.json

一个插件发行包必须有一个**根文件夹**，其中包含 `Plugin.json`：

- `Plugin.json` 所在的文件夹就是 mod 包的根目录。
- 根目录下的内容结构完全自由，可以包含任意嵌套文件夹与资源文件。
- 上传与加载都以“包根”为单位，整个目录树会被完整保留。

```
BepInEx/plugins/MyMod/        <- 包根（必须含 Plugin.json）
├── Plugin.json
├── bin/
│   └── MyMod.dll             <- 含 [BepInPlugin] 的入口程序集
├── lib/
│   └── SomeDependency.dll
└── assets/
    └── config/values.json
```

#### Plugin.json 字段

所有字段均为可选，最简内容可以是 `{}`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Name` | string | 工坊条目默认标题；省略时使用文件夹名 |
| `Description` | string | 工坊条目默认描述；省略时自动列出插件名、版本与 GUID |
| `Version` | string | 版本信息（仅记录，不参与加载逻辑） |
| `EntryAssemblies` | string[] | 相对包根的入口 DLL 列表；省略或为空时递归扫描整个包 |
| `Tags` | string[] | 追加到上传条目的自定义标签 |

> 未包含 `Plugin.json` 的工坊条目不会被识别为插件包，也不会被加载。

#### 上传流程

1. 打开游戏内置的工坊上传界面。
2. 类型选择 `BepInEx Plugin`——下拉框会列出 `BepInEx/plugins` 下所有被 `Plugin.json` 标记的目录（显示为相对路径）。
3. 选择目标包并上传，整个目录树会被完整写入 Steam 创意工坊。

#### 依赖与程序集解析

- `[BepInDependency]` 的硬依赖必须满足（游戏内已有或随包提供），否则该插件会被跳过并在日志中说明原因。
- 包内所有含 DLL 的子目录都会加入程序集解析搜索路径，因此 `bin/`、`lib/` 等嵌套布局可以正常引用依赖。
- 同一个 GUID 出现在多个包中时，保留版本较高者。

### 工作原理

1. **阶段一（插件启动时）**：读取本地记录 `workshop-plugins.json`，按用户顺序 + 依赖顺序加载已启用的插件。此阶段不依赖 Steam。
2. **阶段二（下一帧）**：BepInEx chainloader 完成后重试，加载此前因依赖未就绪而跳过的插件。
3. **阶段三（Steam 就绪后）**：枚举所有已订阅工坊条目，用 `Plugin.json` 识别插件包，刷新记录并补载本次尚未加载的插件。

上传时，游戏原本的暂存逻辑只会把文件复制到暂存目录根部；本 Mod 通过 Harmony 补丁创建目标子目录，从而支持嵌套结构上传。

### 文件位置

| 用途 | 路径 |
| --- | --- |
| 记录与预设 | `BepInEx/config/BepInExSecondaryLoader/workshop-plugins.json` |
| 插件配置项 | `BepInEx/config/experimental.assassin1076.BepInExSecondaryLoader.cfg` |
| 日志 | `BepInEx/LogOutput.log` |
| 工坊内容 | `<SteamLibrary>/steamapps/workshop/content/2168680/<WorkshopId>/` |

### 从源码构建

- 目标框架：`.NET Framework 4.6`；依赖 BepInEx 5.4.x。
- `BepInExSecondaryLoader.csproj` 中的 `HintPath` 指向本地游戏安装目录，构建前请按自己的路径调整。
- `Mono.Cecil`（0.10.4）与 `HarmonyX`（2.9.0）已固定到游戏运行时自带版本，且不随产物复制。

### 已知限制

1. 仅支持 BepInEx 插件包；基于 Blueprinter 的 `.nobp` 支持仍在讨论中。
2. 插件 DLL 位于 BepInEx 目录之外，若代码通过 BepInEx `Paths` 推断自身资源位置，需要格外小心。
3. 加载顺序、启用状态与预设的修改都在下次启动时生效。
4. 只管理本加载器负责的工坊插件，无法调整 BepInEx 自身提前加载的插件。
5. 硬依赖缺失会导致该插件被跳过（日志中有明确原因）。
6. 文件选择与路径处理按 Windows 设计。

---

## English Documentation

### Overview

Nuclear Option only ships two kinds of Workshop content: missions and aircraft liveries. This mod adds a third: **BepInEx Plugin**.

Players can subscribe to plugin packages like any other Workshop item, and a "secondary loader" loads them directly from the Workshop folder at startup — no manual copying into `BepInEx/plugins`.

### Features

| Feature | Description |
| --- | --- |
| New Workshop item type | A `BepInEx Plugin` type in the upload UI, tagged `BepInEx Plugin` / `BepInEx` |
| Package marker | `Plugin.json` in the package root; its folder **is** the package root |
| Full tree upload | The complete directory structure below the root is preserved |
| Two-stage loading | Loads from local records at startup (no Steam needed), then syncs subscriptions once Steam is ready |
| Load order management | Move items up/down; plugin dependencies always win over user order |
| Presets | Save multiple "enabled plugin set + order" profiles and switch between them |
| Config import/export | Keyed by Workshop ID with no local paths, portable across machines; missing items are downloaded automatically |

### Installation

1. Install **BepInEx 5.4.x**.
2. Drop `BepInExSecondaryLoader.dll` into `BepInEx/plugins/`.
3. Launch the game.

> This mod is itself a BepInEx plugin and must be installed manually — it cannot ship through the Workshop (that would be circular).

### Usage for Players

#### Manager window

- Default hotkey: `Ctrl + F9`.
- Configurable at `BepInEx/config/experimental.assassin1076.BepInExSecondaryLoader.cfg`,
  `[Interface]` → `Toggle Workshop Plugin Manager`.
- The window manages **Workshop plugins only**. Plugins you drop into `BepInEx/plugins/` yourself are handled by the BepInEx chainloader and are not listed here.
- Browse and search in the Steam Workshop as usual; once subscribed, the plugin is available in-game.

#### Load order

The list is loaded **top to bottom** (lower value loads first).

- Independent plugins load strictly in your order.
- If A depends on B, B is moved ahead of A — dependencies override manual order.
- The effective order is written to `BepInEx/LogOutput.log` for troubleshooting.
- **Order and enable-state changes take effect on the next launch** (already-instantiated plugins cannot be reordered).

#### Presets

- `Save current as preset` stores the currently enabled plugins and their order under a name.
- `Update current preset` is enabled only when the live list differs from the preset; it overwrites it explicitly.
- `Delete current preset` removes it and returns to manual mode.
- `(Manual mode)` means edits are not written to any preset.
- In preset mode, newly subscribed plugins start **disabled**; in manual mode they start enabled and are appended to the end.

#### Import / Export

- Both buttons open native Windows file dialogs.
- Exports are keyed by **Workshop ID** and contain **no local paths**, so they can be copied to another machine.
- Import merges by ID: existing entries get the imported state and order, local-only entries are kept and appended.
- The importer accepts both the export format and the local `workshop-plugins.json`.
- Workshop IDs not present locally trigger `SteamWorkshop.DownloadItem` (subscribe + download) with progress shown in the window.
- If Steam is not initialized yet, downloads are skipped with a hint; re-import once Steam is ready.

<details>
<summary>Export format example</summary>

```json
{
  "FormatVersion": 1,
  "ActivePreset": "Combat",
  "Mods": {
    "1234567890": { "Name": "My Mod", "Enabled": true, "LoadOrder": 0 }
  },
  "Presets": {
    "Combat": [ { "WorkshopId": 1234567890, "LoadOrder": 0 } ]
  }
}
```

</details>

### Usage for Mod Authors

#### Package root and Plugin.json

A release must have a **root folder** containing `Plugin.json`:

- The folder holding `Plugin.json` is the package root.
- Everything below it is free-form: nested folders and asset files are fine.
- Upload and loading both operate on the package root, so the whole tree is preserved.

```
BepInEx/plugins/MyMod/        <- package root (must contain Plugin.json)
├── Plugin.json
├── bin/
│   └── MyMod.dll             <- entry assembly with [BepInPlugin]
├── lib/
│   └── SomeDependency.dll
└── assets/
    └── config/values.json
```

#### Plugin.json fields

Every field is optional; `{}` is a valid manifest.

| Field | Type | Description |
| --- | --- | --- |
| `Name` | string | Default Workshop title; falls back to the folder name |
| `Description` | string | Default Workshop description; falls back to a generated plugin list |
| `Version` | string | Informational only; not used by the loader |
| `EntryAssemblies` | string[] | Entry DLLs relative to the package root; when omitted the whole package is scanned |
| `Tags` | string[] | Extra tags added to the uploaded Workshop item |

> Workshop items without `Plugin.json` are not recognised as plugin packages and are never loaded.

#### Uploading

1. Open the game's Workshop upload panel.
2. Pick the `BepInEx Plugin` type — the dropdown lists every `Plugin.json`-marked folder under `BepInEx/plugins` (shown as a relative path).
3. Select the package and upload; the whole directory tree is uploaded to Steam.

#### Dependencies and assembly resolution

- Hard `[BepInDependency]` requirements must be satisfiable (shipped in-game or bundled); otherwise the plugin is skipped with a logged reason.
- Every subfolder containing DLLs is added to assembly resolution paths, so layouts like `bin/` and `lib/` work.
- When the same GUID appears in multiple packages, the highest version wins.

### How it works

1. **Stage 1 (plugin startup)**: read `workshop-plugins.json` and load enabled plugins in user order plus dependency order. Steam is not required.
2. **Stage 2 (next frame)**: retry after the BepInEx chainloader finishes, picking up plugins skipped earlier due to unmet dependencies.
3. **Stage 3 (after Steam init)**: enumerate subscribed Workshop items, detect plugin packages via `Plugin.json`, refresh records, and load anything not loaded yet.

For uploads, the game's staging helper only copies files into the staging root; this mod patches it with Harmony so nested destination directories are created, enabling structured uploads.

### File locations

| Purpose | Path |
| --- | --- |
| Records and presets | `BepInEx/config/BepInExSecondaryLoader/workshop-plugins.json` |
| Plugin config | `BepInEx/config/experimental.assassin1076.BepInExSecondaryLoader.cfg` |
| Log | `BepInEx/LogOutput.log` |
| Workshop content | `<SteamLibrary>/steamapps/workshop/content/2168680/<WorkshopId>/` |

### Building from source

- Target framework: `.NET Framework 4.6`; requires BepInEx 5.4.x.
- `HintPath` entries in `BepInExSecondaryLoader.csproj` point to a local game install — adjust them before building.
- `Mono.Cecil` (0.10.4) and `HarmonyX` (2.9.0) are pinned to the versions shipped with the game runtime and are not copied to the output.

### Known limitations

1. BepInEx plugin packages only; Blueprinter `.nobp` support is still under discussion.
2. Plugin DLLs live outside the BepInEx directory, so code that infers paths from BepInEx `Paths` must be careful.
3. Load order, enable state and preset changes apply on the next launch.
4. Only Workshop plugins handled by this loader are managed; BepInEx's own early-loaded plugins cannot be reordered.
5. A missing hard dependency causes that plugin to be skipped (the reason is logged).
6. File dialogs and path handling are Windows-oriented.