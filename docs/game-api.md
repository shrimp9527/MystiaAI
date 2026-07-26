# 《东方夜雀食堂》游戏 API 技术参考（NPC 文本替换向）

> 数据来源：`BepInEx/interop/Assembly-CSharp.dll` 与 `Assembly-CSharp-firstpass.dll`（Il2CppInterop 生成的托管 stub，用 ilspycmd 10.1.1 反编译）。
> stub 方法体均为 icall 转发，本文只采信**签名 / 字段 / 属性**；调用关系由类型引用推断，并以 MetaMystia（github.com/MetaMikuAI/MetaMystia）源码佐证（文中标注「佐证」）。
> 注意 interop 泛型集合多为 `Il2CppSystem.Collections.Generic.*`；`Il2CppStringArray` = `string[]` 的 IL2CPP 包装；`Il2CppSystem.Action<T>` / `Il2CppSystem.Func<T,R>` 是 IL2CPP 版委托，与 managed `System.Action<T>` 不直接兼容，构造时需借助 Il2CppInterop 的委托转换。

---

## 0. 核心数据结构（剧情/对话包）

### `GameData.Profile.DialogPackage`（ScriptableObject）

```csharp
namespace GameData.Profile;
public class DialogPackage : ScriptableObject
{
    public MultiLanguageTextMesh.MultiLanguageAssetReference dialogContext; // 多语言文本资产引用
    public Il2CppReferenceArray<DialogMeta> dialogMeta;                     // 每段对话的元数据
}
```

### `Common.DialogUtility.DialogMeta`（ValueType，[Serializable]）

```csharp
namespace Common.DialogUtility;
public sealed class DialogMeta : Il2CppSystem.ValueType
{
    public int dialogId;                              // 对话段 id —— overrideReplaceTextCallback 字典的 key
    public Il2CppReferenceArray<DialogAction> dialogAction;
    public SpeakerIdentity speakerIdentity;           // 说话人（类型 + id）
    public Position speakerPosition;
    public bool isSpeakInForeground;
    public bool isDark;
    public bool useNameInText;
    public bool useOverrideSprite;
    public AssetReferenceSprite m_OverrideSpriteAsset;
    public bool OverrideSpriteValid { get; }
    public UniTask<IAssetHandle<Sprite>> LoadOverrideSprite();
}
```

### `Common.DialogUtility.SpeakerIdentity`（struct，显式布局）

```csharp
namespace Common.DialogUtility;
[StructLayout(LayoutKind.Explicit)]
public struct SpeakerIdentity
{
    public enum Identity { Self, Special, Normal, Unknown }

    [FieldOffset(0)] public Identity speakerType;
    [FieldOffset(4)] public int speakerId;                 // Special 时 = 稀客数字 id；Normal 时 = 普通角色 id
    [FieldOffset(8)] public int speakerPortrayalVariationId; // 立绘差分 id

    public SpeakerIdentity(Identity speakerType, int speakerId, int speakerPortrayalVariationId);
    public static implicit operator Il2CppSystem.ValueTuple<Identity, int>(SpeakerIdentity speakerIdentity);
}
```

佐证：MetaMystia `ResourceEx/Dialog.cs` 的 `BuildDialogPackage` 完整演示了如何 `ScriptableObject.CreateInstance<DialogPackage>()`、填充 `DialogMeta`/`SpeakerIdentity` 并注册进 `DataBaseDay.allDialogPackages`。

### `GameData.Profile.DialogPackageCollection`

```csharp
namespace GameData.Profile;
public class DialogPackageCollection : ScriptableObject
{
    // 内含 IDPackagePair 列表：dialogId -> DialogPackage，用于按 id 索引对话包
}
```

---

## A. 白天地图 NPC 闲聊

### 数据流

1. 每个地图 NPC 由 `GameData.RunTime.DaySceneUtility.Collection.TrackedNPC` 跟踪，其字段
   `Dictionary<string, Il2CppSystem.Tuple<string, Il2CppStringArray>> dialogPackagePool`
   按 destination 存放该 NPC 可用的闲聊对话包 id 列表。
2. 玩家与 NPC 互动 → 打开 `DayScene.UI.DaySceneChatSelectionPannel` → 选「闲聊」→ 面板调用私有方法
   `DaySceneChatSelectionPannel.Chat(string characterLabel, bool shouldTriggerEvent)`。
3. `Chat` 内部经 `RunTimeDayScene.GetCharacterChatData(characterKey, isPostChat: false)` 取出一个
   `DialogPackage`，再交给 `Common.UI.UniversalGameManager.OpenDialogMenu(...)` 播放。

### 关键签名（`GameData.RunTime.DaySceneUtility.RunTimeDayScene`，全 static）

```csharp
public static void SetNPCDialog(string characterLabel, string destinationLabel, Il2CppStringArray dialogPackageIds);
public static void AddNPCDialog(string characterLabel, string destinationLabel, string dialogPackage);
public static void RemoveNPCDialog(string characterLabel, string destinationLabel, string dialogPackage);
public static bool HasChatData(string characterLabel);
public static DialogPackage GetCharacterChatData(string characterKey, bool isPostChat); // 返回闲聊 DialogPackage
public static TrackedNPC GetTrackedNPC(string key);
public static Dictionary<string, TrackedNPC> GetMapNPCs(string mapLabel);
```

