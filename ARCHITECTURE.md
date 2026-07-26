# MystiaAI 架构文档（供外部 Agent 审查）

> 本文档目的：让另一个 Agent 快速理解项目全貌，并重点协助排查**当前未决的夜晚场景启动闪退问题**（见第 6 章）。
> 写于 2026-07-26，对应代码即仓库当前状态。

## 1. 项目目标

《东方夜雀食堂》（Touhou Mystia Izakaya）的 BepInEx mod：把 NPC 的固定文本替换为 AI 实时生成的文本。

已确认的需求边界：

- **要替换**：白天地图上 NPC 闲聊（含玩家自由输入 + AI 回复选项）、营业中稀客闲聊、上菜后稀客评价语
- **不碰**：剧情对话、羁绊升级对话、稀客顶仓（bark）气泡、点单对话、普通客人评价
- AI 每次重新生成；失败/超时回退原文（评价语计划加角落灰色小字标注，未做）
- 上下文：人设（personas.json）、羁绊等级、场景、游戏内时间、当日报纸流行情报、评价语另加菜品名+等级
- 配置：DeepSeek 等 OpenAI 兼容 API（key 在 BepInEx 配置文件）；后续要做网页配置 GUI（端口 8520）、流式显示——均未开始

## 2. 环境与工具链

- 游戏：`C:\Program Files (x86)\Steam\steamapps\common\Touhou Mystia Izakaya`，Unity 2021.3.28 **IL2CPP**
- 加载器：BepInEx 6.0.0-be.785（IL2CPP 版，CoreCLR .NET 6.0.7）
- mod 项目：`C:/Users/m1525/Desktop/MystiaAI`，net6.0，HarmonyX + Il2CppInterop
- 构建：`dotnet build -v q`，构建事件自动把 DLL 复制到游戏 `BepInEx/plugins/`。**游戏运行时 DLL 被锁，必须先关游戏再构建**
- 日志：游戏 `BepInEx/LogOutput.log`（崩溃前最后几行是定位关键）
- 崩溃转储：`C:\Users\m1525\AppData\Local\CrashDumps\Touhou Mystia Izakaya.exe.*.dmp`
- 转储分析：`tools/.venv-dbg/Scripts/python.exe tools/dump_parse.py <dmp>`（隔离 venv + minidump，可给出异常码/异常地址/模块映射/栈上地址所属 DLL）
- 游戏 API 反编译资料：项目 `docs/game-api.md` + `decomp/`（ilspycmd 产物）
- 用户存档状态：**当前存档在「夜晚营业中有未完成的评价」状态**——每次启动游戏，加载后游戏会立即自己调用一次 `GuestGroupController.PostEvaluation(Exbad, null, true, true)`，这是本轮崩溃调查的天然触发器
- 用户机器上同时装有联机 mod MetaMystia（共存原则：postfix 不改 `__result`，回调链式叠加不覆盖）

## 3. 文件清单与职责

```
src/
├── Plugin.cs                      插件入口 [BepInPlugin("cc.mystia.ai")]，Load() 里依次 Install 各 patch
├── Config/
│   └── Settings.cs                BepInEx 配置（API key/URL/模型/超时/文本长度/Enabled 等）
├── Core/
│   ├── IAiClient.cs               AI 客户端接口（非流式/流式/建议三方法）
│   ├── OpenAiCompatibleClient.cs  OpenAI 兼容实现（SSE 流式已实现；DeepSeek 注意 max_tokens≥512）
│   ├── FakeAiClient.cs            假 AI（调试用）
│   ├── GenerationContext.cs       生成上下文契约（Extra: transcript/characterKey/news/location/dish/rating...）
│   ├── PromptBuilder.cs           prompt 组装（白天/营业闲聊/评价语分支；语言自适应）
│   ├── PersonaStore.cs            人设库 → BepInEx/config/MystiaAI/personas.json
│   ├── NewspaperReader.cs         当日报纸（RunTimeScheduler.newsData 静态表取最新 day）
│   ├── PendingReplacement.cs      白天链路：单条对话的待替换状态机
│   └── PluginContext.cs           全局上下文（Log/Settings/ProviderPresets 等）
├── Patches/
│   ├── DayChatPatch.cs            白天取数点：GetCharacterChatData postfix 登记 PendingReplacement
│   ├── DialogPannelPatch.cs       ★核心★ 白天三层管线的消费层 + 主线程派发通道 + 覆盖层调度
│   ├── OpenDialogMenuPatch.cs     白天兜底：注入 overrideReplaceTextCallback（已双钉委托）
│   ├── MainThreadDispatcher.cs    主线程派发：纯 ConcurrentQueue，EventSystem.Update drain
│   ├── NightChatPatch.cs          夜晚取数点：稀客闲聊/评价语登记 PendingBubble + 气泡原地改写
│   └── NightDiagPatch.cs          【临时诊断】PostEvaluation 锚点 + 帧节流气泡 watcher
├── Reply/
│   └── IPlayerReplyChannel.cs     玩家回复通道接口（自由输入扩展预留）
└── UI/
    └── FreeInputOverlay.cs        白天对话的自由输入覆盖层（TMP_InputField + 2 个 AI 建议按钮）
```

