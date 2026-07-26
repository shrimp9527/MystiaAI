using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common.DialogUtility;
using Il2CppInterop.Runtime;
using MystiaAI.Core;
using MystiaAI.Patches;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace MystiaAI.UI;

/// <summary>
/// 自由输入覆盖层（PoC）：轮到米斯蒂娅（Self）的台词时弹出。
/// 层级策略：不做独立的 ScreenSpaceOverlay（实测被游戏对话框压住），
/// 而是挂到 DialogPannel 所在的根 Canvas 之下，overrideSorting + 全场景最大
/// sortingOrder + 1000，保证渲染在对话框之上且射线不被挡。
/// 纯 GameObject + uGUI/TMP 内置组件拼装，不使用自定义 MonoBehaviour。
/// 创建流程每一步独立 try-catch 并带步骤名日志，任一步失败一眼定位。
/// </summary>
internal sealed class FreeInputOverlay
{
    private static FreeInputOverlay? _current;

    /// <summary>覆盖层是否打开（DialogPannelPatch 用它锁住游戏按键）。</summary>
    public static bool IsOpen => _current != null;

    private GameObject _root = null!;
    private TMP_InputField _input = null!;
    private readonly Action<string?> _onClosed;
    private bool _closed;

    /// <summary>所属对话框面板（会话结束检测用：面板销毁/隐藏、对话包卸载时覆盖层必须随之关闭）。</summary>
    private DialogPannel? _panel;

    /// <summary>兜底：未发现指针 Canvas 且硬件光标被隐藏时，打开期间强制 Cursor.visible（关闭时恢复原值）。</summary>
    private bool _forceCursorVisible;
    private bool _cursorVisibleWas;

    /// <summary>
    /// 确认/跳过按钮引用（点击检测用）。
    /// 崩溃转储证实：两个 dmp 都死在 coreclr.dll+0x1d1fdd（native UnityEvent → managed 委托 thunk），
    /// 且钉住委托后崩溃原样复现——Il2CppInterop 的 UnityEvent 回调通道在本环境不可用。
    /// 因此按钮不再订阅 onClick，改为 Poll 里轮询鼠标位置 + RectTransform 命中检测（纯只读原生调用）。
    /// </summary>
    private Button? _confirmButton;
    private Button? _skipButton;

    /// <summary>Close 已排队到 Update 通道（防 Poll 在 drain 前重复入队）。</summary>
    private bool _closeQueued;

    /// <summary>AI 建议按钮（状态机：生成中… → 可用(显示建议文本) / 建议不可用；点击=直接采用并确认）。</summary>
    private readonly Button[] _suggestionButtons = new Button[2];
    private readonly TextMeshProUGUI[] _suggestionLabels = new TextMeshProUGUI[2];
    private readonly string?[] _suggestions = new string?[2];

    /// <summary>
    /// 打开覆盖层。onClosed 在主线程回调：参数为玩家输入/采用的建议（确认）或 null（跳过）。
    /// panel 用于取字体与定位游戏 UI 根 Canvas。
    /// suggestionProvider：异步取 2 条 AI 建议（null 则建议按钮直接置为不可用）。
    /// </summary>
    public static void Open(DialogPannel panel, Action<string?> onClosed,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? suggestionProvider = null)
    {
        // 防御：已有实例先按「跳过」关掉，避免叠加
        if (_current != null)
            _current.Close(null);
        _current = new FreeInputOverlay(panel, onClosed, suggestionProvider);
        PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 输入覆盖层已打开");
    }

    /// <summary>热键轮询（由 DialogPannel.OnGUI postfix 在主线程调用）：Enter=确认，Esc=跳过。</summary>
    public static void PollHotkeys()
    {
        try
        {
            _current?.Poll();
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay.PollHotkeys 异常: {ex}");
        }
    }