对话包注册表（`GameData.Core.Collections.DaySceneUtility.DataBaseDay`，全 static）：

```csharp
public static Dictionary<string, DialogPackage> allDialogPackages;
public static DialogPackage RefDialogPackage(this string key);   // 按包名取 DialogPackage
public static bool IsDialogPackage(this string key);
```

### 推荐 Harmony 拦截点

- **首选（能拿到角色 ID + 原文，且粒度最细）**：
  `[HarmonyPostfix] RunTimeDayScene.GetCharacterChatData(string characterKey, bool isPostChat, ref DialogPackage __result)`
  —— `characterKey` 即角色 string label（如 `"Wriggle"`），`__result.dialogMeta[i].dialogId` + `dialogContext` 可得原文。
- **文本替换落点**：白天闲聊走的也是 `UniversalGameManager.OpenDialogMenu`，对其做 Prefix，注入
  `overrideReplaceTextCallback`（见 E 节）即可不改资产直接换文本。MetaMystia `Patches/Common/UniversalGameManagerPatch.cs` 正是这么做的（按 `dialogPackage.name` 匹配后注入回调）。
- `SetNPCDialog` 适合做**增/删闲聊池**（如给某 NPC 追加自定义对话包 id），MetaMystia `ResourceEx/SpecialGuest.cs:427` 佐证：`RunTimeDayScene.SetNPCDialog(stringId, "Wriggle", dialogs);`

---

## B. 营业中稀客闲聊（气泡式）

### 数据流

1. 文本池在 `GameData.CoreLanguage.Collections.NightSceneLanguage`（static，语言加载时 `Initialize` 填充）：

```csharp
namespace GameData.CoreLanguage.Collections;
public static class NightSceneLanguage
{
    // key = 稀客数字 id；value = 该稀客的闲聊片段数组（StructPtr<string> 为 TSV 行包装）
    public static Dictionary<int, Il2CppReferenceArray<UnityEngineExtensionStatic.StructPtr<string>>> SpecialConversation { get; }
    public static Dictionary<int, Il2CppReferenceArray<UnityEngineExtensionStatic.StructPtr<string>>> NormalConversation { get; }
    public static Dictionary<int, Il2CppStringArray> SpecialEvaluation { get; }
    public static Dictionary<int, Il2CppStringArray> NormalEvaluation { get; }

    public static void Initialize(
        Dictionary<int, Il2CppStringArray> normalEvaluation,
        Dictionary<int, Il2CppStringArray> specialEvaluation,
        Dictionary<int, Il2CppReferenceArray<UnityEngineExtensionStatic.StructPtr<string>>> normalConversation,
        Dictionary<int, Il2CppReferenceArray<UnityEngineExtensionStatic.StructPtr<string>>> specialConversation);

    public static string GenerateSpecialConv(int id);                              // 拼一条随机闲聊文本
    public static string GenerateSpecialConv(int id, List<string> extraConv);      // 同上，附加额外片段
    public static string GenerateNormalConv(int id);
}
```

2. 每只稀客由 `NightScene.GuestManagementUtility.SpecialGuestsController`（继承 `GuestGroupController`）控制，闲聊文本经以下虚方法产出：

```csharp
namespace NightScene.GuestManagementUtility;
public class GuestGroupController
{
    public virtual string OnRequestIdleDialog(out Transform speaker);       // abstract，气泡闲聊文本
    protected virtual string GetEvaluationDialog(int evaluationIndex, out Transform speaker); // abstract
}

public class SpecialGuestsController : GuestGroupController
{
    public SpecialGuest SpecialGuest { get; }                               // → .Id (int) / .StringId (string)
    public override string OnRequestIdleDialog(out Transform speaker);      // 稀客闲聊入口
    public override string GetEvaluationDialog(int evalulationIndex, out Transform speaker);
}
```

3. `NightScene.GuestManagementUtility.GuestsManager`（MonoSingleton）集中显示：

```csharp
public IEnumerator OnIdleDialog();                                          // 轮询触发闲聊
public void ShowTargetDialog(string message, GuestGroupController guestGroupController,
                             GuestGroupController.EvaluationResult boxType); // 真正出气泡文本
```

另外 `GameData.Core.Collections.NightSceneUtility.SpecialGuest`（继承 `GuestBase`，`GuestBase.id : int`）上有：

```csharp
public string GenerateRandomConvMessage();
public string GenerateRandomConvMessage(IEnumerable<string> extraConv);
public Il2CppStringArray characterKizunaLevel1ChatData; // …Level2~5ChatData，羁绊1~5级的闲聊片段池
```

### 推荐 Harmony 拦截点

- **稀客闲聊首选**：`[HarmonyPostfix] SpecialGuestsController.OnRequestIdleDialog(out Transform speaker, ref string __result)`
  —— `__instance.SpecialGuest.Id` / `.StringId` 拿稀客 ID，`__result` 即原文，可直接改写。
  （普通客人对应 `NormalGuestsController` 的同一虚方法；想统一拦截可 patch 基类虚方法的各 override。）
