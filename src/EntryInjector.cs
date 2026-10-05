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
    /// 位置:菜单打开后延迟两帧,量出源按钮最宽子元素(悬停红幅)的实际宽度,
    /// 让两个按钮的块恰好相挨(无缝隙),与分辨率无关。
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

    internal static class MenuEntryInjector
    {
        private const string EntryName = "UncapSixStatsEntry";

        internal static void EnsureCreated(Mortal.Core.MenuPanel panel)
        {
            try
            {
                Button source = Traverse.Create(panel).Field("_systemSettingButton").GetValue<Button>();
                if (source == null || source.transform.parent == null) return;
                if (source.transform.parent.Find(EntryName) != null) return;
                Create(source);

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
            // 等两帧,让 VerticalLayoutGroup 和画布缩放完成计算
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
                Plugin.Log.LogInfo($"[自检] 红幅对位: 红幅宽={bannerWidth:F1}, 缩放={sourceRect.lossyScale.x:F2}, 偏移={offset:F1}");
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

            // 借游戏菜单按钮的字体给 Mod 面板用(比全局找到的字体更贴合菜单风格)
            Text sourceText = source.GetComponentInChildren<Text>(true);
            if (sourceText != null && sourceText.font != null)
            {
                ModPanelUI.SetFontPreference(sourceText.font);
            }

            // 防布局组把它收回按钮队列;初始位置先贴着源按钮,打开菜单时精确定位
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
