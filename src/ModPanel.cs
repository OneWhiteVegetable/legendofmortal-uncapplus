using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UncapSixStats
{
    /// <summary>
    /// uGUI Mod 面板。创建时机:读档/新游戏进游戏后(由 GameStat.SetValue/Reset 补丁触发),
    /// 此时场景已就绪,DontDestroyOnLoad 安全,不会被场景切换销毁。
    /// 交互用标准 uGUI Button + 游戏内 EventSystem。
    /// 系统菜单关闭时联动关闭(见 EntryInjector.cs 的 MenuCloseWatcher)。
    /// 全部文案使用繁体中文,与游戏字体(DFT 书法体)覆盖范围一致,避免简体字回退成黑体。
    /// 面板顶部有补丁自测状态行:运行时创建一个临时 GameStat 写入 150,验证取消上限补丁是否真的生效。
    /// </summary>
    internal static class ModPanelUI
    {
        private static readonly System.Collections.Generic.List<System.Action> _refreshers =
            new System.Collections.Generic.List<System.Action>();

        private static GameObject _root;
        private static GameObject _panel;
        private static Text _statusText;
        private static bool _created;
        private static Font _font;

        internal static bool IsOpen => _panel != null && _panel.activeSelf;

        /// <summary>一次性创建入口,可从任何已证实会执行的游戏上下文调用(主线程)。</summary>
        internal static void TryCreate()
        {
            if (_created) return;
            _created = true;
            LogEnvironmentProbe();
            try
            {
                Build();
                Plugin.Log.LogInfo("[UncapSixStats] uGUI 面板创建成功(读档/新游戏后)");
                bool ok = SelfTestUncap();
                Plugin.Log.LogInfo("[自检] 取消上限补丁自测: 临时属性写入150 → 读取=" +
                    (ok ? "150(生效)" : "被钳回100(未生效!)"));
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[UncapSixStats] 面板创建失败:" + e);
            }
        }

        /// <summary>
        /// 补丁有效性自测:创建一个临时 GameStat(武功刀劍),写入 150。
        /// 补丁生效则 FinalValue=150,未生效则被原版钳回 100。
        /// </summary>
        internal static bool SelfTestUncap()
        {
            Mortal.Core.GameStat stat = null;
            try
            {
                stat = ScriptableObject.CreateInstance<Mortal.Core.GameStat>();
                Traverse traverse = Traverse.Create(stat);
                traverse.Field("_type").SetValue(Mortal.Core.GameStatType.武功刀劍);
                traverse.Field("_property").SetValue(Mortal.Core.GameStatPropertyType.武功);
                stat.SetValue(150);
                return stat.FinalValue >= 150;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[自检] 补丁自测异常:" + e);
                return false;
            }
            finally
            {
                if (stat != null)
                {
                    Object.Destroy(stat);
                }
            }
        }

        internal static void UpdateStatusText()
        {
            if (_statusText == null) return;
            bool ok = SelfTestUncap();
            _statusText.text = ok ? "上限解除狀態:生效中" : "上限解除狀態:未生效!(請把 BepInEx/LogOutput.log 發給作者)";
            _statusText.color = ok ? new Color(0.55f, 0.9f, 0.55f) : new Color(1f, 0.45f, 0.4f);
        }

        /// <summary>优先采用游戏菜单按钮的字体;面板已建好时同步替换所有文字。</summary>
        internal static void SetFontPreference(Font font)
        {
            if (font == null || _font == font) return;
            _font = font;
            if (_root == null) return;
            foreach (Text text in _root.GetComponentsInChildren<Text>(true))
            {
                text.font = font;
            }
        }

        // 全量自检:一次性把"宿主/画布/事件系统/场景"的真相写进日志
        private static void LogEnvironmentProbe()
        {
            try
            {
                Plugin.Log.LogInfo("[自检] ===== 环境探针 =====");
                Plugin hostComponent = Object.FindObjectOfType<Plugin>();
                Plugin.Log.LogInfo("[自检] BepInEx 宿主上的 Plugin 组件存活: " + (hostComponent != null));
                Plugin.Log.LogInfo("[自检] Awake 期标记物体存活: " + Plugin.AwakeMarkerAlive);
                Plugin.Log.LogInfo("[自检] 屏幕: " + Screen.width + "x" + Screen.height);
                EventSystem[] eventSystems = Object.FindObjectsOfType<EventSystem>();
                Plugin.Log.LogInfo("[自检] EventSystem 数量: " + eventSystems.Length);
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    UnityEngine.SceneManagement.Scene scene =
                        UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    Plugin.Log.LogInfo("[自检] 场景[" + i + "]: " + scene.name + " (已加载=" + scene.isLoaded + ")");
                }
                Plugin.Log.LogInfo("[自检] 活动场景: " +
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
                Plugin.Log.LogInfo("[自检] ===================");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[自检] 探针异常:" + e);
            }
        }

        internal static void Toggle()
        {
            TryCreate();
            if (_panel == null) return;
            _panel.SetActive(!_panel.activeSelf);
            if (IsOpen)
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                EnsureFont();
                RefreshAll();
                UpdateStatusText();
            }
            Plugin.Log.LogInfo("[UncapSixStats] 面板:" + (IsOpen ? "打开" : "关闭"));
        }

        private static void RefreshAll()
        {
            foreach (System.Action refresher in _refreshers)
            {
                refresher();
            }
        }

        private static void Build()
        {
            EnsureFont();

            _root = new GameObject("UncapSixStats.Canvas");
            Object.DontDestroyOnLoad(_root);
            _root.AddComponent<PortraitSyncWatcher>();
            Canvas canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            CanvasScaler scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();

            // 主面板(默认隐藏),左上锚定,内部绝对坐标排布
            // 入口在游戏系统菜单里(见 EntryInjector.cs),F2 也可开关
            _panel = NewUI("Panel", _root.transform);
            SetRect(_panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(50f, -40f), new Vector2(640f, 1010f));
            Image bg = _panel.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.06f, 0.08f, 0.92f);

            float y = -14f;
            NewLabel(_panel.transform, 20f, ref y, 460f, 40f, "活俠傳 · 上限解除 Mod", 30, Color.white);
            // 关闭按钮(面板右上角 ×)
            BuildButton(_panel.transform, 566f, -14f, 50f, 40f, "×", () => Toggle());

            // 补丁自测状态行
            GameObject statusGo = NewUI("Status", _panel.transform);
            SetChildRect(statusGo, 20f, y, 600f, 30f);
            _statusText = statusGo.AddComponent<Text>();
            _statusText.font = _font;
            _statusText.fontSize = 19;
            _statusText.alignment = TextAnchor.MiddleLeft;
            _statusText.raycastTarget = false;
            y -= 36f;

            BuildSwitchRow(_panel.transform, ref y, "取消六維/武功上限(關閉後讀檔恢復)",
                () => Plugin.Enabled.Value, v => Plugin.Enabled.Value = v);

            BuildSwitchRow(_panel.transform, ref y, "趙活:你在唐門只能算是個蘿莉",
                () => Plugin.MuscleSwitch.Value, v => Plugin.MuscleSwitch.Value = v);

            BuildSwitchRow(_panel.transform, ref y, "更好的天命點分配(繼承介面)",
                () => Plugin.FateTweakSwitch.Value, v => Plugin.FateTweakSwitch.Value = v);

            BuildNumberRow(_panel.transform, ref y, "破限頂出距離係數",
                () => Plugin.RadarLogFactor.Value, v => Plugin.RadarLogFactor.Value = v, 0.2f, 0.2f, 3f, "F1");
            BuildNumberRow(_panel.transform, ref y, "破限視覺半徑上限",
                () => Plugin.RadarVisualMax.Value, v => Plugin.RadarVisualMax.Value = v, 0.2f, 1f, 3f, "F1");
            BuildNumberRow(_panel.transform, ref y, "破限層濃度",
                () => Plugin.OverlayAlpha.Value, v => Plugin.OverlayAlpha.Value = v, 0.05f, 0f, 0.8f, "F2");
            BuildSwitchRow(_panel.transform, ref y, "熒光閃閃流動效果(頂點不動)",
                () => Plugin.OverlayPulse.Value, v => Plugin.OverlayPulse.Value = v);
            BuildNumberRow(_panel.transform, ref y, "流動速度",
                () => Plugin.OverlayPulseSpeed.Value, v => Plugin.OverlayPulseSpeed.Value = v, 0.5f, 0.3f, 6f, "F1");

            NewLabel(_panel.transform, 20f, ref y, 300f, 34f, "破限層顏色基調", 21, Silver());
            float colorX = 320f;
            float colorY = y + 38f;
            BuildButton(_panel.transform, colorX, colorY, 68f, 36f, "亮銀", () => Plugin.SetOverlayColorPublic("0.92,0.95,1.0"));
            BuildButton(_panel.transform, colorX + 76f, colorY, 68f, 36f, "金色", () => Plugin.SetOverlayColorPublic("1.0,0.84,0.30"));
            BuildButton(_panel.transform, colorX + 152f, colorY, 68f, 36f, "血紅", () => Plugin.SetOverlayColorPublic("0.90,0.25,0.20"));
            BuildButton(_panel.transform, colorX + 228f, colorY, 68f, 36f, "幽青", () => Plugin.SetOverlayColorPublic("0.30,0.85,0.80"));
            y -= 44f;

            BuildSwitchRow(_panel.transform, ref y, "曲線類加成破限後繼續增長(如血量,推薦開)",
                () => Plugin.CurveExtrapolate.Value, v => Plugin.CurveExtrapolate.Value = v);
            BuildNumberRow(_panel.transform, ref y, "曲線延伸斜率倍率",
                () => Plugin.CurveExtrapolateFactor.Value, v => Plugin.CurveExtrapolateFactor.Value = v, 0.5f, 0f, 3f, "F1");

            NewLabel(_panel.transform, 20f, ref y, 600f, 34f, "六維調試(真實數值,存檔前可用 -10 還原)", 21, Silver());
            BuildStatRows(_panel.transform, ref y);

            BuildButton(_panel.transform, 20f, y, 340f, 40f, "打印數值轉換診斷到日誌(F10)",
                () => Plugin.Instance.DumpDiagnosticsPublic());
            y -= 50f;

            NewLabel(_panel.transform, 20f, ref y, 600f, 28f, "改動即時生效並自動保存;日誌在 BepInEx/LogOutput.log", 17,
                new Color(0.6f, 0.62f, 0.66f));

            // 内容多高面板就多高,底部留 14px 边距
            RectTransform panelRect = (RectTransform)_panel.transform;
            panelRect.sizeDelta = new Vector2(640f, -y + 14f);

            _panel.SetActive(false);
        }

        private static void EnsureFont()
        {
            if (_font != null) return;
            UnityEngine.Object[] texts = Object.FindObjectsOfTypeAll(typeof(Text));
            foreach (UnityEngine.Object item in texts)
            {
                Text t = item as Text;
                if (t != null && t.font != null)
                {
                    _font = t.font;
                    return;
                }
            }
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        private static Color Silver()
        {
            return new Color(0.88f, 0.9f, 0.94f);
        }

        private static GameObject NewUI(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void SetRect(GameObject go, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
            Vector2 position, Vector2 size)
        {
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static void SetChildRect(GameObject go, float x, float y, float w, float h)
        {
            SetRect(go, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(x, y), new Vector2(w, h));
        }

        private static Text NewLabel(Transform parent, float x, ref float y, float w, float h,
            string content, int size, Color color)
        {
            GameObject go = NewUI("Label", parent);
            SetChildRect(go, x, y, w, h);
            Text text = go.AddComponent<Text>();
            text.font = _font;
            text.text = content;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAnchor.MiddleLeft;
            text.raycastTarget = false;
            y -= h + 4f;
            return text;
        }

        private static Button AddButton(GameObject go, string label, int size)
        {
            Image image = go.AddComponent<Image>();
            image.color = new Color(0.2f, 0.23f, 0.28f, 0.95f);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            Navigation navigation = button.navigation;
            navigation.mode = Navigation.Mode.None;
            button.navigation = navigation;
            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(0.35f, 0.4f, 0.48f);
            colors.pressedColor = new Color(0.5f, 0.55f, 0.62f);
            button.colors = colors;

            GameObject textGo = NewUI("Text", go.transform);
            SetRect(textGo, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            Text text = textGo.AddComponent<Text>();
            text.font = _font;
            text.text = label;
            text.fontSize = size;
            text.color = Silver();
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;
            return button;
        }

        // 在面板绝对坐标处造按钮(topY 为面板内 y 游标值,负向向下)
        private static Text BuildButton(Transform parent, float x, float topY, float w, float h,
            string label, UnityAction action)
        {
            GameObject go = NewUI("Btn", parent);
            SetChildRect(go, x, topY, w, h);
            Button button = AddButton(go, label, 20);
            button.onClick.AddListener(action);
            button.onClick.AddListener(RefreshAll);
            return go.GetComponentInChildren<Text>();
        }

        // 滑条开关:轨道 + 滑块,开=金色轨道滑块在右,关=灰色轨道滑块在左,一眼可读
        private static void BuildSwitchRow(Transform parent, ref float y, string label,
            System.Func<bool> get, System.Action<bool> set)
        {
            float rowTop = y;
            NewLabel(parent, 20f, ref y, 460f, 44f, label, 21, Color.white);

            GameObject track = NewUI("Switch", parent);
            // 行高44,文本垂直居中于 rowTop-22;滑条高28,顶部放 rowTop-8 即与文本同心
            SetChildRect(track, 506f, rowTop - 8f, 96f, 28f);
            Image trackImage = track.AddComponent<Image>();
            Button button = track.AddComponent<Button>();
            button.targetGraphic = trackImage;
            Navigation navigation = button.navigation;
            navigation.mode = Navigation.Mode.None;
            button.navigation = navigation;
            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(1f, 1f, 1f, 1f);
            colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            button.colors = colors;

            GameObject knob = NewUI("Knob", track.transform);
            RectTransform knobRect = knob.GetComponent<RectTransform>();
            knobRect.anchorMin = new Vector2(0f, 0.5f);
            knobRect.anchorMax = new Vector2(0f, 0.5f);
            knobRect.pivot = new Vector2(0f, 0.5f);
            knobRect.sizeDelta = new Vector2(22f, 22f);
            Image knobImage = knob.AddComponent<Image>();
            knobImage.raycastTarget = false;

            button.onClick.AddListener(() =>
            {
                set(!get());
                RefreshAll();
            });

            System.Action refresh = () =>
            {
                bool on = get();
                trackImage.color = on ? new Color(0.52f, 0.42f, 0.16f, 0.95f) : new Color(0.22f, 0.24f, 0.28f, 0.95f);
                knobImage.color = on ? new Color(1f, 0.84f, 0.3f) : new Color(0.55f, 0.57f, 0.62f);
                knobRect.anchoredPosition = on ? new Vector2(70f, 0f) : new Vector2(4f, 0f);
            };
            _refreshers.Add(refresh);
            refresh();
        }

        private static void BuildNumberRow(Transform parent, ref float y, string label,
            System.Func<float> get, System.Action<float> set, float step, float min, float max, string format)
        {
            float rowTop = y;
            NewLabel(parent, 20f, ref y, 300f, 44f, label, 21, Color.white);
            BuildButton(parent, 330f, rowTop, 60f, 40f, "-", () =>
            {
                set(Mathf.Max(min, Mathf.Round((get() - step) * 100f) / 100f));
            });
            GameObject valueGo = NewUI("Value", parent);
            SetChildRect(valueGo, 396f, rowTop, 90f, 40f);
            Text valueText = valueGo.AddComponent<Text>();
            valueText.font = _font;
            valueText.fontSize = 21;
            valueText.color = Silver();
            valueText.alignment = TextAnchor.MiddleCenter;
            valueText.raycastTarget = false;
            BuildButton(parent, 492f, rowTop, 60f, 40f, "+", () =>
            {
                set(Mathf.Min(max, Mathf.Round((get() + step) * 100f) / 100f));
            });
            System.Action refresh = () => valueText.text = get().ToString(format);
            _refreshers.Add(refresh);
            refresh();
        }

        private static void BuildStatRows(Transform parent, ref float y)
        {
            Mortal.Core.GameStatType[] types =
            {
                Mortal.Core.GameStatType.體力, Mortal.Core.GameStatType.內力, Mortal.Core.GameStatType.輕功,
                Mortal.Core.GameStatType.武功刀劍, Mortal.Core.GameStatType.武功暗器, Mortal.Core.GameStatType.武功拳掌
            };
            foreach (Mortal.Core.GameStatType type in types)
            {
                float rowTop = y;
                GameObject labelGo = NewUI("StatLabel", parent);
                SetChildRect(labelGo, 20f, rowTop, 280f, 40f);
                Text valueText = labelGo.AddComponent<Text>();
                valueText.font = _font;
                valueText.fontSize = 20;
                valueText.color = Color.white;
                valueText.alignment = TextAnchor.MiddleLeft;
                valueText.raycastTarget = false;
                y -= 44f;
                Mortal.Core.GameStatType captured = type;
                BuildButton(parent, 320f, rowTop, 90f, 40f, "-10", () => Plugin.Instance.AddStatPublic(captured, -10));
                BuildButton(parent, 420f, rowTop, 90f, 40f, "+10", () => Plugin.Instance.AddStatPublic(captured, 10));
                System.Action refresh = () =>
                {
                    Mortal.Core.GameStat stat = GetStat(captured);
                    valueText.text = stat != null ? $"{captured} = {stat.FinalValue}" : $"{captured}(讀檔後可用)";
                };
                _refreshers.Add(refresh);
                refresh();
            }
        }

        private static Mortal.Core.GameStat GetStat(Mortal.Core.GameStatType type)
        {
            try
            {
                Mortal.Core.PlayerStatManagerData manager = Mortal.Core.PlayerStatManagerData.Instance;
                if (manager == null || manager.Stats == null) return null;
                return manager.Stats.Get(type);
            }
            catch
            {
                return null;
            }
        }
    }
}