- **兜底/统一出口**：`[HarmonyPrefix] GuestsManager.ShowTargetDialog(string message, GuestGroupController, EvaluationResult boxType)`
  —— 所有气泡文本（闲聊 + 评价）都经过它，`guestGroupController` 可 `TryCast<SpecialGuestsController>()` 取稀客 ID；但此处分不清是闲聊还是评价时要结合 `boxType`。
- 想**改写文本池本身**：`[HarmonyPostfix] NightSceneLanguage.Initialize(...)` 之后直接改 `SpecialConversation[id]`。
  MetaMystia `Patches/CoreLanguage/NightSceneLanguagePatch.cs` 即在 Initialize postfix 中触发注册，
  `ResourceEx/SpecialGuest.cs:172`：`NightSceneLanguage.SpecialConversation[config.id] = ...`。

---

## C. 上菜后评价语

### 数据流与签名

1. 评价等级枚举（`NightScene.GuestManagementUtility.GuestGroupController` 内嵌）：

```csharp
public enum EvaluationResult { Exbad, Bad, Normal, Good, ExGood, Null }
```

2. 评价文本池：`NightSceneLanguage.SpecialEvaluation : Dictionary<int, Il2CppStringArray>`（key = 稀客 id，
   数组按评价等级索引）。稀客资产侧另有快捷属性：

```csharp
// GameData.Core.Collections.NightSceneUtility.SpecialGuest
public Il2CppStringArray Evaluation { get; }   // 该稀客全部评价语
```

3. 产出与显示链：

```csharp
// GuestGroupController（基类）
public string OnRequestEvaluationDialog(EvaluationResult evaluation, out Transform speaker); // 按等级取评价语
public virtual void PostEvaluation(EvaluationResult evaluationType, Il2CppSystem.Action onFinish,
                                   bool finishedByPartner, bool obtainedExGoodRatingWithModifiers = false);
public event Il2CppSystem.Action<EvaluationResult, GuestGroupController, bool> OnEvalFinishCallback;

// SpecialGuestsController（稀客 override）
public override string GetEvaluationDialog(int evalulationIndex, out Transform speaker);

// GuestsManager
private IEnumerator ShowEvaluationDialog(GuestGroupController toTalk, string message,
                                         GuestGroupController.EvaluationResult result, Transform target);
public void ShowTargetDialog(string message, GuestGroupController, EvaluationResult boxType);
public GuestGroupController.EvaluationResult EvaluationTrans(int evaluation); // 数值评价 → 等级（实例方法，MetaMystia 调用佐证）
```

4. 辅助（`GameData.CoreLanguage.Collections.DataBaseLanguage`）：

```csharp
public static string GetEvalText(this GuestGroupController.EvaluationResult evalLevel); // 等级 → 通用评价短文本

// NightSceneLanguage.GuestEvaluation（ValueType）
public enum EvaluationType { ... }        // exbad/bad/normal/good/exgood/warning/overpay
public string GetEvaluation(EvaluationType type);
```

### 推荐 Harmony 拦截点

- **首选（同时拿到稀客 ID 与评价等级）**：
  `[HarmonyPostfix] GuestGroupController.OnRequestEvaluationDialog(EvaluationResult evaluation, out Transform speaker, ref string __result)`
  —— `evaluation` 是等级；`__instance` 为 `SpecialGuestsController` 时 `.SpecialGuest.Id` 是稀客 ID；`__result` 为原文。
- 亦可 postfix `SpecialGuestsController.GetEvaluationDialog(int evalulationIndex, ...)`（`evalulationIndex` 即等级索引）。
- 改池：`NightSceneLanguage.Initialize` postfix 后写 `SpecialEvaluation[id]`（MetaMystia `ResourceEx/SpecialGuest.cs:156` 佐证）。

---

## D. 白天聊天的选项面板（DaySceneChatSelectionPannel）

`DayScene.UI.DaySceneChatSelectionPannel`（`Common.UI` 面板体系，`AdpUIPanelManager` 管理）。

### 选项如何传入

选项不是静态列表，而是**委托数组**，每个委托产出一个选项的（标题、可用性、点击回调）：

```csharp
// 嵌套委托类型
public sealed class GetSelectionConfigurationCallback : Il2CppSystem.MulticastDelegate
{
    public virtual void Invoke(BaseInteractData baseInteractData,
        out string title, out bool availability, out Il2CppSystem.Action onInteract);
}
public sealed class GetNormalNPCSelectionConfigurationCallback : Il2CppSystem.MulticastDelegate
{
    public virtual void Invoke(NormalNPCInteractData data,
        out string title, out bool availability, out Il2CppSystem.Action onInteract);
}
public sealed class GetSpecialNPCSelectionConfigurationCallback : Il2CppSystem.MulticastDelegate
{
    public virtual void Invoke(SpecialNPCInteractData data,
        out string title, out bool availability, out Il2CppSystem.Action onInteract);
}
```

交互上下文（嵌套类）：

```csharp
public class BaseInteractData
{
    public Il2CppSystem.Func<string, string> getPhraseCallback;   // 语言 key → 选项标题文本
    public Il2CppSystem.Action closeChatSelectionPannelCallback;
    public Il2CppSystem.Action refreshSelectionCallback;
}
public class NormalNPCInteractData : BaseInteractData
{
    public string characterLabel;            // 角色 string label
    public TrackedMerchant merchantData;
}
public class SpecialNPCInteractData : NormalNPCInteractData
{
    public int npcKizunaLevel;               // 当前羁绊等级（决定显示哪些选项：邀请/委托/请购等）
    public StatusTracker statusTracker;
}
```