## 4. 架构与数据流

### 4.1 白天闲聊链路（已稳定，用户实测通过）

```
GetCharacterChatData postfix (DayChatPatch)
    → 登记 PendingReplacement（包 key → 替换表）
OpenDialogMenu prefix (OpenDialogMenuPatch)
    → 注入 overrideReplaceTextCallback（兜底旧路径，委托已双钉）
DialogPannel.OnExecutingDialogLoopCore prefix (DialogPannelPatch)
    → 数据准备：NPC 段建懒生成状态；Self（主角）段分组（连续 Self 段归一组）
DialogPannel.ExecuteDialog prefix
    → 逐句拦截：NPC 句轮到才发起 AI 生成，未就绪显示「……」占位，到位原地替换；
      Self 组首句弹 FreeInputOverlay（玩家自由输入/AI 建议/Enter 确认/Esc 跳过），
      后续句置空并 panel.Interact(default CallbackContext) 自动推进
AI 回调 → MainThreadDispatcher.Post（纯托管队列）
    → EventSystem.Update postfix drain（主线程）→ 更新 TMP 文本
```

关键设计：

- **逐句懒生成**：transcript 里带玩家真实输入，上下文连贯
- **Self 段过滤**：`speakerIdentity.speakerType == SpeakerIdentity.Identity.Self` 保持原文
- **主线程预取**：游戏时间/报纸/语言等 IL2CPP 调用全部在主线程完成，线程池只碰托管字符串（血的教训，见 5.2）
- 失败/超时：占位符回退原文

### 4.2 夜晚链路与白天完全不同（调查定案）

营业闲聊/评价**不走 DialogPackage/DialogPannel 管线**：

- `SpecialGuestsController.OnRequestIdleDialog(out Transform)` → 直接返回 string（稀客闲聊）
- `GuestGroupController.OnRequestEvaluationDialog(EvaluationResult, out Transform)` → 直接返回 string（评价）
- 显示：`GuestsManager` 协程 → `ShowTargetDialog` → `NightScene.UI.GuestManagementUtility.DialogBoxUI` 气泡（评价用子类 `EvalulationBoxUI`，5 套皮肤）

postfix 必须立刻返回 string（等不了 AI），所以夜晚方案是**「原文先行 + 原地改写」**：

```
OnRequestXxxDialog postfix → 不改 __result（游戏立即显示原文气泡，失败天然回退）
    → 同帧主线程预取上下文，发起 AI 生成，登记 PendingBubble(原文+场景)
DialogBoxUI.SetMessage/SetMessageAsync postfix → 按原文匹配认领气泡实例
watcher（Task.WhenAny 超时）→ MainThreadDispatcher.Post 回主线程
    → 气泡活着且文本仍是原文 → tmp.text 原地改写为 AI 文本
```

### 4.3 自由输入覆盖层（FreeInputOverlay）

- 挂 RootCanvas 下，siblingIndex 插在游戏指针「Cursor」节点前（否则遮鼠标）
- 字体取 DialogPannel.context.font，中文 IME 可用
- **按钮不用 UnityEvent**（见 5.1），Poll 里轮询 `Mouse.current.leftButton.wasReleasedThisFrame` + `RectangleContainsScreenPoint` 命中检测
- 会话结束三重检测：面板销毁 / `gameObject.activeInHierarchy==false` / `OpenContext.DialogPackageToPlay==null`，任一命中 QueueClose 静默关闭（防快进残留）
- Poll 的泵在 EventSystem.Update postfix（不依赖面板存亡）；Close 经 dispatcher 延迟执行，OnGUI 内不做原生写

## 5. IL2CPP + Harmony 血泪教训（审查者必须逐条核对）

### 5.1 崩溃签名 A：`0xC0000005` @ `coreclr.dll+0x1d1fdd`

