using System;
using System.Collections.Generic;
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
/// 自由输入覆盖层：轮到米斯蒂娅（Self）的台词时弹出。
/// 视觉原则：不使用自定义大框——覆盖层除输入框/按钮外全透明，游戏原版对话框完整透出。
/// 布局：对话正文区域左右分栏——左 4/5 区内部上下平分（上半=输入框，下半=两个 AI 建议按钮并排）；
/// 右 1/5 区竖排「完成 / 重新生成 / 结束对话」三个功能按钮（自上而下，互不重叠）。
/// 右上角并排的「结束对话」「重新生成」与「完成」小按钮，底部两个并排的 AI 建议按钮（深色底米白字）；
/// 提交只走鼠标点击（完成/建议按钮）——键盘 Enter/Esc 检测在本环境多条通道均不可靠，已移除。
/// 层级策略：不做独立的 ScreenSpaceOverlay（实测被游戏对话框压住），
/// 而是挂到 DialogPannel 所在的根 Canvas 之下，siblingIndex 插在游戏指针节点前，保证渲染在对话框之上且射线不被挡。
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
    /// 「重新生成」按钮引用（点击检测用）。
    /// 崩溃转储证实：两个 dmp 都死在 coreclr.dll+0x1d1fdd（native UnityEvent → managed 委托 thunk），
    /// 且钉住委托后崩溃原样复现——Il2CppInterop 的 UnityEvent 回调通道在本环境不可用。
    /// 因此按钮不再订阅 onClick，改为 Poll 里轮询鼠标位置 + RectTransform 命中检测（纯只读原生调用）。
    /// </summary>
    private Button? _regenButton;

    /// <summary>「完成」按钮引用（点击检测用，命中后提交输入框文本，等价于回车发送）。</summary>
    private Button? _doneButton;

    /// <summary>「结束对话」按钮引用（点击检测用，命中后关闭覆盖层并干净终止整段对话）。</summary>
    private Button? _exitButton;

    /// <summary>打开时传入的建议 provider（「重新生成」复用再跑一遍）。</summary>
    private Func<CancellationToken, Task<IReadOnlyList<string>>>? _suggestionProvider;

    /// <summary>建议生成进行中（重新生成按钮防重复点击）。</summary>
    private bool _suggestionsLoading;

    /// <summary>对话正文同款米白色（输入文字/按钮文字/占位提示的基色）。</summary>
    private static readonly Color OffWhite = new(0.96f, 0.94f, 0.88f);

    /// <summary>建议/重新生成按钮底色：深棕，比面板底色略深。</summary>
    private static readonly Color ButtonBg = new(0.24f, 0.14f, 0.08f, 0.95f);

    /// <summary>主面板底色：深棕半透明——面板有明确底板（不再全透明），同时仍透出游戏对话框底图轮廓。</summary>
    private static readonly Color PanelBg = new(0.28f, 0.17f, 0.09f, 0.86f);

    /// <summary>面板/输入框/按钮描边色：浅棕，与深棕面板形成外浅内深的层次。</summary>
    private static readonly Color PanelBorder = new(0.78f, 0.62f, 0.42f, 0.85f);

    /// <summary>输入框底色：比面板亮一档的深棕，标识可输入区域。</summary>
    private static readonly Color InputBg = new(0.36f, 0.23f, 0.13f, 0.90f);

    /// <summary>整个覆盖层（面板+输入框+全部按钮）相对正文矩形中心的垂直下移量（UI y 向上为正，正值=下移）。</summary>
    private const float VerticalOffset = 60f;

    /// <summary>右侧三个功能按钮（完成/重新生成/结束对话）额外的垂直下移量（仅右区，左区不动）。</summary>
    private const float RightButtonsVerticalOffset = 15f;

    /// <summary>Close 已排队到 Update 通道（防 Poll 在 drain 前重复入队）。</summary>
    private bool _closeQueued;

    /// <summary>AI 建议按钮（状态机：生成中… → 可用(显示建议文本) / 建议不可用；点击=直接采用并确认）。</summary>
    private readonly Button[] _suggestionButtons = new Button[2];
    private readonly TextMeshProUGUI[] _suggestionLabels = new TextMeshProUGUI[2];
    private readonly string?[] _suggestions = new string?[2];

    /// <summary>建议整体不可用时的按钮文案（默认「建议不可用」；NPC 前句 AI 未生成时传「未进行对话」）。</summary>
    private readonly string _unavailableText;

    /// <summary>
    /// 打开覆盖层。onClosed 在主线程回调：参数为玩家输入/采用的建议（确认）或 null（跳过）。
    /// panel 用于取字体与定位游戏 UI 根 Canvas。
    /// suggestionProvider：异步取 2 条 AI 建议（null 则不发起生成，按钮直接置为不可用，文案用 unavailableText）。
    /// </summary>
    public static void Open(DialogPannel panel, Action<string?> onClosed,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? suggestionProvider = null,
        string unavailableText = "建议不可用")
    {
        // 防御：已有实例先按「跳过」关掉，避免叠加
        if (_current != null)
            _current.Close(null);
        _current = new FreeInputOverlay(panel, onClosed, suggestionProvider, unavailableText);
        PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 输入覆盖层已打开");
    }

    /// <summary>轮询（由 DialogPannel.OnGUI postfix 在主线程调用）：鼠标命中检测 + 聚焦/键盘屏蔽联动 + 会话存活检测。</summary>
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
        Func<CancellationToken, Task<IReadOnlyList<string>>>? suggestionProvider, string unavailableText)
    {
        _onClosed = onClosed;
        _panel = panel;
        _unavailableText = unavailableText;

        TMP_FontAsset? font = null;
        Step("取字体", () =>
        {
            if (panel != null && panel.context != null)
                font = panel.context.font;
        });

        // ---- 定位对话框根 Canvas、全场景最大 order、软件指针所在 Canvas（后续挂载要用）----
        Canvas? gameRoot = null;
        var dialogOrder = 0;
        Step("定位游戏Canvas", () =>
        {
            if (panel == null) return;
            var own = panel.GetComponentInParent<Canvas>();
            gameRoot = own != null ? own.rootCanvas : null;
            if (own != null) dialogOrder = own.sortingOrder;
        });

        var maxOrder = 0;
        Canvas? cursorCanvas = null;
        Step("枚举全场景Canvas", () =>
        {
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (canvas == null) continue;
                if (canvas.sortingOrder > maxOrder) maxOrder = canvas.sortingOrder;
                if (cursorCanvas == null && LooksLikeCursor(canvas.transform))
                    cursorCanvas = canvas;
            }
        });

        Step("检查EventSystem", () =>
        {
            if (EventSystem.current == null)
                PluginContext.Log.LogWarning("[MystiaAI] FreeInputOverlay: EventSystem.current 为 null，按钮点击/聚焦不可用");
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
        });

        // ---- 主面板：对齐「对话正文文本」（panel.context）的矩形，背景全透明——
        // 游戏自己的对话框底图/边框保持可见，我们的内容直接画在对话框内部。
        // （此前按「层级内最大 Image」对齐踩过的坑：对话框底图是 1280x720 全屏纹理，
        //   底框艺术烘在 FGImage 全屏纹理里，按 Image 对齐必然错位。）
        Transform? panelTf = null;
        var panelPos = new Vector2(0f, -300f);
        var panelSize = new Vector2(1000f, 340f);
        var textPos = Vector2.zero;  // 正文矩形中心（覆盖层根 local 单位）
        var textSize = Vector2.zero; // 正文矩形尺寸（覆盖层根 local 单位）
        var matched = false;
        Step("创建主面板", () =>
        {
            if (!UnityObjectGuard.IsDead(panel) && gameRoot != null)
            {
                try
                {
                    var textArea = FindDialogTextArea(panel);
                    if (textArea != null &&
                        TryMatchRect(textArea, _root.GetComponent<RectTransform>(), out var p, out var s))
                    {
                        textPos = p;
                        textSize = s;
                        matched = true;
                        // 面板 = 正文矩形外扩 10 单位的包络（点击拦截层，同时形成明显的面板边框）
                        var top = textPos.y + textSize.y / 2f + 10f;
                        var bottom = textPos.y - textSize.y / 2f - 10f;
                        panelPos = new Vector2(textPos.x, (top + bottom) / 2f);
                        panelSize = new Vector2(textSize.x + 20f, top - bottom);
                    }
                    else
                    {
                        PluginContext.Log.LogWarning(
                            $"[MystiaAI] FreeInputOverlay: 正文矩形换算失败（textArea={(textArea == null ? "<无>" : textArea.name)}）");
                    }
                }
                catch (Exception ex)
                {
                    PluginContext.Log.LogWarning($"[MystiaAI] FreeInputOverlay: 对话正文矩形获取失败: {ex.Message}");
                }
            }
            if (!matched)
                PluginContext.Log.LogWarning("[MystiaAI] FreeInputOverlay: 未定位到对话正文矩形，回退固定布局");

            // 整体下移：面板与全部子元素（输入框/建议/功能按钮）一起向下平移
            panelPos.y -= VerticalOffset;

            var panelGo = NewUi("Panel", _root.transform, panelPos, panelSize);
            var bg = panelGo.AddComponent<Image>();
            // 不要自定义大框：纯透明点击拦截层，游戏原版对话框完全透出；
            // 保留 raycastTarget：点击仍被我们拦截不穿透
            bg.color = new Color(0f, 0f, 0f, 0f);
            panelTf = panelGo.transform;
        });
        if (panelTf == null) return; // 背景都失败就不再往下建，避免半残 UI

        // ---- 布局度量（相对面板：对话正文矩形左右分栏——左 4/5 区内部上下平分
        //      （上半=输入框，下半=两个 AI 建议按钮）；右 1/5 区竖排功能按钮
        //      （完成 / 重新生成 / 结束对话，自上而下，互不重叠））----
        const float gap = 14f;
        var regenSize = new Vector2(160f, 34f);
        var doneSize = new Vector2(72f, 34f);
        var exitSize = new Vector2(104f, 34f);
        Vector2 inputPos, inputSize, regenPos, donePos, exitPos;
        float suggY, suggW, suggH, sugg1X, sugg2X;
        if (matched)
        {
            // 左右分栏：左 4/5 = 输入框+建议按钮区，右 1/5 = 功能按钮区
            var textTop = textPos.y + textSize.y / 2f;
            var textBottom = textPos.y - textSize.y / 2f;
            var textLeft = textPos.x - textSize.x / 2f;
            var textRight = textPos.x + textSize.x / 2f;
            var leftW = textSize.x * 4f / 5f;
            var rightW = textSize.x / 5f;
            var leftCenterX = textLeft + leftW / 2f;
            var rightCenterX = textRight - rightW / 2f;

            // 左 4/5 区内上下平分（中间留 10f）：上半=输入框，下半=两个建议按钮
            const float midGap = 10f;
            var halfH = (textSize.y - midGap) / 2f;
            inputSize = new Vector2(leftW - 2f * 20f, Mathf.Max(36f, halfH - 10f));
            inputPos = new Vector2(leftCenterX, textTop - halfH / 2f);
            suggH = 40f;
            suggW = (leftW - 2f * 16f - gap) / 2f;
            sugg1X = leftCenterX - suggW / 2f - gap / 2f;
            sugg2X = leftCenterX + suggW / 2f + gap / 2f;
            suggY = textBottom + halfH / 2f;

            // 右 1/5 区：三个功能按钮竖排（完成 / 重新生成 / 结束对话），整组垂直居中
            var btnW = rightW - 2f * 12f;
            var btnH = 34f;
            var btnGap = 10f;
            doneSize = new Vector2(btnW, btnH);
            regenSize = new Vector2(btnW, btnH);
            exitSize = new Vector2(btnW, btnH);
            var btnTotalH = 3f * btnH + 2f * btnGap;
            var topBtnY = textPos.y + btnTotalH / 2f - btnH / 2f - RightButtonsVerticalOffset;
            donePos = new Vector2(rightCenterX, topBtnY);
            regenPos = new Vector2(rightCenterX, topBtnY - btnH - btnGap);
            exitPos = new Vector2(rightCenterX, topBtnY - 2f * (btnH + btnGap));

            // 换算为相对面板中心
            inputPos -= panelPos;
            sugg1X -= panelPos.x;
            sugg2X -= panelPos.x;
            suggY -= panelPos.y;
            regenPos -= panelPos;
            donePos -= panelPos;
            exitPos -= panelPos;
        }
        else
        {
            // 回退：面板区域同样左右分栏（左 4/5 内部上下平分，右 1/5 竖排功能按钮）
            var leftW = panelSize.x * 4f / 5f;
            var rightW = panelSize.x / 5f;
            var leftCenterX = -panelSize.x / 2f + leftW / 2f;
            var rightCenterX = panelSize.x / 2f - rightW / 2f;
            const float midGap = 10f;
            var halfH = (panelSize.y - midGap) / 2f;
            suggH = 44f;
            inputSize = new Vector2(leftW - 2f * 20f, Mathf.Max(40f, halfH - 10f));
            inputPos = new Vector2(leftCenterX, panelSize.y / 2f - halfH / 2f);
            suggW = (leftW - 2f * 18f - gap) / 2f;
            sugg1X = leftCenterX - suggW / 2f - gap / 2f;
            sugg2X = leftCenterX + suggW / 2f + gap / 2f;
            suggY = -panelSize.y / 2f + halfH / 2f;
            var btnW = rightW - 2f * 12f;
            var btnH = 34f;
            var btnGap = 10f;
            doneSize = new Vector2(btnW, btnH);
            regenSize = new Vector2(btnW, btnH);
            exitSize = new Vector2(btnW, btnH);
            var btnTotalH = 3f * btnH + 2f * btnGap;
            var topBtnY = btnTotalH / 2f - btnH / 2f - RightButtonsVerticalOffset;
            donePos = new Vector2(rightCenterX, topBtnY);
            regenPos = new Vector2(rightCenterX, topBtnY - btnH - btnGap);
            exitPos = new Vector2(rightCenterX, topBtnY - 2f * (btnH + btnGap));
        }

        // ---- 输入框（背景近似透明融入面板，米白大字居中，灰色占位提示）----
        Step("创建输入框", () =>
        {
            var inputGo = NewUi("Input", panelTf, inputPos, inputSize);
            var inputImg = inputGo.AddComponent<Image>();
            inputImg.sprite = CreateRoundedRectSprite(inputSize.x, inputSize.y, InputBg, PanelBorder, 3f, 14f);
            inputImg.color = Color.white; // sprite 已含颜色

            var textGo = NewUi("Text", inputGo.transform, Vector2.zero, inputSize - new Vector2(20f, 8f));
            var textComp = textGo.AddComponent<TextMeshProUGUI>();
            if (font != null) textComp.font = font;
            textComp.fontSize = 32f;
            textComp.color = OffWhite;
            textComp.alignment = TextAlignmentOptions.Center;
            textComp.raycastTarget = false; // 点击穿透给输入框本体

            var phGo = NewUi("Placeholder", inputGo.transform, Vector2.zero, inputSize - new Vector2(20f, 8f));
            var phComp = phGo.AddComponent<TextMeshProUGUI>();
            if (font != null) phComp.font = font;
            phComp.fontSize = 32f;
            phComp.color = new Color(0.80f, 0.70f, 0.55f, 0.55f); // 浅棕灰占位提示
            phComp.alignment = TextAlignmentOptions.Center;
            phComp.text = "说点什么…";
            phComp.raycastTarget = false;

            _input = inputGo.AddComponent<TMP_InputField>();
            _input.textComponent = textComp;
            _input.placeholder = phComp;
            _input.targetGraphic = inputImg;
            _input.lineType = TMP_InputField.LineType.SingleLine;
            _input.text = string.Empty;
        });

        // ---- AI 建议按钮（下半左 4/5 区并排，深色底米白字；初始「生成中…」不可点）----
        Step("创建建议按钮1", () => CreateSuggestionButton(panelTf, 0,
            new Vector2(sugg1X, suggY), new Vector2(suggW, suggH), font));
        Step("创建建议按钮2", () => CreateSuggestionButton(panelTf, 1,
            new Vector2(sugg2X, suggY), new Vector2(suggW, suggH), font));

        // ---- 右下 1/5 区竖排功能按钮（上→下）：完成（提交输入）+ 重新生成 + 结束对话 ----
        Step("创建结束对话按钮", () =>
        {
            _exitButton = CreateStyledButton(panelTf, "结束对话", exitPos, exitSize, font,
                "结束对话", 20f, out _);
        });
        Step("创建重新生成按钮", () =>
        {
            _regenButton = CreateStyledButton(panelTf, "重新生成", regenPos, regenSize, font,
                "重新生成", 20f, out _);
        });
        Step("创建完成按钮", () =>
        {
            _doneButton = CreateStyledButton(panelTf, "完成", donePos, doneSize, font,
                "完成", 20f, out _);
        });

        // ---- 聚焦策略：不自动聚焦——用户点击输入框后才进入输入态（Poll 里检测点击命中后 Activate）。
        // 聚焦期间 Poll 会禁用 InputSystem 键盘设备，屏蔽游戏全部快捷键（J/K/Ctrl/Esc/W 等）----
        PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 覆盖层就绪，等待点击输入框聚焦");

        // ---- 异步获取 AI 建议 ----
        _suggestionProvider = suggestionProvider; // 重新生成按钮复用
        Step("获取AI建议", () => StartSuggestions(suggestionProvider));
    }

    // ---- AI 建议：状态机（生成中… → 可用 / 建议不可用；点击=采用并确认）----

    private void CreateSuggestionButton(Transform parent, int index, Vector2 pos, Vector2 size, TMP_FontAsset? font)
    {
        var btn = CreateStyledButton(parent, $"建议{index + 1}", pos, size, font, "生成中…", 22f, out var labelComp);
        btn.interactable = false;
        // 不订阅 onClick（UnityEvent → managed thunk 会崩，见 _regenButton 注释）；
        // 点击由 Poll 轮询鼠标位置 + 命中检测
        _suggestionButtons[index] = btn;
        _suggestionLabels[index] = labelComp;
    }

    /// <summary>深色底米白字按钮（建议/重新生成共用）；返回 Button 并 out 出文本组件，供状态机改文案/置灰。</summary>
    private static Button CreateStyledButton(Transform parent, string name, Vector2 pos, Vector2 size,
        TMP_FontAsset? font, string label, float fontSize, out TextMeshProUGUI labelComp)
    {
        var go = NewUi(name + "Button", parent, pos, size);
        var img = go.AddComponent<Image>();
        img.sprite = CreateRoundedRectSprite(size.x, size.y, ButtonBg, PanelBorder, 2f, 10f); // 圆角矩形按钮
        img.color = Color.white; // sprite 已含颜色
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        // 不订阅 onClick（UnityEvent → managed thunk 会崩）；不可点时 Button 默认 ColorTint 自动半透明灰化

        var textGo = NewUi("Label", go.transform, Vector2.zero, size - new Vector2(12f, 4f));
        var text = textGo.AddComponent<TextMeshProUGUI>();
        if (font != null) text.font = font;
        text.text = label;
        text.fontSize = fontSize;
        text.color = OffWhite;
        text.alignment = TextAlignmentOptions.Center;
        text.raycastTarget = false;
        // 防超框：关闭自动换行，超出按钮宽度时由 TMP 按像素裁掉并显示省略号（Ellipsis）
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        labelComp = text;
        return btn;    }

    private void StartSuggestions(Func<CancellationToken, Task<IReadOnlyList<string>>>? provider)
    {
        if (provider == null)
        {
            SetSuggestionsUnavailable();
            return;
        }
        _suggestionsLoading = true;

        PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 建议任务启动");
        _ = Task.Run(async () =>
        {
            IReadOnlyList<string>? result = null;
            try
            {
                var timeout = Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1f, PluginContext.Settings.TimeoutSeconds)));
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
        });
    }

    /// <summary>建议结果落回主线程：填充按钮文本并放开点击；失败/超时置灰。</summary>
    private void ApplySuggestions(IReadOnlyList<string>? result)
    {
        if (_closed) return;
        _suggestionsLoading = false;
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
        _suggestionsLoading = false;
        try
        {
            for (var i = 0; i < _suggestionButtons.Length; i++)
            {
                if (UnityObjectGuard.IsDead(_suggestionButtons[i]) || UnityObjectGuard.IsDead(_suggestionLabels[i])) continue;
                // 不可点（Poll 命中检测跳过 interactable=false 的按钮）且 _suggestions 保持 null，
                // 双保险保证「未进行对话」/「建议不可用」文案绝不会被当成玩家回复提交
                _suggestionLabels[i].text = _unavailableText;
                _suggestionButtons[i].interactable = false;
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay.SetSuggestionsUnavailable 异常: {ex}");
        }
    }

    /// <summary>按钮回到「生成中…」并清空旧建议（初次生成与重新生成共用，生成中防重复点击）。</summary>
    private void SetSuggestionsLoading()
    {
        try
        {
            _suggestionsLoading = true;
            for (var i = 0; i < _suggestionButtons.Length; i++)
            {
                if (UnityObjectGuard.IsDead(_suggestionButtons[i]) || UnityObjectGuard.IsDead(_suggestionLabels[i])) continue;
                _suggestions[i] = null;
                _suggestionLabels[i].text = "生成中…";
                _suggestionButtons[i].interactable = false;
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay.SetSuggestionsLoading 异常: {ex}");
        }
    }

    /// <summary>「重新生成」点击：复用打开时的 provider 再跑一遍（生成中/无 provider 时忽略）。</summary>
    private void RegenSuggestions()
    {
        if (_closed || _suggestionsLoading || _suggestionProvider == null) return;
        PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 重新生成");
        SetSuggestionsLoading();
        StartSuggestions(_suggestionProvider);
    }

    /// <summary>
    /// 在 DialogPannel 层级里找「对话正文」文本组件：activeInHierarchy 且屏幕像素面积最大的 TMP。
    /// </summary>
    private static RectTransform? FindDialogTextArea(DialogPannel? panel)
    {
        if (UnityObjectGuard.IsDead(panel)) return null;
        RectTransform? best = null;
        var bestArea = 0f;
        foreach (var tmp in panel!.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (tmp == null) continue;
            RectTransform? rt = null;
            float w = 0f, h = 0f;
            var active = false;
            try
            {
                rt = tmp.rectTransform;
                var r = rt.rect;
                var cv = tmp.canvas;
                Vector2 bl, tr;
                if (cv == null || cv.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    // Overlay：TransformPoint 的结果即屏幕像素（见 GetOverlayScreenRect 注释）
                    GetOverlayScreenRect(rt, out bl, out tr);
                }
                else
                {
                    var cam = cv.rootCanvas.worldCamera;
                    bl = RectTransformUtility.WorldToScreenPoint(cam,
                        rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
                    tr = RectTransformUtility.WorldToScreenPoint(cam,
                        rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
                }
                w = tr.x - bl.x;
                h = tr.y - bl.y;
                active = tmp.gameObject.activeInHierarchy;
            }
            catch
            {
                continue;
            }
            if (!active || w < 100f || h < 30f) continue;
            var area = w * h;
            if (area > bestArea)
            {
                bestArea = area;
                best = rt;
            }
        }
        return best;
    }

    /// <summary>
    /// Overlay 画布下求 RectTransform 的屏幕像素矩形（左下/右上）。
    /// 不用 GetWorldCorners：其「native 回填托管数组」在 IL2CPP 下实测全部返回 (0,0)（日志实证 角0=(0,0)），
    /// 改用 TransformPoint 逐点换算（实例方法返回值，封送正常）。Overlay 模式下根 Canvas 的缩放
    /// 由 CanvasScaler 挂在变换上，TransformPoint 的结果直接就是屏幕像素。
    /// </summary>
    private static void GetOverlayScreenRect(RectTransform rt, out Vector2 bl, out Vector2 tr)
    {
        var r = rt.rect;
        var p0 = rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0f));
        var p2 = rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0f));
        bl = new Vector2(p0.x, p0.y);
        tr = new Vector2(p2.x, p2.y);
    }

    /// <summary>
    /// 把对话正文 RectTransform 的矩形换算到覆盖层根下的 anchored 位置与尺寸。
    /// 全程手动换算，不走 RectTransformUtility（其 null cam 路径在 IL2CPP 下失灵）：
    /// Overlay 的 world corners 即屏幕像素；屏幕像素 → 根 Canvas local 用
    /// local = (screen - 屏幕中心) / 缩放 + 根rect.center，缩放由 屏幕尺寸/根Canvas rect 推出。
    /// 失败/尺寸异常返回 false。
    /// </summary>
    private static bool TryMatchRect(RectTransform from, RectTransform parent,
        out Vector2 anchoredPos, out Vector2 size)
    {
        anchoredPos = default;
        size = default;
        try
        {
            if (UnityObjectGuard.IsDead(from) || UnityObjectGuard.IsDead(parent)) return false;

            // 换算目标空间：parent 所在根 Canvas 的 local 单位
            var rootCanvas = parent.GetComponentInParent<Canvas>();
            if (rootCanvas == null) return false;
            rootCanvas = rootCanvas.rootCanvas;
            var rr = rootCanvas.GetComponent<RectTransform>().rect;
            if (rr.width < 1f || rr.height < 1f) return false;
            var sx = Screen.width / rr.width;
            var sy = Screen.height / rr.height;
            if (sx <= 0f || sy <= 0f) return false;

            var fromCanvas = from.GetComponentInParent<Canvas>();
            var overlay = fromCanvas == null ||
                          fromCanvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay;

            Vector2 bl, tr;
            if (overlay)
            {
                GetOverlayScreenRect(from, out bl, out tr);
            }
            else
            {
                var r0 = from.rect;
                var cam = fromCanvas!.rootCanvas.worldCamera;
                bl = RectTransformUtility.WorldToScreenPoint(cam,
                    from.TransformPoint(new Vector3(r0.xMin, r0.yMin, 0f)));
                tr = RectTransformUtility.WorldToScreenPoint(cam,
                    from.TransformPoint(new Vector3(r0.xMax, r0.yMax, 0f)));
            }

            // 屏幕像素 → 根 Canvas local（根 rect.center 对应屏幕中心）
            var half = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            var localBl = new Vector2((bl.x - half.x) / sx, (bl.y - half.y) / sy) + rr.center;
            var localTr = new Vector2((tr.x - half.x) / sx, (tr.y - half.y) / sy) + rr.center;

            // parent（覆盖层根）锚定在根 Canvas 中心且 scale=1：parent local = 根 local - parent.rect.center
            var pc = parent.rect.center;
            localBl -= pc;
            localTr -= pc;

            size = localTr - localBl;
            if (size.x < 50f || size.y < 30f) return false;
            anchoredPos = (localBl + localTr) * 0.5f;
            return true;
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] FreeInputOverlay: 对话框矩形换算失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>按钮显示用截断（完整文本照常提交）。先按字符粗截（宽裕值 18），
    /// 剩余超出由 TMP Ellipsis 按像素精确裁掉（见 CreateStyledButton）。</summary>
    private static string TruncateForButton(string s)
        => s.Length <= 18 ? s : s.Substring(0, 18) + "…";

    private void Poll()
    {
        if (_closed || _closeQueued) return;

        // 心跳：覆盖层存活期间 Poll 一直在跑——崩溃后若心跳还在增长的时间点附近停住，
        // 可界定崩溃时刻与 Poll 无关；若心跳中断即崩，凶手在 Poll 内
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

        // 输入聚焦 ⟷ 游戏键盘快捷键屏蔽联动：
        // 聚焦时禁用 InputSystem 键盘设备（游戏快捷键 J/K/Ctrl/Esc/W 全部静默），失焦/关闭时恢复
        var focused = _input != null && !UnityObjectGuard.IsDead(_input) && _input.isFocused;
        SetKeyboardBlocked(focused);

        // 鼠标点击：不订阅 UnityEvent（native → managed thunk 会崩，见 _regenButton 注释），
        // 改为轮询左键释放 + RectTransform 命中检测（只读原生调用，全程可靠）
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
                QueueClose(text); // 等价于输入该文本后点完成，走同一 Close 路径
            return;
        }
        if (Hit(_regenButton, pos))
        {
            PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 重新生成按钮点击命中（轮询检测）");
            RegenSuggestions();
            return;
        }
        if (Hit(_exitButton, pos))
        {
            PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 结束对话按钮点击命中（轮询检测）");
            QueueClose(null); // 本句按跳过处理（显示原文），随后终止整段对话
            // Close 经 Update 通道执行完后置终止标记（FIFO 保证顺序）：
            // fastForwardMode 解锁当前句等待，shouldInterrupt 让对话循环下一轮直接结束，
            // 剩余句子不播、不进历史；面板走游戏正常关闭流程（两标记下次打开自动复位）
            var panel = _panel;
            MainThreadDispatcher.Post(() => EndConversation(panel));
            return;
        }
        if (Hit(_doneButton, pos))
        {
            PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 完成按钮点击命中（轮询检测）");
            QueueClose(_input.text);
            return;
        }

        // 点击输入框本体 → 聚焦进入输入态（用户要求：点击之后才允许输入；
        // 同时聚焦会联动屏蔽游戏快捷键，见 Poll 前段 SetKeyboardBlocked）
        if (_input != null && !UnityObjectGuard.IsDead(_input) && !_input.isFocused &&
            HitRt(_input.GetComponent<RectTransform>(), pos))
        {
            PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 输入框点击命中，聚焦进入输入态");
            _input.ActivateInputField();
        }
    }

    /// <summary>屏幕坐标是否落在 RectTransform 内（Overlay 体系，相机传 null）。失败一律按未命中。</summary>
    private static bool HitRt(RectTransform? rt, Vector2 screenPos)
    {
        try
        {
            if (UnityObjectGuard.IsDead(rt)) return false;
            return RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos, null);
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay.HitRt 异常: {ex}");
            return false;
        }
    }

    /// <summary>输入聚焦期间是否已禁用 InputSystem 键盘设备（屏蔽游戏快捷键）。</summary>
    private bool _keyboardBlocked;

    /// <summary>
    /// 禁用/恢复 InputSystem 键盘设备。禁用后游戏的对话快捷键（继续/快进/隐藏/跳过/历史）
    /// 全部收不到键入；只动设备开关、不碰游戏逻辑，恢复幂等，失败只记日志。
    /// </summary>
    private void SetKeyboardBlocked(bool blocked)
    {
        if (_keyboardBlocked == blocked) return;
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (blocked) InputSystem.DisableDevice(kb);
            else InputSystem.EnableDevice(kb);
            _keyboardBlocked = blocked;
            PluginContext.Log.LogInfo(
                $"[MystiaAI] FreeInputOverlay: {(blocked ? "输入聚焦，已屏蔽游戏键盘快捷键" : "输入失焦/关闭，已恢复游戏键盘")}");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay 键盘屏蔽切换异常: {ex}");
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

    /// <summary>
    /// 「结束对话」：对当前对话面板置 fastForwardMode + shouldInterrupt 双标记——
    /// fastForwardMode 让当前句的等待立即通过（DialogPannel.WaitForNextLine），
    /// shouldInterrupt 让主循环下一轮迭代直接 yield break（游戏自留口子，原生从未使用），
    /// 剩余句子不播、不进历史、不放音效，随后面板走游戏正常关闭流程（onChatFinished 链路完整）。
    /// 两字段在面板下次打开时由游戏自行复位，无副作用。失败只记日志（对话将照常继续）。
    /// </summary>
    private static void EndConversation(DialogPannel? panel)
    {
        try
        {
            if (UnityObjectGuard.IsDead(panel)) return;
            DialogPannelPatch.MarkExitRequested(panel); // 通知自动续聊：本轮播完不再重开
            panel!.fastForwardMode = true;
            panel.shouldInterrupt = true;
            PluginContext.Log.LogInfo("[MystiaAI] FreeInputOverlay: 已置终止标记，对话将在当前句后结束");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogWarning($"[MystiaAI] FreeInputOverlay: 结束对话置位失败（对话将照常继续）: {ex.Message}");
        }
    }

    private void Close(string? result)
    {
        PluginContext.Log.LogInfo(
            $"[MystiaAI] FreeInputOverlay: Close 进入（{(result == null ? "跳过" : "确认")}）");
        if (_closed) return;
        _closed = true;
        SetKeyboardBlocked(false); // 关闭前务必恢复游戏键盘
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

    /// <summary>
    /// 把覆盖层插到指针所在的根层级子节点之前：在 RootCanvas 直接子节点中找出
    /// 子树含 cursor/mouse 元素的那个，SetSiblingIndex 到它的位置（它随后顺移 +1）。
    /// 找不到指针时退化为「插到对话框面板正上方」（Manual 模式下指针不在根层级直子节点中），
    /// 再找不到才 SetAsLastSibling（最顶，可能遮指针，但比压在对话框下看不见强）。
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
                return;
            }

            // 回退 1：插到对话框面板（的根层级祖先）正上方
            var dialogRoot = DirectChildOf(_panel == null ? null : _panel.transform, rootTf);
            if (dialogRoot != null)
            {
                _root.transform.SetSiblingIndex(dialogRoot.GetSiblingIndex() + 1);
                PluginContext.Log.LogInfo(
                    "[MystiaAI] FreeInputOverlay: 未定位到指针元素，已插到对话框正上方");
                return;
            }

            // 回退 2：最顶（指针可能被遮，但覆盖层可见可点）
            _root.transform.SetAsLastSibling();
            PluginContext.Log.LogWarning(
                "[MystiaAI] FreeInputOverlay: 指针与对话框均未定位到，退化为 SetAsLastSibling（指针可能被遮挡，请反馈）");
        }
        catch (Exception ex)
        {
            PluginContext.Log.LogError($"[MystiaAI] FreeInputOverlay.InsertBelowCursor 异常: {ex}");
        }
    }

    /// <summary>找 t 的祖先中直接挂在 parent 下的那个节点（含 t 本身）。无则 null。</summary>
    private static Transform? DirectChildOf(Transform? t, Transform parent)
    {
        try
        {
            var cur = t;
            Transform? last = null;
            while (cur != null)
            {
                if (ReferenceEquals(cur.parent, parent)) return cur;
                last = cur;
                cur = cur.parent;
            }
            return null;
        }
        catch
        {
            return null;
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

    /// <summary>
    /// 程序化生成「圆角矩形填充 + 外侧描边」的 Sprite（矩形四角带圆角，圆角外为透明，
    /// Image 本身仍是矩形，点击拦截区域不变）。用有向距离场（SDF）绘制：
    /// 内部填充色、边界外一圈描边色、再往外透明，边缘均带抗锯齿过渡。
    /// 纹理分辨率封顶 256（长边），每次调用重新生成，不缓存。
    /// </summary>
    private static Sprite CreateRoundedRectSprite(float width, float height, Color fill, Color border,
        float borderWidth, float cornerRadius)
    {
        const int maxTex = 256;
        var scale = Mathf.Min(1f, maxTex / Mathf.Max(width, height));
        var tw = Mathf.Max(4, Mathf.RoundToInt(width * scale));
        var th = Mathf.Max(4, Mathf.RoundToInt(height * scale));
        var tex = new Texture2D(tw, th, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        var halfW = tw / 2f;
        var halfH = th / 2f;
        var radius = Mathf.Max(1f, cornerRadius * scale);       // 圆角半径（纹理像素）
        var borderPx = Mathf.Max(1f, borderWidth * scale);      // 描边带宽度（纹理像素）
        const float aa = 1f;                                    // 抗锯齿过渡宽度（纹理像素）

        for (var y = 0; y < th; y++)
        {
            for (var x = 0; x < tw; x++)
            {
                var px = x + 0.5f - halfW;
                var py = y + 0.5f - halfH;
                // 圆角矩形 SDF：d<=0 在矩形内，d>0 在矩形外（像素单位）
                var qx = Mathf.Abs(px) - (halfW - radius);
                var qy = Mathf.Abs(py) - (halfH - radius);
                var d = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f))
                    + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;

                Color c;
                float a;
                if (d < 0f)
                {
                    // 内部：填充色，边缘向描边色过渡（抗锯齿）
                    var t = Mathf.Clamp01((d + aa) / aa);
                    c = Color.Lerp(fill, border, t);
                    a = 1f;
                }
                else if (d <= borderPx - aa)
                {
                    c = border; a = 1f;                                 // 描边带
                }
                else
                {
                    // 描边外缘：向透明过渡（抗锯齿）
                    var t = Mathf.Clamp01((borderPx + aa - d) / (2f * aa));
                    c = border; a = t;
                }
                tex.SetPixel(x, y, new Color(c.r, c.g, c.b, c.a * a));
            }
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0f, 0f, tw, th), new Vector2(0.5f, 0.5f), 100f);
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