打开上下文（嵌套类）：`BaseOpenContext`（abstract，`OpenContextType { ... }`）→
`SpecialGuestOpenContext(string characterLabel, bool shouldTriggerEvent, Action<Action> onWillExecuteDayEndEventCallback)` /
`NormalGuestOpenContext` / `GeneralOpenContext`，其构造为：

```csharp
public GeneralOpenContext(Il2CppReferenceArray<GetSelectionConfigurationCallback> selections,
    string endButtonTitleKey, int indexToSelect, EndButtonCallback onEndButtonSubmitCallback);
public sealed class EndButtonCallback : Il2CppSystem.MulticastDelegate { ... }
```

面板本体：

```csharp
public BaseOpenContext OpenContext { get; }
public override void OnPanelOpen();
public void RefreshPannel();

// 选项动作（private，由选项 onInteract 触发）
private void Chat(string characterLabel, bool shouldTriggerEvent);                       // 闲聊 → GetCharacterChatData → OpenDialogMenu
private void Invite(string characterLabel, int currentKizunaLevel, Action<Action> onWillExecuteDayEndEventCallback);
private void RequestIngredient(string characterLabel, int currentKizunaLevel, Action<Action> cb);
private void RequestBeverages(string characterLabel, int currentKizunaLevel, Action<Action> cb);
private void Commision(string characterLabel, string commisionLabel, Action<Action> cb);
public static bool InviteSpecGuest(SpecialGuest specialGuest, int kizunaLevel, out DialogPackage selectedDialogue);
public static void CommitSpecGuest(SpecialGuest specialGuest, string commisionLabel);

// 额外选项注入点（稀客 ExtraDialogData 提供 prepend/append/extraMission 选项）
private IEnumerable<GetSpecialNPCSelectionConfigurationCallback> GetConfigurationSet(
    string currentCharacterLabel,
    Il2CppReferenceArray<GetSpecialNPCSelectionConfigurationCallback> prependSelections,
    Il2CppReferenceArray<GetSpecialNPCSelectionConfigurationCallback> appendSelections,
    Il2CppReferenceArray<GetSpecialNPCSelectionConfigurationCallback> extraMissionSelections,
    bool shouldTriggerEvent, Il2CppSystem.Action<Il2CppSystem.Action> onWillExecuteDayEndEventCallback);
```

### 选中后的流转

选项的 `onInteract` 回调即选中后的动作：「闲聊」→ `Chat(characterLabel, …)` →
`RunTimeDayScene.GetCharacterChatData` → `UniversalGameManager.OpenDialogMenu`（对话播完经
`onFinishCallback` 回到面板/或触发 `onWillExecuteDayEndEventCallback` 消耗行动次数）。
聊天前还有确认环节：`DayScene.UI.UIManager.OpenChatConfirmationModule(...)` 系列：

```csharp
public void OpenChatConfirmationModule(Il2CppSystem.Action<bool> onResult);                          // 是否确认闲聊（扣行动点）
public void OpenChatConfirmationModule(Il2CppSystem.Action<bool> onResult, PanelVisualMode mode);
public UniTask<bool> OpenChatConfirmationModuleAsync();

// 对话结束后的「继续/结束」菜单
public void OpenAfterChatMenu(string normalCharacterLabel, TrackedMerchant trackedMerchant,
    Il2CppReferenceArray<GetNormalNPCSelectionConfigurationCallback> configurationCallback,
    Il2CppSystem.Action onExitCallback, PanelVisualMode m = HideVisual);
public void OpenAfterChatMenu(Il2CppReferenceArray<GetSelectionConfigurationCallback> configurationCallbacks,
    string endButtonTitleKey, GeneralOpenContext.EndButtonCallback endButtonAction,
    Il2CppSystem.Action onExitCallback, int indexToSelct = -1, PanelVisualMode m = HideVisual);
```

### 自定义选项的注入方式

- `GameData.Core.Collections.DaySceneUtility.Collections.SpecialGuestExtraDialogData`（ScriptableObject）
  字段：`prependSelection` / `appendSelections` / `extraMissionSelections`
  （均为 `Il2CppReferenceArray<GetSpecialNPCSelectionConfigurationCallback>`）；
  子类 `SpecialGuestPublicExtraDialogData` 增加 `Il2CppStringArray targetSpecialGuestLabel` 与
  `virtual bool ShouldShowGlobalSelectionForThisCharacter(string characterLabel)`。
  全局池：`DataBaseDay.globalPublicExtraDiaglogSelectionsData : List<SpecialGuestPublicExtraDialogData>`，
  按稀客：`DataBaseDay.DaySceneGetSpecialGuestExtraDialogData(this int specialGuestId)`。
- MetaMystia 佐证（`Patches/DayScene/DaySceneChatSelectionPannel__c__DisplayClass17_0Patch.cs`）：
  postfix 选项配置方法（`SpecialNPCInteractData, out title, ref availability, out onInteract` 形态），
  用 `RunTimeDayScene.HasChatData(stringId)` 与 `DataBaseDay.DaySceneCheckSpecialGuestNotSkipGreeting(npcId)`
  动态放开「闲聊」选项 —— 这就是给选项面板加/改选项的实际手法。

