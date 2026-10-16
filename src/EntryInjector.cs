using System.Collections;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace UncapSixStats
{
    /// <summary>
    /// 把「上限解除」入口按钮注入游戏自带的系统菜单(MenuPanel):
    /// 克隆其「環境設定」按钮(含 MenuToggleButton 的悬停红幅/音效,风格完全一致),
    /// 所有状态子物体的文字都改掉(否则悬停态会显示回"環境設定")。
    /// 位置:菜单打开后延迟两帧,量出源按钮悬停红幅的实际宽度,贴到環境設定右侧(不占菜单栏位,
    /// 避免把「回到標題」顶出屏幕)。環境設定的次级面板打开时,本按钮自动隐藏(防止遮挡/吃掉
    /// 次级面板的点击),关闭后自动恢复。
    /// 同时在 MenuPanel 上挂 MenuCloseWatcher:系统菜单关闭时联动关闭 Mod 面板。
    /// </summary>
    [HarmonyPatch(typeof(Mortal.Core.MenuPanel), "Awake")]
    internal static class MenuPanelAwakePatch
    {
        private static void Postfix(Mortal.Core.MenuPanel __instance)
        {
            MenuEntryInjector.EnsureCreated(__instance);
        }
    }

    [HarmonyPatch(typeof(Mortal.Core.MenuPanel), "OnPanelOpen")]
    internal static class MenuPanelOpenPatch
    {
        private static void Postfix(Mortal.Core.MenuPanel __instance)
        {
            MenuEntryInjector.ScheduleReposition(__instance);
        }
    }

    /// <summary>
    /// 挂在 MenuPanel 物体上的联动关闭监视器。
    /// 游戏内场景物体的组件消息循环是正常的(破限层动画已证明),
    /// 任何关闭路径(继续游戏/右键/ESC/场景切换)最终都会让菜单失活,
    /// 只在"从开着变成关着"的跳变时联动关闭 Mod 面板,避免误伤独立打开的情况。
    /// </summary>
    internal class MenuCloseWatcher : MonoBehaviour
    {
        private CanvasGroup _group;
        private bool _wasOpen;

        private void Awake()
        {
            _group = GetComponent<CanvasGroup>();
        }

        private void Update()
        {
            bool menuOpen = gameObject.activeInHierarchy && (_group == null || _group.alpha > 0.01f);
            if (_wasOpen && !menuOpen && ModPanelUI.IsOpen)
            {
                ModPanelUI.Toggle();
            }
            _wasOpen = menuOpen;
        }

        private void OnDisable()
        {
            if (_wasOpen && ModPanelUI.IsOpen)
            {
                ModPanelUI.Toggle();
            }
            _wasOpen = false;
        }
    }

    /// <summary>
    /// 挂在 MenuPanel 物体上(菜单物体始终激活,Update 不会被停用):
    /// 菜单的次级面板(環境設定/儲存/讀取)打开时隐藏「上限解除」按钮,
    /// 防止盖在次级面板上吃点击(界面冲突的根因);次级面板关闭后恢复。
    /// </summary>
    internal class EntryVisibilityWatcher : MonoBehaviour
    {
        private static readonly string[] InnerPanelFields = { "_systemSettingsPanel", "_savePanel", "_loadPanel" };

        private GameObject _entry;
        private Component[] _panels;
        private bool _init;

        private void Update()
        {
            if (!_init)
            {
                _init = true;
                Mortal.Core.MenuPanel panel = GetComponent<Mortal.Core.MenuPanel>();
                if (panel == null) return;
                System.Collections.Generic.List<Component> found = new System.Collections.Generic.List<Component>();
                foreach (string field in InnerPanelFields)
                {
                    Component c = Traverse.Create(panel).Field(field).GetValue<Component>();
                    if (c != null) found.Add(c);
                }
                _panels = found.ToArray();
            }
            if (_entry == null)
            {
                Mortal.Core.MenuPanel panel = GetComponent<Mortal.Core.MenuPanel>();
                Button source = panel != null
                    ? Traverse.Create(panel).Field("_systemSettingButton").GetValue<Button>()
                    : null;
                Transform found = source != null ? source.transform.parent.Find(MenuEntryInjector.EntryName) : null;
                if (found != null) _entry = found.gameObject;
                return;
            }
            if (_panels == null) return;
            bool innerOpen = false;
            foreach (Component c in _panels)
            {
                if (c != null && c.gameObject.activeSelf)
                {
                    innerOpen = true;
                    break;
                }
            }
            if (_entry.activeSelf == innerOpen)
            {
                _entry.SetActive(!innerOpen);
            }
        }
    }

    internal static class MenuEntryInjector
    {
        internal const string EntryName = "UncapSixStatsEntry";

        internal static void EnsureCreated(Mortal.Core.MenuPanel panel)
        {
            try
            {
                Button source = Traverse.Create(panel).Field("_systemSettingButton").GetValue<Button>();
                if (source == null || source.transform.parent == null) return;
                Transform existing = source.transform.parent.Find(EntryName);
                if (existing == null)
                {
                    Create(source);
                }

                // 联动关闭监视器(挂在游戏自己的物体上,组件消息循环正常)
                if (panel.GetComponent<MenuCloseWatcher>() == null)
                {
                    panel.gameObject.AddComponent<MenuCloseWatcher>();
                }

                Plugin.Log.LogInfo("[UncapSixStats] 已向系统菜单注入「上限解除」按钮");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[UncapSixStats] 注入系统菜单按钮失败:" + e);
            }
        }

        internal static void ScheduleReposition(Mortal.Core.MenuPanel panel)
        {
            try
            {
                panel.StartCoroutine(RepositionLater(panel));
            }
            catch { }
        }

        private static IEnumerator RepositionLater(Mortal.Core.MenuPanel panel)
        {
            // 等两帧,让布局组和画布缩放完成计算
            yield return null;
            yield return null;
            try
            {
                Button source = Traverse.Create(panel).Field("_systemSettingButton").GetValue<Button>();
                if (source == null) yield break;
                Transform parent = source.transform.parent;
                Transform entry = parent != null ? parent.Find(EntryName) : null;
                if (entry == null) yield break;

                RectTransform sourceRect = (RectTransform)source.transform;
                RectTransform entryRect = (RectTransform)entry;

                // 量「環境設定」悬停红幅(_hover 子物体)的实测世界宽度,让两块红幅恰好相挨;
                // 克隆体与源同尺寸同 pivot,偏移一个红幅宽度即无缝相接,与分辨率无关
                float bannerWidth = 0f;
                Mortal.Core.MenuToggleButton toggle = source.GetComponent<Mortal.Core.MenuToggleButton>();
                if (toggle != null)
                {
                    GameObject hover = Traverse.Create(toggle).Field("_hover").GetValue<GameObject>();
                    RectTransform hoverRect = hover != null ? hover.transform as RectTransform : null;
                    if (hoverRect != null)
                    {
                        bannerWidth = hoverRect.rect.width * hoverRect.lossyScale.x;
                    }
                }
                if (bannerWidth <= 0f)
                {
                    bannerWidth = sourceRect.rect.width * sourceRect.lossyScale.x;
                }
                // 红幅贴图四周有透明边,矩形相挨后视觉仍有缝隙 → 内缩 4% 重叠抵消
                float offset = bannerWidth * 0.96f;
                entryRect.position = sourceRect.position + new Vector3(offset, 0f, 0f);
            }
            catch { }
        }

        private static void Create(Button source)
        {
            Transform parent = source.transform.parent;
            GameObject clone = Object.Instantiate(source.gameObject, parent);
            clone.name = EntryName;

            // 去掉本地化组件,防止文本被刷回「環境設定」;保留 MenuToggleButton(悬停红幅/音效)
            foreach (Component comp in clone.GetComponentsInChildren<Component>(true))
            {
                if (comp == null || comp is Transform) continue;
                string ns = comp.GetType().Namespace ?? "";
                if (ns.StartsWith("Lean"))
                {
                    Object.Destroy(comp);
                }
            }

            // 所有状态子物体(正常/悬停/选中/禁用)的文字全部改掉
            foreach (Text text in clone.GetComponentsInChildren<Text>(true))
            {
                text.text = "上限解除";
            }
            foreach (Component comp in clone.GetComponentsInChildren<Component>(true))
            {
                if (comp != null && comp.GetType().FullName == "TMPro.TextMeshProUGUI")
                {
                    Traverse.Create(comp).Property("text").SetValue("上限解除");
                }
            }

            // 清空克隆来的点击事件(否则点了会打开环境设定),装上面板开关
            Button button = clone.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(ModPanelUI.Toggle);

            // 環境設定等次级面板打开时自动隐藏,解决界面冲突
            // (挂在 MenuPanel 上而非克隆体自身:被隐藏的物体 Update 会停,自己藏了就再也回不来)
            Mortal.Core.MenuPanel menuPanel = source.GetComponentInParent<Mortal.Core.MenuPanel>();
            if (menuPanel != null && menuPanel.GetComponent<EntryVisibilityWatcher>() == null)
            {
                menuPanel.gameObject.AddComponent<EntryVisibilityWatcher>();
            }

            // 借游戏菜单按钮的字体给 Mod 面板用(比全局找到的字体更贴合菜单风格)
            Text sourceText = source.GetComponentInChildren<Text>(true);
            if (sourceText != null && sourceText.font != null)
            {
                ModPanelUI.SetFontPreference(sourceText.font);
            }

            // 脱离布局组(不占栏位,不会把「回到標題」顶出屏幕);初始位置先贴着源按钮,
            // 打开菜单时 ScheduleReposition 精确定位到環境設定右侧
            LayoutElement layoutElement = clone.AddComponent<LayoutElement>();
            layoutElement.ignoreLayout = true;
            RectTransform sourceRect = (RectTransform)source.transform;
            RectTransform cloneRect = (RectTransform)clone.transform;
            cloneRect.anchorMin = sourceRect.anchorMin;
            cloneRect.anchorMax = sourceRect.anchorMax;
            cloneRect.pivot = sourceRect.pivot;
            cloneRect.sizeDelta = sourceRect.sizeDelta;
            cloneRect.localScale = sourceRect.localScale;
            cloneRect.anchoredPosition = sourceRect.anchoredPosition;
        }
    }
}