    private FreeInputOverlay(DialogPannel panel, Action<string?> onClosed,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? suggestionProvider)
    {
        _onClosed = onClosed;
        _panel = panel;

        TMP_FontAsset? font = null;
        Step("取字体", () =>
        {
            if (panel != null && panel.context != null)
                font = panel.context.font;
        });

        // ---- 层级诊断：定位对话框 Canvas，枚举全场景 Canvas，尝试定位软件指针 Canvas ----
        Canvas? gameRoot = null;
        var dialogOrder = 0;
        Step("定位游戏Canvas", () =>
        {
            if (panel == null) return;
            var own = panel.GetComponentInParent<Canvas>();
            gameRoot = own != null ? own.rootCanvas : null;
            if (own != null)
            {
                dialogOrder = own.sortingOrder;
                PluginContext.Log.LogInfo(
                    $"[MystiaAI] FreeInputOverlay 诊断: 对话框 Canvas「{own.name}」renderMode={own.renderMode} " +
                    $"sortingOrder={own.sortingOrder} overrideSorting={own.overrideSorting}；根 Canvas「{(gameRoot == null ? "<null>" : gameRoot.name)}」");
            }
        });

        var maxOrder = 0;
        Canvas? cursorCanvas = null;
        Step("枚举全场景Canvas", () =>
        {
            var sb = new StringBuilder();
            var count = 0;
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (canvas == null) continue;
                count++;
                if (canvas.sortingOrder > maxOrder) maxOrder = canvas.sortingOrder;
                var parentName = canvas.transform.parent == null ? "<场景根>" : canvas.transform.parent.name;
                sb.Append($"\n  「{canvas.name}」renderMode={canvas.renderMode} order={canvas.sortingOrder} " +
                          $"override={canvas.overrideSorting} root={(canvas.isRootCanvas ? "是" : "否")} 父={parentName}");
                if (cursorCanvas == null && LooksLikeCursor(canvas.transform))
                    cursorCanvas = canvas;
            }
            PluginContext.Log.LogInfo($"[MystiaAI] FreeInputOverlay 诊断: 全场景 Canvas 共 {count} 个：" + sb);

            // 指针也可能不是 Canvas 渲染：顺带查名字含 cursor/mouse 的 SpriteRenderer
            var foundSpriteCursor = false;
            foreach (var sr in UnityEngine.Object.FindObjectsOfType<SpriteRenderer>())
            {
                if (sr == null || sr.name == null) continue;
                var n = sr.name.ToLowerInvariant();
                if (!n.Contains("cursor") && !n.Contains("mouse")) continue;
                foundSpriteCursor = true;
                PluginContext.Log.LogInfo(
                    $"[MystiaAI] FreeInputOverlay 诊断: 疑似指针 SpriteRenderer「{sr.name}」" +
                    $"sortingLayer={sr.sortingLayerName} sortingOrder={sr.sortingOrder}");
            }
            if (!foundSpriteCursor)
                PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay 诊断: 未发现名字含 cursor/mouse 的 SpriteRenderer");

            PluginContext.Log.LogInfo(
                $"[MystiaAI] FreeInputOverlay 诊断: 指针 Canvas={(cursorCanvas == null ? "未找到" : $"「{cursorCanvas.name}」order={cursorCanvas.sortingOrder}")}；" +
                $"全场景最大 order={maxOrder}；硬件光标 Cursor.visible={Cursor.visible} lockState={Cursor.lockState}");
        });

        Step("检查EventSystem", () =>
        {
            var es = EventSystem.current;
            if (es == null)
            {
                PluginContext.Log.LogWarning("[MystiaAI] FreeInputOverlay 诊断: EventSystem.current 为 null，按钮点击/聚焦不可用");
                return;
            }
            var module = es.GetComponent<BaseInputModule>();
            PluginContext.Log.LogInfo(
                $"[MystiaAI] FreeInputOverlay 诊断: EventSystem 输入模块={(module == null ? "<null>" : module.GetType().FullName)}");
        });