---

## E. overrideReplaceTextCallback 的确切类型

`Common.UI.UniversalGameManager`（全 static）的全部重载：

```csharp
public static void OpenDialogMenu(
    DialogPackage dialogPackage,
    Il2CppSystem.Action onFinishCallback,
    Il2CppSystem.Action<Dictionary<int, string>> overrideReplaceTextCallback = null,
    AdpUIPanelManager.PanelVisualMode previousPanelVisualMode = AdpUIPanelManager.PanelVisualMode.HideVisual);

public static void OpenDialogMenuWithExitCode(
    DialogPackage dialogPackage,
    Il2CppSystem.Action<int> onFinishCallback,
    Il2CppSystem.Action<Dictionary<int, string>> overrideReplaceTextCallback = null,
    AdpUIPanelManager.PanelVisualMode previousPanelVisualMode = HideVisual);

public static UniTask OpenDialogMenuAsync(
    DialogPackage dialogPackage,
    Il2CppSystem.Action<Dictionary<int, string>> overrideReplaceTextCallback = null,
    AdpUIPanelManager.PanelVisualMode previousPanelVisualMode = HideVisual);   // 无 onFinishCallback，以 await 代替
public static IEnumerator OpenDialogMenuCoroutine(DialogPackage, Action<Dictionary<int,string>> ...);
public static IEnumerator OpenDialogMenuWithExitCodeCoroutine(DialogPackage, Action<int>, Action<Dictionary<int,string>> ...);
```

- **委托类型**：`Il2CppSystem.Action<Il2CppSystem.Collections.Generic.Dictionary<int, string>>`。
- **语义**：对话面板在渲染每段文本前把「可替换文本表」交给回调；回调向字典写入
  `dict[dialogMeta[i].dialogId] = "替换后的文本"` 即可按段替换。key 就是 `DialogMeta.dialogId`。
- 佐证：MetaMystia `ResourceEx/Dialog.cs` 的 `BuiltDialogPackage.OverrideReplaceTextCallback`：
  `System.Action<Il2CppSystem.Collections.Generic.Dictionary<int, string>>`，逐条 `replaceDict[kvp.Key] = kvp.Value;`；
  `Patches/Common/UniversalGameManagerPatch.cs` 对 `OpenDialogMenu` 做 Prefix，在 `overrideReplaceTextCallback == null`
  且包名命中时注入该回调 —— **这条路径对白天地图闲聊（A 节）同样适用**，因为闲聊最终也走 `OpenDialogMenu`。

---

## F. 环境信息获取

### 当前语言（`Assembly-CSharp-firstpass.dll`）

```csharp
namespace GameData;
public class MultiLanguageTextMesh : MultiLanguageTextMeshCore
{
    public enum LoadLanguageType { Chinese, English, Japanese, Korean, CNT }
    public static LoadLanguageType CurrentLanguage { get; }   // 只读静态属性
}
```

### 游戏内时间 / 日期 / 阶段

```csharp
// GameData.RunTime.Common.RunTimeDayScene（白天，行动次数制时间）
public static int TOTAL_ACTIVE_HOUR;
public static int RemainActions { get; }                    // 剩余行动次数
public static Il2CppSystem.Action<int> OnTimePass;          // 时间流逝事件
public static Il2CppSystem.Action<int> OnTimeSet;
public static Il2CppSystem.Action onDayOver;
public static int GetTotalActions();
public static void WarpHours(int hours, Il2CppSystem.Action<Il2CppSystem.Action> onCustomEventFinish);

// GameData.RunTime.Common.GameDate（struct）
public enum Season { Spring, Summer, Autumn, Winter }
public int Year { get; }  public int Week { get; }  public int Day { get; }  public int Month { get; }
public Season CurrentSeason { get; } public int CorrectedDay { get; set; }
public string ToDetailedText();  public static string ToDetailedText(int day);

// GameData.RunTime.Common.RunTimeScheduler（日期/阶段调度）
public enum GamePhase { Day, DayTimeEnd, DayToPreperation, Preperation, PreperationToWork,
                        BeforeWorkStart, Work, WorkEnd, WorkToResult, ... }
public static GamePhase CurrentGamePhase { get; }           // 当前处于白天/准备/营业/结算哪个阶段
public static Il2CppSystem.Action OnSchedulerUpdate;

// Common.TimelineExtestion.GameTimeManager（MonoSingleton，营业场景时钟驱动）
public enum TimeMode { Freeze, HalfFreeze, Resume }
public static GameTimeManager Instance { get; }             // MonoSingleton<>
public TimeMode CurrentTimeMode { get; }
public float DefaultTimeScale { get; }
public void SetGameTimeMode(TimeMode mode);                 // MetaMystia 用它冻结/恢复营业时钟
public void AddTicks(int ticks);
```

> 注意：当前绝对日期（第几天）没有简单的 public static getter；`RunTimeScheduler.Initialize(..., int currentDate)`
> 接收存档日期，`TrackedMissionData.today : GameDate` 等内部使用。实际取日期可走 `PlayerSaveFile` 当前存档
> 或监听 `RunTimeScheduler.OnSchedulerUpdate`。营业结束事件：`GuestsManager.OnBussinessTimeEnd`。