**含义：native 侧经 reverse P/Invoke 调用了已死/非法的 managed thunk，CoreCLR 内部访问冲突，无声闪退，managed 层无任何异常。** 历史上由以下原因分别触发过（每次修复一个，症状不变因为还有其他源）：

1. **按钮 onClick 的 managed 委托**：Il2CppInterop 把 managed lambda 转 native thunk，未钉住/转换损坏 → EventSystem 点击分发即崩。**钉引用无效（实证）**，最终方案：彻底绕开 UnityEvent，改轮询。
2. **SynchronizationContext 派发**：`UnitySynchronizationContext.Post(SendOrPostCallback)` 把 managed 回调交给 native 持有稍后回调，closure 未钉 → 崩。已整个删除该通道，改纯队列。
3. **patch 形态不对的 IL2CPP 方法**：①`TextMeshProUGUI.set_text`（Harmony 警告 "patch the declared method"）；②协程状态机 `MoveNext`（IEnumerator 接口方法，native 接口调度进 detour 即崩，哪怕状态机是 class）；③struct（Il2CppSystem.ValueType）实例方法。这几类 patch 挂上后，第一次被调用就崩，**连 postfix 第一行日志都打不出来**。
4. **嫌疑中（未证实也未排除）：postfix 声明接收原方法的 `out` 参数**（如 `out Transform`）→ 封送崩溃。已在 NightChatPatch 中移除该参数，**但移除后仍崩**（最新证据），说明要么不是它，要么还有别的源。

### 5.2 其他硬性约束

- **禁止从线程池调 IL2CPP API**（会写坏 GC 堆，同为 event 1023 崩溃源）：上下文一律主线程预取
- **崩溃签名 B：`0xC00000FD` 栈溢出**：在 postfix 内调游戏方法（`PeekOrders()`）触发游戏重入调用被 patch 方法自身 → 无限递归。教训：**postfix 里不碰游戏数据，重活延迟到帧 watcher 脱离原调用栈执行，并加防重入闸门**
- Harmony patch 注册方式：全部手动 `harmony.Patch` + `GetPatchInfo` 回读自检（启动日志可见每条注册结果）
- 日志即证据：BepInEx 日志实时 flush，崩溃后最后几行就是凶手坐标；埋点要打在可疑路径入口/出口

## 6. 夜晚场景启动即闪退（根因已定位，修复待用户实测）

### 6.1 现象

用户双击游戏 → BepInEx 控制台加载 → 无响应 → 进程死亡（进标题界面之前）。每次必现。
白天场景的一切功能在 05:53 构建上完全正常（用户完整玩过）。
**关键前提（用户补充）**：游戏流程是标题→选存档→进场景；当前存档是「夜晚营业中有未完成评价」状态。

### 6.2 日志（每次崩溃都停在同一位置）

```
[Info] NightChat 自检通过 ×4（OnRequestIdleDialog/OnRequestEvaluationDialog/SetMessage/SetMessageAsync）
[Info] MystiaAI-DIAG 自检通过 ×3（PostEvaluation×2 + EventSystem.Update）
[Message] Chainloader startup complete
[Info] [MystiaAI-DIAG] 命中 GuestGroupController.PostEvaluation [GuestGroupController] 参数(Exbad, null, True, True) (#1)
← 进程死亡，之后无任何日志
```

PostEvaluation 的 postfix 体已缩到最轻（计数+节流日志+存实例引用，防重入闸门，catch-all），**它打印完日志后正常返回了**——崩溃发生在它返回之后的某个环节。
注意：这次 PostEvaluation 是**游戏启动加载存档时自己补调的**（非玩家操作触发），这直接暴露了评价流程的后续调用链。

### 6.3 转储证据（11:39 最新，与之前多个 dmp 一致）

- 异常码 `0xC0000005`，异常地址 `coreclr.dll+0x1d1fdd`（=签名 A，死 thunk）
- 栈上能看到 `MystiaAI.dll` 映射区地址 + GameAssembly 帧 + coreclr 帧
- 分析命令：`tools/.venv-dbg/Scripts/python.exe tools/dump_parse.py "C:\Users\m1525\AppData\Local\CrashDumps\Touhou Mystia Izakaya.exe.34684.dmp"`

### 6.4 根因定案（07-26 全代码审查结论）