        // ---- 创建画布：挂到游戏 UI 根之下；层级 = 对话框 < 覆盖层 < 指针 ----
        // 根因（实测查明）：软件指针就在 RootCanvas 内部（指针检测匹配到 RootCanvas 自身，
        // order=32600，Cursor.visible=False）。同一 Canvas 内渲染顺序由层级兄弟顺序决定，
        // sortingOrder/overrideSorting 调整无效——之前调 order 一直没用的原因。
        // 修法：挂进 RootCanvas 后找到指针所在的「根层级直接子节点」，
        // 把覆盖层 SetSiblingIndex 到它的位置上（插到指针兄弟节点之前）。
        Step("创建根Canvas", () =>
        {
            _root = new GameObject("MystiaAI_FreeInputOverlay", Il2CppType.Of<RectTransform>());
            var canvas = _root.AddComponent<Canvas>();
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            _root.AddComponent<GraphicRaycaster>();

            if (gameRoot != null)
            {
                // 同 Canvas 内由兄弟顺序决定层级，overrideSorting 保持 false
                canvas.overrideSorting = false;
                _root.transform.SetParent(gameRoot.transform, false);
                InsertBelowCursor(gameRoot);
            }
            else
            {
                // 退化路径：找不到游戏根 Canvas 时只能独立 overlay 根 Canvas，
                // 此时才需要 sortingOrder（取全场景最大值的下一档）
                canvas.overrideSorting = true;
                canvas.sortingOrder = Math.Max(maxOrder - 1, 0);
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                PluginContext.Log.LogWarning(
                    $"[MystiaAI] FreeInputOverlay: 未找到游戏根 Canvas，退化为 ScreenSpaceOverlay order={canvas.sortingOrder}");
            }

            // 兜底：没找到指针 Canvas 且硬件光标被隐藏时，打开期间强制显示硬件光标（Close 时恢复）
            if (cursorCanvas == null && !Cursor.visible)
            {
                _cursorVisibleWas = false;
                _forceCursorVisible = true;
                Cursor.visible = true;
                PluginContext.Log.LogWarning(
                    "[MystiaAI] FreeInputOverlay: 硬件光标处于隐藏状态且未找到软件指针 Canvas，" +
                    "覆盖层打开期间强制 Cursor.visible=true（关闭时恢复）");
            }

            PluginContext.Log.LogInfo(
                $"[MystiaAI] FreeInputOverlay 诊断: 覆盖层 renderMode={canvas.renderMode} overrideSorting={canvas.overrideSorting} " +
                $"siblingIndex={_root.transform.GetSiblingIndex()} 父级={(gameRoot == null ? "<场景根>" : gameRoot.name)}");
        });