### 羁绊（Kizuna）

```csharp
// GameData.RunTime.Common.RunTimeAlbum（全 static）
public static int GetCharacterKizuna(int characterId, out int maxExp, out int level);   // 返回当前 exp
public static int GetCharacterKizuna(string characterLabel, out int maxExp, out int level);
public static int GetLevelUpExpAmount(int currentLevel);
public static bool HasSpecialNPCKizunaExpFull(int characterId);
public static int GetOrGenerateSpecialNPCKizunaLevel(string characterLabel);
public static int GetOrGenerateSpecialNPCKizunaLevel(int characterId);
public static void AlterOrGenerateSpecialNPCKizuna(string characterLabel, int kizunaAmount);
public static void UpgradeOrGenerateSpecialNPCKizuna(string characterLabel);
public static int RefSpecialNPCId(this string characterLabel);                          // label → 数字 id
public static RunTimeAlbum.SpecialGuestRunTimeData RefOrGenerateSpecialRunTimeData(this int guestId);
public static IEnumerable<int> GetLevel5KizunaNPCData();

// 事件
public static Il2CppSystem.Action<Il2CppSystem.ValueTuple<int, int>> OnSpecialGuestKizunaUpgrade;
public static Il2CppSystem.Action<string, bool> OnSpecialGuestKizunaChanged;
public static Il2CppSystem.Action OnSpecialGuestKizunaFull;

// RunTimeAlbum.SpecialGuestRunTimeData（每稀客运行时数据）
public int CurrentBondLevel { get; }   // 当前羁绊等级
```

### 稀客档案枚举

```csharp
// GameData.Profile.SpecialGuestProfile（ScriptableObject，全部稀客资产）
public Il2CppReferenceArray<SpecialGuest> specialGuests;
public Il2CppReferenceArray<MappedSpecialGuest> mappedSpecialGuests;

// GameData.Core.Collections.NightSceneUtility.SpecialGuest : GuestBase（GuestBase.id : int）
public string StringId { get; }
public Il2CppStringArray Evaluation { get; }
public Il2CppStringArray characterKizunaLevel1ChatData;  // …至 Level5
public Il2CppStringArray GetDialogPackagesAtKizunaLevel(int level);
public Il2CppReferenceArray<DialogPackage> GetWelcomeDialogPackagesAtKizunaLevel(int level);
public Il2CppReferenceArray<DialogPackage> GetInviteDialogPackageAtKizunaLevel(int level, bool isSuccess);
public string GenerateRandomConvMessage();
```

---

## 附：工具与产物

- 反编译：`tools/ilspycmd.exe -t <FullTypeName> <interop dll>`（输出走 stdout 重定向；`-o` 会生成目录）。
- 本文涉及类型的完整反编译副本在 `decomp/` 目录（如 `RunTimeDayScene.cs`、`Common_UI_UniversalGameManager.cs`、
  `NightScene_GuestManagementUtility_GuestsManager.cs`、`DayScene_UI_DaySceneChatSelectionPannel.cs` 等）。
- MetaMystia 参考实现（关键文件）：
  - `ResourceEx/Dialog.cs` — 自建 DialogPackage + overrideReplaceTextCallback 注入（E 节范式）；
  - `ResourceEx/SpecialGuest.cs` — 注册 `NightSceneLanguage.SpecialEvaluation/SpecialConversation`、`RunTimeDayScene.SetNPCDialog`；
  - `Patches/Common/UniversalGameManagerPatch.cs` — `OpenDialogMenu` Prefix；
  - `Patches/CoreLanguage/NightSceneLanguagePatch.cs` — Initialize postfix 改语言池；
  - `Patches/DayScene/DaySceneChatSelectionPannel__c__DisplayClass17_0Patch.cs` — 动态放开「闲聊」选项。

---

## G. 夜晚气泡只读方案可行性（2026-07-26 调查定案）

> 背景：夜场景方法 patch 全部下线后游戏稳定。新方案原则——**不 patch 任何夜场景方法**，
> 只用 `EventSystem.Update` postfix 帧轮询 + `FindObjectsOfType<DialogBoxUI>(true)` 两个已证明安全的通道，
> 营业中发现气泡 → 主线程读游戏数据 → AI 生成 → 气泡原地改写 `tmp.text`。
> 以下逐条是 decomp 证据；新增反编译产物：`decomp/NightScene_UI_DialogBoxUI.cs`、`decomp/NightScene_UI_EvalulationBoxUI.cs`、`decomp/Common_WorldSpaceUITracker.cs`。

### G.1 气泡实例能读到什么

`NightScene.UI.GuestManagementUtility.DialogBoxUI : MonoBehaviour`（`decomp/NightScene_UI_DialogBoxUI.cs:19`）：

- `text : TextMeshProUGUI`（:366）——改写落点；
- `m_WorldSpaceUITracker : WorldSpaceUITracker`（:381）→ `m_FollowTarget : Transform`（`decomp/Common_WorldSpaceUITracker.cs:78`）——跟随的客人；
- `m_CancellationTokenSource`（:396）；static `DIALOG_BOX_OFFSET` / `DIALOG_BOX_SHOW_DURATION`。

