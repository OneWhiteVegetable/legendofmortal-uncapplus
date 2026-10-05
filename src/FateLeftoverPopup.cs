using System.Collections.Generic;
using HarmonyLib;
using Mortal.Core;
using UnityEngine;
using UnityEngine.UI;

namespace UncapSixStats
{
    /// <summary>
    /// 剩余天命点提示弹窗:结束加点时若还有点数未分配,提示剩余点数会转为命运点带入游戏。
    /// 直接克隆游戏自带的确认弹窗(_confirmBonusPanel),框体/按钮/字体与游戏 UI 一致;
    /// 附加"之後不再提示"勾选(存 BepInEx 配置,不写存档)。
    /// </summary>
    internal static class FateLeftoverPopup
    {
        private const string PopupName = "FateLeftoverPopup";
        private const string MessageFormat = "尚有 {0} 點天命點數未分配。未用完的點數會轉為命運點數,在接下來的旅程中使用。現在要結束分配、繼續遊戲嗎?";

        private static GameObject _popup;
        private static Text _messageText;
        private static Image _toggleFill;
        private static FateBonusPanel _panel;
        private static bool _dontShow;

        internal static void EnsureCreated(FateBonusPanel panel)
        {
            if (_popup != null) return;
            try
            {
                Traverse t = Traverse.Create(panel);
                GameObject template = t.Field("_confirmBonusPanel").GetValue<GameObject>();
                if (template == null || template.transform.parent == null) return;

                _popup = Object.Instantiate(template, template.transform.parent);
                _popup.name = PopupName;

                // 去掉本地化组件,防止文案被刷回原文
                foreach (Component comp in _popup.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null || comp is Transform) continue;
                    string ns = comp.GetType().Namespace ?? "";
                    if (ns.StartsWith("Lean"))
                    {
                        Object.Destroy(comp);
                    }
                }

                // 主文案:取字符最长的那个 Text(标题/说明位),放宽并允许换行
                Text[] texts = _popup.GetComponentsInChildren<Text>(true);
                Transform innerPanel = _popup.transform.Find("Panel");
                foreach (Text text in texts)
                {
                    if (_messageText == null || text.text.Length > _messageText.text.Length)
                    {
                        _messageText = text;
                    }
                }
                if (_messageText != null)
                {
                    _messageText.horizontalOverflow = HorizontalWrapMode.Wrap;
                    _messageText.verticalOverflow = VerticalWrapMode.Overflow;
                    _messageText.alignment = TextAnchor.MiddleCenter;
                    RectTransform messageRect = (RectTransform)_messageText.transform;
                    float panelWidth = innerPanel != null ? ((RectTransform)innerPanel).rect.width : 500f;
                    messageRect.sizeDelta = new Vector2(panelWidth > 0 ? panelWidth - 60f : 440f, 150f);
                }

                // 按钮:游戏弹窗只有一个 X 关闭按钮(CloseButton_02)——它就是"取消"(留下继续加点);
                // "確定"按钮克隆继承界面右下角的確定按钮(游戏风格),放到文案正下方(沿用文案的锚点系)。
                Button[] buttons = _popup.GetComponentsInChildren<Button>(true);
                if (buttons.Length >= 1)
                {
                    Button closeX = buttons[0];
                    closeX.onClick = new Button.ButtonClickedEvent();
                    closeX.onClick.AddListener(OnCancel);
                }
                Mortal.Core.MenuToggleButton saveButton = t.Field("_propertySaveButton").GetValue<Mortal.Core.MenuToggleButton>();
                Vector2 togglePos = Vector2.zero;
                if (saveButton != null && _messageText != null)
                {
                    RectTransform messageRect = (RectTransform)_messageText.transform;
                    GameObject confirmGo = Object.Instantiate(saveButton.gameObject, _messageText.transform.parent);
                    confirmGo.name = "FatePopupConfirmBtn";
                    RectTransform confirmRect = (RectTransform)confirmGo.transform;
                    confirmRect.anchorMin = messageRect.anchorMin;
                    confirmRect.anchorMax = messageRect.anchorMax;
                    confirmRect.pivot = new Vector2(0.5f, 0.5f);
                    float below = 150f * 0.5f + confirmRect.sizeDelta.y * confirmRect.localScale.y * 0.5f + 16f;
                    confirmRect.anchoredPosition = messageRect.anchoredPosition + new Vector2(0f, -below);
                    togglePos = messageRect.anchoredPosition + new Vector2(0f, -(below + confirmRect.sizeDelta.y * confirmRect.localScale.y * 0.5f + 34f));
                    Button confirm = confirmGo.GetComponent<Button>();
                    if (confirm == null) confirm = confirmGo.GetComponentInChildren<Button>(true);
                    if (confirm != null)
                    {
                        confirm.onClick = new Button.ButtonClickedEvent();
                        confirm.onClick.AddListener(OnConfirm);
                    }
                }

                BuildToggle(texts, innerPanel, _messageText, togglePos);
                _popup.SetActive(false);
                string layout = _messageText != null
                    ? $" 文案锚点{((RectTransform)_messageText.transform).anchorMin} 位置{((RectTransform)_messageText.transform).anchoredPosition}"
                    : "";
                Plugin.Log.LogInfo($"[天命] 剩餘點數提示彈窗已克隆遊戲彈窗創建(文本{texts.Length}個,按鈕{buttons.Length}個){layout}");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[天命] 提示彈窗創建失敗:" + e);
                _popup = null;
            }
        }

        // TMP 文本统一改字(游戏的弹窗按钮用 TextMeshProUGUI,反射写入,不引程序集)
        private static void SetTmpTexts(GameObject root, string value)
        {
            foreach (Component comp in root.GetComponentsInChildren<Component>(true))
            {
                if (comp != null && comp.GetType().FullName == "TMPro.TextMeshProUGUI")
                {
                    Traverse.Create(comp).Property("text").SetValue(value);
                }
            }
        }