        // ---- 背景面板（加高以容纳两个建议按钮）----
        Transform? panelTf = null;
        Step("创建背景面板", () =>
        {
            var panelGo = NewUi("Panel", _root.transform, new Vector2(0f, -300f), new Vector2(1000f, 340f));
            var bg = panelGo.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.85f);
            panelTf = panelGo.transform;
        });
        if (panelTf == null) return; // 背景都失败就不再往下建，避免半残 UI

        // ---- 输入框 ----
        Step("创建输入框", () =>
        {
            var inputGo = NewUi("Input", panelTf, new Vector2(0f, 115f), new Vector2(920f, 60f));
            var inputImg = inputGo.AddComponent<Image>();
            inputImg.color = new Color(1f, 1f, 1f, 0.95f);

            var textGo = NewUi("Text", inputGo.transform, Vector2.zero, new Vector2(890f, 50f));
            var textComp = textGo.AddComponent<TextMeshProUGUI>();
            if (font != null) textComp.font = font;
            textComp.fontSize = 30f;
            textComp.color = Color.black;

            _input = inputGo.AddComponent<TMP_InputField>();
            _input.textComponent = textComp;
            _input.targetGraphic = inputImg;
            _input.lineType = TMP_InputField.LineType.SingleLine;
            _input.text = string.Empty;
        });

        // ---- AI 建议按钮（初始「生成中…」不可点）----
        Step("创建建议按钮1", () => CreateSuggestionButton(panelTf, 0, new Vector2(0f, 45f), font));
        Step("创建建议按钮2", () => CreateSuggestionButton(panelTf, 1, new Vector2(0f, -11f), font));

        // ---- 确认/跳过按钮 ----
        Step("创建确认按钮", () =>
            _confirmButton = CreateButton(panelTf, "确认", new Vector2(-260f, -100f), font, () => Close(_input.text)));
        Step("创建跳过按钮", () =>
            _skipButton = CreateButton(panelTf, "跳过", new Vector2(260f, -100f), font, () => Close(null)));

        // ---- 聚焦（需要 EventSystem，没有就跳过聚焦只记日志）----
        Step("聚焦输入框", () =>
        {
            if (_input == null) return;
            if (EventSystem.current == null)
            {
                PluginContext.Log.LogWarning("[MystiaAI] FreeInputOverlay: 无 EventSystem，跳过 ActivateInputField");
                return;
            }
            _input.ActivateInputField();
        });

        // ---- 异步获取 AI 建议 ----
        Step("获取AI建议", () => StartSuggestions(suggestionProvider));
    }

    // ---- AI 建议：状态机（生成中… → 可用 / 建议不可用；点击=采用并确认）----

    private void CreateSuggestionButton(Transform parent, int index, Vector2 pos, TMP_FontAsset? font)
    {
        var label = "生成中…";
        var btn = CreateWideButton(parent, $"建议{index + 1}", pos, font, label, out var labelComp);
        btn.interactable = false;
        // 不订阅 onClick（UnityEvent → managed thunk 会崩，见 _confirmButton 注释）；
        // 点击由 Poll 轮询鼠标位置 + 命中检测
        _suggestionButtons[index] = btn;
        _suggestionLabels[index] = labelComp;
    }

    private void StartSuggestions(Func<CancellationToken, Task<IReadOnlyList<string>>>? provider)
    {
        if (provider == null)
        {
            SetSuggestionsUnavailable();
            return;
        }

        PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 建议任务启动");
        _ = Task.Run(async () =>
        {
            IReadOnlyList<string>? result = null;
            try
            {
                var timeout = Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1f, PluginContext.Settings.TimeoutSeconds.Value)));
                var task = provider(CancellationToken.None);
                var finished = await Task.WhenAny(task, timeout).ConfigureAwait(false);
                if (finished == task && task.IsCompletedSuccessfully)
                    result = task.Result;
                PluginContext.Log.LogInfo(
                    $"[MystiaAI] FreeInputOverlay: 建议 provider 完成（线程 {Environment.CurrentManagedThreadId}，" +
                    $"结果 {(result == null ? "<null>" : result.Count + " 条")}）");
            }
            catch (Exception ex)
            {
                PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay: 获取 AI 建议异常: {ex}");
            }

            var captured = result;
            MainThreadDispatcher.Post(() => ApplySuggestions(captured));
            PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 建议回调已入队");
        });
    }

    /// <summary>建议结果落回主线程：填充按钮文本并放开点击；失败/超时置灰。</summary>
    private void ApplySuggestions(IReadOnlyList<string>? result)
    {
        PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: ApplySuggestions 进入");
        if (_closed) return;
        try
        {
            if (result == null || result.Count == 0)
            {
                SetSuggestionsUnavailable();
                PluginContext.Log.LogWarning("[MystiaAI] FreeInputOverlay: AI 建议超时/失败，按钮置灰");
                return;
            }

            for (var i = 0; i < _suggestionButtons.Length; i++)
            {
                if (UnityObjectGuard.IsDead(_suggestionButtons[i]) || UnityObjectGuard.IsDead(_suggestionLabels[i])) continue;
                if (i < result.Count && !string.IsNullOrWhiteSpace(result[i]))
                {
                    _suggestions[i] = result[i];
                    _suggestionLabels[i].text = TruncateForButton(result[i]); // 显示截断，提交用完整文本
                    _suggestionButtons[i].interactable = true;
                }
                else
                {
                    _suggestionLabels[i].text = "建议不可用";
                    _suggestionButtons[i].interactable = false;
                }
            }
            PluginContext.Log.LogInfo($"[MystiaAI] FreeInputOverlay: AI 建议已就绪（{result.Count} 条）");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay.ApplySuggestions 异常: {ex}");
        }
    }

    private void SetSuggestionsUnavailable()
    {
        try
        {
            for (var i = 0; i < _suggestionButtons.Length; i++)
            {
                if (UnityObjectGuard.IsDead(_suggestionButtons[i]) || UnityObjectGuard.IsDead(_suggestionLabels[i])) continue;
                _suggestionLabels[i].text = "建议不可用";
                _suggestionButtons[i].interactable = false;
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay.SetSuggestionsUnavailable 异常: {ex}");
        }
    }

    /// <summary>按钮显示用截断（完整文本照常提交）。</summary>
    private static string TruncateForButton(string s)
        => s.Length <= 24 ? s : s.Substring(0, 24) + "…";

    private static int _pollTicks;

    private void Poll()
    {
        if (_closed || _closeQueued) return;

        // 心跳：覆盖层存活期间 Poll 一直在跑——崩溃后若心跳还在增长的时间点附近停住，
        // 可界定崩溃时刻与 Poll 无关；若心跳中断即崩，凶手在 Poll 内
        if (++_pollTicks % 600 == 0)
            PluginContext.Log.LogInfo($"[MystiaAI] FreeInputOverlay: Poll 心跳 {_pollTicks}");

        // 保险：场景切换等导致覆盖层对象被销毁（fake-null）或 Il2Cpp 包装被 GC 回收
        // 但仍挂着引用时自动关闭，否则 IsOpen 永久为 true 会把 GuardWhileOverlayOpen 卡死在拦截态
        if (UnityObjectGuard.IsDead(_root))
        {
            PluginContext.Log.LogWarning("[MystiaAI] FreeInputOverlay: 覆盖层对象已被销毁，自动按跳过关闭");
            QueueClose(null);
            return;
        }

        // 对话会话结束/中断检测（覆盖层自身 GameObject 不会在对话结束时被销毁，必须看面板状态）：
        // 正常播完/快进跳过/Esc 中断/面板关闭隐藏——任一命中即静默关闭，绝不残留卡屏
        if (UnityObjectGuard.IsDead(_panel))
        {
            PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 对话框面板已销毁，静默关闭");
            QueueClose(null);
            return;
        }
        try
        {
            if (_panel != null && !_panel.gameObject.activeInHierarchy)
            {
                PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 对话框面板已隐藏（对话结束/快进跳过），静默关闭");
                QueueClose(null);
                return;
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay 面板活性检测异常（按会话结束处理）: {ex}");
            QueueClose(null);
            return;
        }
        try
        {
            if (_panel != null && _panel.OpenContext?.DialogPackageToPlay == null)
            {
                PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 对话包已卸载，静默关闭");
                QueueClose(null);
                return;
            }
        }
        catch (Exception ex)
        {
            // OpenContext 读取失败可能是瞬态（面板复用切换中），只记日志不关闭
            PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay 对话包检测异常（忽略）: {ex}");
        }

        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
            {
                QueueClose(_input.text);
                return;
            }
            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                QueueClose(null);
                return;
            }
        }

        // 鼠标点击：不订阅 UnityEvent（native → managed thunk 会崩，见 _confirmButton 注释），
        // 改为轮询左键释放 + RectTransform 命中检测（只读原生调用）
        var mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasReleasedThisFrame) return;
        var pos = mouse.position.ReadValue();

        for (var i = 0; i < _suggestionButtons.Length; i++)
        {
            var btn = _suggestionButtons[i];
            if (UnityObjectGuard.IsDead(btn) || !btn.interactable) continue;
            if (!Hit(btn, pos)) continue;
            var text = _suggestions[i];
            PluginContext.Log.LogInfo(
                $"[MystiaAI] FreeInputOverlay: 建议{i + 1} 点击命中（轮询检测），" +
                (text == null ? "无建议文本，忽略" : $"采用「{TruncateForButton(text)}」"));
            if (text != null)
                QueueClose(text); // 等价于输入该文本后点确认，走同一 Close 路径
            return;
        }
        if (Hit(_confirmButton, pos))
        {
            PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 确认按钮点击命中（轮询检测）");
            QueueClose(_input.text);
            return;
        }
        if (Hit(_skipButton, pos))
        {
            PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 跳过按钮点击命中（轮询检测）");
            QueueClose(null);
        }
    }

    /// <summary>屏幕坐标是否落在按钮矩形内（ScreenSpaceOverlay 体系，相机传 null）。失败一律按未命中。</summary>
    private static bool Hit(Button? btn, Vector2 screenPos)
    {
        try
        {
            if (UnityObjectGuard.IsDead(btn)) return false;
            var rt = btn!.GetComponent<RectTransform>();
            return RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos, null);
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay.Hit 异常: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Poll 运行在 DialogPannel.OnGUI postfix（IMGUI 事件处理内部）——
    /// 在这里直接 Close 会把 Destroy/SetContent 等原生 UI 调用做进 OnGUI 事件里（native 崩溃风险），
    /// 因此一律经 Update 通道延迟执行。
    /// </summary>
    private void QueueClose(string? result)
    {
        if (_closed || _closeQueued) return;
        _closeQueued = true;
        MainThreadDispatcher.Post(() => Close(result));
    }

    private void Close(string? result)
    {
        PluginContext.Log.LogInfo(
            $"[MystiaAI] FreeInputOverlay: Close 进入（{(result == null ? "跳过" : "确认")}）");
        if (_closed) return;
        _closed = true;
        try
        {
            if (!UnityObjectGuard.IsDead(_root))
                UnityEngine.Object.Destroy(_root);
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay 销毁异常: {ex}");
        }
        try
        {
            if (_forceCursorVisible)
            {
                _forceCursorVisible = false;
                Cursor.visible = _cursorVisibleWas; // 恢复打开前的硬件光标状态
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay 恢复光标异常: {ex}");
        }
        if (ReferenceEquals(_current, this))
            _current = null;

        try
        {
            _onClosed(result);
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay 关闭回调异常: {ex}");
        }
        PluginContext.Log.LogInfo(
            $"[MystiaAI] FreeInputOverlay: 覆盖层已关闭（{(result == null ? "跳过" : "确认")}）");
    }

    /// <summary>宽条建议按钮（920x46）：返回 Button 并 out 出文本组件，供状态机改文案/置灰。</summary>
    private Button CreateWideButton(Transform parent, string name, Vector2 pos, TMP_FontAsset? font,
        string label, out TextMeshProUGUI labelComp)
    {
        var go = NewUi(name + "Button", parent, pos, new Vector2(920f, 46f));
        var img = go.AddComponent<Image>();
        img.color = new Color(1f, 1f, 1f, 0.9f);
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;

        var textGo = NewUi("Label", go.transform, Vector2.zero, new Vector2(900f, 46f));
        var text = textGo.AddComponent<TextMeshProUGUI>();
        if (font != null) text.font = font;
        text.text = label;
        text.fontSize = 24f;
        text.color = Color.black;
        text.alignment = TextAlignmentOptions.Center;
        labelComp = text;
        return btn;
    }

    private Button CreateButton(Transform parent, string label, Vector2 pos, TMP_FontAsset? font, System.Action action)
    {
        var go = NewUi(label + "Button", parent, pos, new Vector2(200f, 56f));
        var img = go.AddComponent<Image>();
        img.color = new Color(1f, 1f, 1f, 0.9f);
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        // 不订阅 onClick（UnityEvent → managed thunk 会崩，见 _confirmButton 注释）；
        // action 语义由 Poll 的命中检测执行

        var textGo = NewUi("Label", go.transform, Vector2.zero, new Vector2(200f, 56f));
        var text = textGo.AddComponent<TextMeshProUGUI>();
        if (font != null) text.font = font;
        text.text = label;
        text.fontSize = 26f;
        text.color = Color.black;
        text.alignment = TextAlignmentOptions.Center;
        return btn;
    }

    /// <summary>
    /// 把覆盖层插到指针所在的根层级子节点之前：在 RootCanvas 直接子节点中找出
    /// 子树含 cursor/mouse 元素的那个，SetSiblingIndex 到它的位置（它随后顺移 +1）。
    /// 找不到时退化为 SetAsFirstSibling 并打 Warning（绝不能 SetAsLastSibling，那会更压指针）。
    /// </summary>
    private void InsertBelowCursor(Canvas gameRoot)
    {
        try
        {
            var rootTf = gameRoot.transform;
            Transform? cursorRoot = null;
            var cursorIndex = -1;
            for (var i = 0; i < rootTf.childCount; i++)
            {
                var child = rootTf.GetChild(i);
                if (_root != null && ReferenceEquals(child, _root.transform)) continue;
                if (LooksLikeCursor(child))
                {
                    cursorRoot = child;
                    cursorIndex = child.GetSiblingIndex();
                    break;
                }
            }

            if (cursorRoot != null)
            {
                _root.transform.SetSiblingIndex(cursorIndex);
                PluginContext.Log.LogInfo(
                    $"[MystiaAI] FreeInputOverlay: 指针根层级子节点「{cursorRoot.name}」原 siblingIndex={cursorIndex}" +
                    $"（插入后顺移为 {cursorRoot.GetSiblingIndex()}），覆盖层 siblingIndex={_root.transform.GetSiblingIndex()}");
            }
            else
            {
                _root.transform.SetAsFirstSibling();
                PluginContext.Log.LogWarning(
                    "[MystiaAI] FreeInputOverlay: 未在 RootCanvas 直接子节点中定位到指针元素，" +
                    "退化为 SetAsFirstSibling（覆盖层可能压在对话框之下，请反馈 Canvas 枚举日志）");
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay.InsertBelowCursor 异常: {ex}");
        }
    }

    /// <summary>判断某 Canvas 子树是否疑似软件鼠标指针（自身或任一子物体名字含 cursor/mouse，不区分大小写）。</summary>
    private static bool LooksLikeCursor(Transform root)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            var n = t.name;
            if (n != null && (n.ToLowerInvariant().Contains("cursor") || n.ToLowerInvariant().Contains("mouse")))
                return true;
        }
        return false;
    }

    private static GameObject NewUi(string name, Transform parent, Vector2 anchoredPos, Vector2 size)
    {
        // GameObject 的 params Type[] 构造在 interop 下要求 Il2CppSystem.Type，用 Il2CppType.Of<T>()
        var go = new GameObject(name, Il2CppType.Of<RectTransform>());
        go.transform.SetParent(parent, false);
        // interop 下 go.transform 的托管包装不能强转 RectTransform，必须走 GetComponent
        var rt = go.GetComponent<RectTransform>();
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
        return go;
    }

    private static void Step(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay 创建步骤「{name}」失败: {ex}");
        }
    }
}