子类 `EvalulationBoxUI : DialogBoxUI`（`decomp/NightScene_UI_EvalulationBoxUI.cs:15`）：

- 5 套皮肤字段 `exBadSkin/badSkin/normalSkin/goodSkin/exGoodSkin`（:329~:397，各含 box/handle/head 三个 Sprite）；
- 当前应用的 `box/handle/head/heart : Image`（:399~:457）；
- `SetMessage(string, EvaluationResult, Transform)`（:480）。

**实例上不存 evaluationType**（它只是 `<SetMessage>d__9` 协程参数，:146），但**评价等级可反推**：
比较 `box.sprite` 与 5 个 skin 的 `box` Sprite 引用（5 次引用比较），`heart` Image 的显隐大概率是 ExGood 标记（需实测）。
**气泡类型判别**：`box.TryCast<EvalulationBoxUI>() != null` = 评价气泡；否则为普通 DialogBoxUI（闲聊/驱赶等）。

### G.2 followTarget → 稀客身份（关键反查链）

**`GuestGroupController` 不是 MonoBehaviour**（`: Il2CppSystem.Object`，`decomp/NightScene_GuestManagementUtility_GuestGroupController.cs:20`），
GetComponent 链不存在。反查走「控制器枚举 + guestInstances 匹配」：

1. `GuestsManager : MonoSingleton<GuestsManager>`（`decomp/NightScene_GuestManagementUtility_GuestsManager.cs:25`），
   `Instance` 可用。枚举入口（全 public）：
   - `AllPresentedGuestGroupController : HashSet<GuestGroupController>`（:9985）——全部在场控制器；
   - `AllGuestInDeskController : IEnumerable<GuestGroupController>`（:10669，CallerCount(23)，游戏自己大量用）；
   - `GetInDeskGuest(int deskCode) : GuestGroupController`（token :10831）；
   - `currentDisplayedDialogBox : Dictionary<GuestGroupController, Il2CppSystem.Action>`（:9825）——
     **正在显示气泡的控制器 → 关闭回调**，可直接回答「谁现在头上有气泡」。
2. `GuestGroupController.guestInstances : Il2CppReferenceArray<AStarInputGeneratorComponent>`（`..._GuestGroupController.cs:3230`）。
   `AStarInputGeneratorComponent : CharacterControllerInputGeneratorComponent`（`Common.CharacterUtility`，MonoBehaviour 链，有 `.transform`）。
   `ShowTargetDialog` 的闭包 lambda 就收 `AStarInputGeneratorComponent`（`..._GuestsManager.cs:6476`）——气泡 followTarget 由客人可视组件派生。
   **匹配法**：`followTarget == inst.transform` 或 `followTarget.IsChildOf(inst.transform)`，逐控制器逐实例比对。
3. 命中后 `controller.TryCast<SpecialGuestsController>()?.SpecialGuest`（`..._SpecialGuestsController.cs:898` backing field）
   → `.StringId`（personas.json key）；普通客人 TryCast 为 null 即区分。
   羁绊等级：`RunTimeAlbum.GetOrGenerateSpecialNPCKizunaLevel(id)` 或 `RefOrGenerateSpecialRunTimeData(id).CurrentBondLevel`（见 F 节）。

### G.3 评价上下文（等级/菜品/触发时机）

- **「哪组刚评价完」**：`GuestGroupController.HasEvaluated : bool`（`..._GuestGroupController.cs:4945`）。
  帧 watcher 轮询 `AllGuestInDeskController`，检测 false→true 翻转 = 该组刚完成评价（与评价气泡出现同窗口）。
- **评价等级**：气泡侧 skin sprite 比较（G.1）；
- **菜品**：`controller.PeekOrders() : GuestsManager.OrderBase`（:5823，**CallerCount(25)**——游戏自身 25 处调它，
  属常规只读访问器；加载期崩溃是「半成品实例 + patch 上下文」特例，营业相位 + 在册控制器上是游戏正常路径）。
  `OrderBase.ServFood : Sellable`（`..._GuestsManager.cs:467`，`.Text?.Name` 链在 NightChatPatch 已编译验证）、
  `ServBeverage`（:493）、`DeskCode`（:560）、`IsFullfilled`（:575）。
  **残留风险**：评价气泡弹出时订单可能已出栈 → PeekOrders 取不到。备选：提前在 `IsFullfilled`/`ServedFoodInAir`（:638）
  非空时缓存 ServFood，或接受取不到（prompt 退化为无菜名）。
- `PostEvaluation` 流程结束时游戏内部触发 `OnEvalFinishCallback` 事件（:4808）——**不要订阅**
  （managed 委托移交 native 是签名 A 高危模式），轮询 HasEvaluated 已够用。

### G.4 闲聊/评价/bark/点单甄别