        // "之後不再提示"勾选行:自绘小方框 + 文字,风格贴近游戏弹窗(確定按钮再下方,沿用文案锚点系)
        private static void BuildToggle(Text[] texts, Transform innerPanel, Text messageText, Vector2 pos)
        {
            Font font = null;
            foreach (Text text in texts)
            {
                if (text != null && text.font != null) { font = text.font; break; }
            }
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            GameObject row = new GameObject("DontShowRow", typeof(RectTransform));
            row.transform.SetParent(messageText != null ? messageText.transform.parent : (innerPanel != null ? innerPanel : _popup.transform), false);
            RectTransform rowRect = (RectTransform)row.transform;
            if (messageText != null)
            {
                RectTransform messageRect = (RectTransform)messageText.transform;
                rowRect.anchorMin = messageRect.anchorMin;
                rowRect.anchorMax = messageRect.anchorMax;
            }
            else
            {
                rowRect.anchorMin = new Vector2(0.5f, 0f);
                rowRect.anchorMax = new Vector2(0.5f, 0f);
            }
            rowRect.pivot = new Vector2(0.5f, 0.5f);
            rowRect.anchoredPosition = pos;
            rowRect.sizeDelta = new Vector2(220f, 30f);

            Image rowBg = row.AddComponent<Image>();
            rowBg.color = new Color(0f, 0f, 0f, 0f);
            Button rowButton = row.AddComponent<Button>();
            rowButton.targetGraphic = rowBg;
            Navigation nav = rowButton.navigation;
            nav.mode = Navigation.Mode.None;
            rowButton.navigation = nav;

            GameObject box = new GameObject("Box", typeof(RectTransform));
            box.transform.SetParent(row.transform, false);
            RectTransform boxRect = (RectTransform)box.transform;
            boxRect.anchorMin = new Vector2(0f, 0.5f);
            boxRect.anchorMax = new Vector2(0f, 0.5f);
            boxRect.pivot = new Vector2(0f, 0.5f);
            boxRect.anchoredPosition = new Vector2(2f, 0f);
            boxRect.sizeDelta = new Vector2(22f, 22f);
            Image boxImage = box.AddComponent<Image>();
            boxImage.color = new Color(0.16f, 0.17f, 0.19f, 0.95f);

            GameObject fill = new GameObject("Fill", typeof(RectTransform));
            fill.transform.SetParent(box.transform, false);
            RectTransform fillRect = (RectTransform)fill.transform;
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = new Vector2(3f, 3f);
            fillRect.offsetMax = new Vector2(-3f, -3f);
            _toggleFill = fill.AddComponent<Image>();
            _toggleFill.color = Plugin.FateOverCapColor;

            GameObject labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(row.transform, false);
            RectTransform labelRect = (RectTransform)labelGo.transform;
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(1f, 1f);
            labelRect.offsetMin = new Vector2(32f, 0f);
            labelRect.offsetMax = Vector2.zero;
            Text label = labelGo.AddComponent<Text>();
            label.font = font;
            label.text = "之後不再提示";
            label.fontSize = 20;
            label.color = new Color(0.88f, 0.9f, 0.94f);
            label.alignment = TextAnchor.MiddleLeft;
            label.raycastTarget = false;

            rowButton.onClick.AddListener(() =>
            {
                _dontShow = !_dontShow;
                RefreshToggle();
            });
        }

        private static void RefreshToggle()
        {
            if (_toggleFill != null)
            {
                _toggleFill.enabled = _dontShow;
            }
        }

        internal static void Show(FateBonusPanel panel, int remaining)
        {
            EnsureCreated(panel);
            if (_popup == null)
            {
                // 弹窗不可用时直接放行,绝不卡点
                Traverse.Create(panel).Field("_pressCancel").SetValue(true);
                return;
            }
            _panel = panel;
            _dontShow = false;
            RefreshToggle();
            if (_messageText != null)
            {
                _messageText.text = string.Format(MessageFormat, remaining);
            }
            SetUnderlyingInteractable(panel, false);
            _popup.SetActive(true);
            _popup.transform.SetAsLastSibling();
        }

        private static void Hide()
        {
            if (_popup != null)
            {
                _popup.SetActive(false);
            }
            if (_panel != null)
            {
                SetUnderlyingInteractable(_panel, true);
            }
        }

        // 弹窗期间锁住下面的加点界面(复刻原版 OpenConfirmPanel/CloseConfirmPanel 的做法)
        private static void SetUnderlyingInteractable(FateBonusPanel panel, bool interactable)
        {
            try
            {
                Traverse t = Traverse.Create(panel);
                Selectable[] buttons = t.Field("_interactableButtons").GetValue<Selectable[]>();
                if (buttons != null)
                {
                    foreach (Selectable selectable in buttons)
                    {
                        if (selectable != null) selectable.interactable = interactable;
                    }
                }
                object closeButton = t.Field("_closeButton_1").GetValue<object>();
                if (closeButton != null)
                {
                    Traverse.Create(closeButton).Method("SetActive", interactable).GetValue();
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[天命] 彈窗鎖定狀態切換失敗:" + e.Message);
            }
        }

        private static void OnConfirm()
        {
            if (_dontShow)
            {
                Plugin.FateSkipLeftoverWarning.Value = true;
            }
            FateBonusPanel panel = _panel;
            Hide();
            if (panel != null)
            {
                Traverse.Create(panel).Field("_pressCancel").SetValue(true);
            }
        }

        private static void OnCancel()
        {
            Hide();
        }
    }
}