启动加载期游戏自动补调 `PostEvaluation(Exbad, null, true, true)`（日志实证）→ 评价流程紧接调
`OnRequestEvaluationDialog` → **NightChatPatch 的 postfix 首次真正执行**（该 postfix 历史上零命中，
「05:53 安全」实际是根本没跑过、安全性从未被验证）→ 在加载中的半成品实例上调
`ResolveDishName()` → `controller.PeekOrders()` → native AV（签名 A），死在 postfix 第一条日志之前。
与「PostEvalHit 打印 (#1) 正常返回后、主界面之前死亡」完全吻合。

次嫌疑（可能性较低，一并修复）：NightDiagPatch 的 FrameWatcher 第 30 帧处理启动时存下的
`_pendingInstance` → `DumpPostEvalContext` → 同一个 PeekOrders。但其「评价者」日志（纯托管应先打出）
未在崩溃日志中出现。

### 6.5 已排除（实证）

| 嫌疑 | 排除依据 |
|---|---|
| TMP_Text.set_text / TMP_Text.SetText patch | 已删，仍崩（但删后栈上 TMP 帧消失，当时确实也是凶手之一） |
| 协程 MoveNext patch ×3（接口方法） | 已删，仍崩 |
| EvalulationBoxUI.SetSkin（struct 参数） | 已删，仍崩 |
| NightSceneDirector×2 / Timeline OnBehaviourEnter×2 / GuestsManager×3 / GenerateXxxConv×3 / GetEvaluationDialog×2 / Evaluate | 已删，仍崩 |
| PostEvaluation postfix 体内重活（PeekOrders/FindObjectsOfType） | 已移出 + 防重入，栈溢出消失但签名 A 崩溃仍在 |
| NightChatPatch postfix 的 `out Transform` 参数 | 已移除，仍崩（11:39 实证） |
| 按钮 UnityEvent / SyncContext | 白天链路的旧案，已修 |

### 6.6 修复（07-26 构建，已部署待实测）

全部走「相位闸门」保守化，不新增任何 patch：

1. `NightChatPatch.IsNightWorkPhase()`（新增 internal static）：只读
   `GameData.RunTime.Common.RunTimeScheduler.CurrentGamePhase` 一个 static 枚举属性，
   仅 `Work`/`BeforeChallengeStart`/`Challenge` 返回 true，任何异常 catch 归为 false。
2. `OnRequestIdleDialog_Postfix` / `OnRequestEvaluationDialog_Postfix`：在 Enabled 与原文判空之后、
   任何其他 IL2CPP 调用之前，`if (!IsNightWorkPhase()) return;`——启动加载期一律不碰 IL2CPP。
3. `NightDiagPatch.FrameWatcher`：启动延迟 600 帧（约 10s，标题到达后才干活）；处理
   `_pendingInstance` 前再过一道 `IsNightWorkPhase()` 闸门，非营业场景只打一条跳过日志，
   PeekOrders 永不在启动期执行。

已知局限：相位闸门假定加载期 `CurrentGamePhase` 非 Work（或读取异常被 catch 归为 false）。
若用户实测仍崩，下一步把两个取数 postfix 缩成纯登记（零 IL2CPP 调用），重活全移帧 watcher。

### 6.7 仍未解之谜（不阻塞启动）

NightChatPatch 取数点（OnRequestIdleDialog/OnRequestEvaluationDialog postfix）**历史零命中**——
真实营业时气泡文本可能根本不走这两个方法。本次修复只保启动；正式实现的取数点要靠
FrameWatcher 的气泡观察数据（`[MystiaAI-DIAG]` 日志）来定。备选思路：夜晚实现全部挂安全锚点
（PostEvaluation 登记 + 帧 watcher 轮询气泡原地改写），一个夜场景方法 patch 都不留。

## 7. 配置与数据文件（游戏侧）

- `BepInEx/config/cc.mystia.ai.cfg`：API key（用户已填 DeepSeek key，注意已暴露过需轮换）、URL、模型（deepseek-v4-flash，deepseek-chat 已被官方淘汰）、超时、MaxLength
- `BepInEx/config/MystiaAI/personas.json`：角色人设，key = 角色 stringId（如 "Rumia"）
- `BepInEx/plugins/MystiaAI.dll`：部署目标

## 8. 已完成功能（用户实测通过，回归时不要破坏）

1. 白天闲聊 AI 替换（三层管线）+ 真实 DeepSeek API
2. Self 段保持原文 + 逐句懒生成 + 上下文连贯（transcript 带玩家输入）
3. 自由输入覆盖层（中文 IME、连续 Self 段归组只弹一次、2 个 AI 建议按钮、Enter/Esc）
4. 真实游戏时间 + 当日报纸流行情报进 prompt
5. 闪退修复（ dispatcher 纯队列化、UnityObjectGuard fake-null 防护）
6. 快进对话时覆盖层随会话结束静默关闭