- **评价 vs 非评价**：实例类型判别（G.1）即可。
- **稀客闲聊**（普通 DialogBoxUI + followTarget 属 SpecialGuestsController）：文本源自
  `NightSceneLanguage.SpecialConversation[id]`（`decomp/GameData_CoreLanguage_Collections_NightSceneLanguage.cs:444`）
  经 `GenerateSpecialConv(id)`（:542）拼出。**可用「文本命中闲聊池片段」做佐证甄别**（池为主线程可读 static；
  StructPtr<string> 是 TSV 行包装，精确匹配规则需实测一轮确认）。
  非闲聊的普通 DialogBoxUI：驱赶（ShowRepellDialog/ShowSeenRepellDialog）、没钱、ForceDialogDeskCode 强制对话——
  用池成员性 + `CanIdleDialog`（`..._GuestGroupController.cs:4921`）排除。
- **顶仓 bark**：timeline 资产 `NS_MGuest_PlayEvaluationDialog_Special` 带 `label + evaluationResult`
  （`decomp/NightScene_TimelineExtestion_NS_MGuest_PlayEvaluationDialog_Special.cs:38`）——**符卡 bark 也走评价气泡通道，
  实例同样是 EvalulationBoxUI**。甄别：bark 不伴随 `HasEvaluated` 翻转（那是吃饭评价专用状态），
  且出现时游戏在播符卡 timeline。规则：**只改「HasEvaluated 翻转窗口内出现」的评价气泡**（需实测验证 bark 不翻转该标志）。
- **点单**：`ShowOrder` 是 private（token :10885），点单呈现走 `guestIconManager`/HUD 图标，
  不走 DialogBoxUI 文本气泡；普通客人点单对话是否产生 DialogBoxUI 需首轮实测确认（确认后按「非稀客直接跳过」处理）。

### G.5 扫描性能与更省入口

- `FindObjectsOfType(Il2CppType.From(typeof(DialogBoxUI)), true)` 每 30 帧一次：诊断构建已实机跑过，安全（用户实证）。
  实例数 = 活动气泡数，最多 桌位数+排队 级别（个位数到十几个）。
- **更省入口**：`GuestsManager.Instance.uiContainer : Canvas`（:9690）→ `GetComponentsInChildren<DialogBoxUI>(true)`，
  只扫 UI 子树不扫全场景（气泡父级在 UI canvas 下，需首轮实测确认全都在该 canvas 下）。
- 管理器不持有气泡实例列表（`currentDisplayedDialogBox` 的 value 是关闭回调不是 box）；评价气泡有对象池
  （`_pushCallback_5__3 : Action<EvalulationBoxUI>`，:6640）但无公开注册表。结论：先 FindObjectsOfType，
  有性能问题再切 uiContainer 子树扫描。

### G.6 GamePhase 营业相位覆盖

`RunTimeScheduler.GamePhase`（`decomp/GameData_RunTime_Common_RunTimeScheduler.cs:34-53`）：
`Day, DayTimeEnd, DayToPreperation, Preperation, PreperationToWork, BeforeWorkStart, **Work**, WorkEnd,
WorkToResult, Result, ResultToDay, BeforeChallengeStart, Challenge, BeforeChallengeEnd, YuyukoStageChange,
KyoukoTutorial, KyoukoTutorialEnd`。

- 完整营业时段 = `Work`（普通营业）+ `BeforeChallengeStart/Challenge`（Boss 挑战营业）；
  `YuyukoStageChange` 是幽幽子战中途换阶段（营业中），建议一并放行；`BeforeWorkStart` 是开门前准备，客人未入座，放行无害。
- `CurrentGamePhase`（:11759）getter 的 CallerCount(0)——游戏 native 侧直接读 backing 字段，属性本身有效
  （IsNightWorkPhase 已随启动修复部署，相位闸门行为待用户实测顺带验证）。

### G.7 可行性结论

| 链路环节 | 数据支撑 | 结论 |
|---|---|---|
| 发现气泡 | FindObjectsOfType 每 30 帧（诊断构建实证安全）/ uiContainer 子树 | ✅ |
| 甄别评价 vs 闲聊 | 实例类型 TryCast&lt;EvalulationBoxUI&gt; | ✅ |
| 甄别 bark vs 吃饭评价 | HasEvaluated 翻转窗口关联（待实测验证 bark 不翻转） | ⚠️ 有方案待验证 |
| 甄别闲聊 vs 驱赶/强制 | SpecialConversation 池成员性 + CanIdleDialog | ✅（匹配规则待一轮实测） |
| 取稀客身份 | followTarget ↔ guestInstances 匹配 → TryCast → SpecialGuest.StringId；kizuna 走 RunTimeAlbum | ✅ |
| 取评价等级 | skin sprite 引用比较 ×5（heart 显隐佐证 ExGood） | ✅ |
| 取菜品 | PeekOrders（CallerCount 25，营业期正常路径）；订单已出栈时提前缓存兜底 | ✅ 有兜底 |
| 原地改写 | text.text 主线程写（白天链路同款，已稳定） | ✅ |
| 触发时机 | 轮询 HasEvaluated 翻转 + 气泡出现双条件，零事件订阅零 patch | ✅ |

**结论：可以开工。** 每一环都有只读数据支撑，无一环需要 patch 夜场景方法或订阅 IL2CPP 事件。
两个待实测验证点（bark 不翻转 HasEvaluated、闲聊池文本匹配规则）不阻塞架构，
首轮实现时保留 DIAG 日志一轮即可定案；菜品取数有「提前缓存」兜底，取不到时 prompt 降级为无菜名而非失败。
