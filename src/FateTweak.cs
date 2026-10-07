using System.Collections.Generic;
using HarmonyLib;
using Mortal.Core;
using UnityEngine;
using UnityEngine.UI;

namespace UncapSixStats
{
    /// <summary>
    /// 更好的天命点分配(FateTweak):继承界面三页签自由切换、加点立即落实、
    /// 各项可减(只退已分配,不动初始值)、性格偏离初始才扣费/回到初始退费、
    /// 复原=全部回到打开面板时、确定=结束加点、剩余点数提示、六维/武学/银两/锻造/炼丹破限变色。
    /// 所有补丁以 Plugin.FateTweakSwitch 为总闸:关闭时完全走原版逻辑。
    ///
    /// 点数守恒红线:绝不直接写 _fateStat 的值,一切变动只通过游戏自身 AddValue(±1/次),
    /// 且每次变动都在日志留痕([天命] 前缀),可随时审计对账。
    ///
    /// 立即落实模型:不再调用 ModifyExecute(原"确定"落实),_default 恒为面板打开时快照,
    /// _currentAdd 语义 = 当前值相对初始快照的位移(可正可负,性格双向)。
    /// </summary>
    internal static class FateTweak
    {
        internal static bool On => Plugin.FateTweakSwitch != null && Plugin.FateTweakSwitch.Value;

        // 性格(双向,费用=位移变化);其余项只能加不能低于初始值
        private static bool IsPersonality(Mortal.Core.GameStat stat)
        {
            return stat.PropertyType == Mortal.Core.GameStatPropertyType.陰陽
                || stat.PropertyType == Mortal.Core.GameStatPropertyType.善惡;
        }

        private static int DisplacementCost(int def, int oldValue, int newValue, int addPoint)
        {
            float delta = (Mathf.Abs(newValue - def) - Mathf.Abs(oldValue - def)) / (float)Mathf.Max(1, addPoint);
            return Mathf.RoundToInt(delta);
        }

        // 给按钮接 RepeatPress:按下即触发一次,长按连发(清空原 onClick,避免抬起再触发一次)
        internal static void WireRepeat(Button button, System.Action action)
        {
            if (button == null || action == null) return;
            button.onClick = new Button.ButtonClickedEvent();
            RepeatPress repeat = button.GetComponent<RepeatPress>();
            if (repeat == null)
            {
                repeat = button.gameObject.AddComponent<RepeatPress>();
            }
            repeat.Action = action;
        }

        // 社交/门派项没有减号按钮:克隆一个放在加号左侧。
        // 优先克隆属性项自带的减号按钮(游戏原生 "−" 图标);没有模板时退而克隆加号按钮再补 "−" 文字。
        internal static Button EnsureMinusButton(Button addButton, Transform itemRoot, System.Action minusAction, string name)
        {
            Transform parent = addButton.transform.parent;
            Transform existing = parent.Find(name);
            if (existing != null)
            {
                return existing.GetComponent<Button>();
            }
            Button template = addButton;
            FateBonusProperty anyProperty = Object.FindObjectOfType<FateBonusProperty>();
            if (anyProperty != null)
            {
                Button propertyMinus = Traverse.Create(anyProperty).Field("_minusButton").GetValue<Button>();
                if (propertyMinus != null)
                {
                    template = propertyMinus;
                }
            }
            GameObject clone = Object.Instantiate(template.gameObject, parent);
            clone.name = name;
            RectTransform addRect = (RectTransform)addButton.transform;
            RectTransform minusRect = (RectTransform)clone.transform;
            minusRect.anchorMin = addRect.anchorMin;
            minusRect.anchorMax = addRect.anchorMax;
            minusRect.pivot = addRect.pivot;
            minusRect.sizeDelta = addRect.sizeDelta;
            minusRect.localScale = addRect.localScale;
            // 镜像到加号左边,间隔一个按钮宽
            minusRect.anchoredPosition = addRect.anchoredPosition + new Vector2(-addRect.sizeDelta.x * 1.1f, 0f);
            clone.SetActive(false);

            Button minus = clone.GetComponent<Button>();
            // 克隆的是加号时(找不到模板兜底):加号是图标,补一个 "−" 文字盖上去
            if (template == addButton)
            {
                Text label = clone.GetComponentInChildren<Text>(true);
                if (label == null)
                {
                    GameObject textGo = new GameObject("MinusLabel", typeof(RectTransform));
                    textGo.transform.SetParent(clone.transform, false);
                    RectTransform textRect = (RectTransform)textGo.transform;
                    textRect.anchorMin = Vector2.zero;
                    textRect.anchorMax = Vector2.one;
                    textRect.offsetMin = Vector2.zero;
                    textRect.offsetMax = Vector2.zero;
                    label = textGo.AddComponent<Text>();
                    Font font = null;
                    Text anyText = itemRoot.GetComponentInChildren<Text>(true);
                    if (anyText != null) font = anyText.font;
                    if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    label.font = font;
                    label.fontSize = 28;
                    label.color = Color.white;
                    label.alignment = TextAnchor.MiddleCenter;
                    label.raycastTarget = false;
                }
                label.text = "−";
            }
            WireRepeat(minus, minusAction);
            return minus;
        }

        // 找 EnsureMinusButton 造出的减号(供 UpdateButton 控制显隐)
        private static Button FindMinus(Button addButton, string name)
        {
            if (addButton == null || addButton.transform.parent == null) return null;
            Transform found = addButton.transform.parent.Find(name);
            return found != null ? found.GetComponent<Button>() : null;
        }

        private const string SocialMinusName = "FateMinusBtn";
        private const string FlagMinusName = "FateMinusBtn";

        // ----------------------------------------------------------------
        // 属性项 FateBonusProperty
        // ----------------------------------------------------------------

        internal static void PropertyClick(FateBonusProperty item, int direction)
        {
            Traverse t = Traverse.Create(item);
            Mortal.Core.GameStat stat = t.Field("_stat").GetValue<Mortal.Core.GameStat>();
            Mortal.Core.GameStat fateStat = t.Field("_fateStat").GetValue<Mortal.Core.GameStat>();
            int addPoint = t.Field("_addPoint").GetValue<int>();
            int def = t.Field("_default").GetValue<int>();
            int currentAdd = t.Field("_currentAdd").GetValue<int>();
            if (stat == null || fateStat == null || addPoint == 0) return;

            bool personality = IsPersonality(stat);
            bool uncapped = Plugin.IsUncapped(stat);
            int oldValue = def + currentAdd;
            int lower = personality ? stat.Min : def;
            int upper = uncapped ? int.MaxValue : stat.Max;
            int newValue = Mathf.Clamp(oldValue + direction * addPoint, lower, upper);
            if (newValue == oldValue) return;

            int cost = DisplacementCost(def, oldValue, newValue, addPoint);
            if (cost > 0 && fateStat.FinalValue < cost) return;

            stat.AddValue(newValue - oldValue); // 破限类型由 IsUncapped 补丁放行,不钳 Max
            fateStat.AddValue(-cost);           // cost<0 即退费;点数池只经游戏自身 AddValue
            t.Field("_usedFatePoint").SetValue(t.Field("_usedFatePoint").GetValue<int>() + cost);
            t.Field("_currentAdd").SetValue(newValue - def);
            Plugin.Log.LogInfo($"[天命] 属性 {stat.StatType} {oldValue}->{newValue} 費用{cost} 剩餘{fateStat.FinalValue}");
            t.Method("UpdateReviewData").GetValue();
            object modifyEvent = t.Field("_modifyEvent").GetValue<object>();
            if (modifyEvent != null) Traverse.Create(modifyEvent).Method("Raise").GetValue();
        }

        internal static void ReviewProperty(FateBonusProperty item)
        {
            Traverse t = Traverse.Create(item);
            Mortal.Core.GameStat stat = t.Field("_stat").GetValue<Mortal.Core.GameStat>();
            int def = t.Field("_default").GetValue<int>();
            int currentAdd = t.Field("_currentAdd").GetValue<int>();
            Text valueText = t.Field("_valueText").GetValue<Text>();
            Image barImage = t.Field("_barImage").GetValue<Image>();
            Color defaultColor = t.Field("_defaultColor").GetValue<Color>();
            Color modifyColor = t.Field("_modifyColor").GetValue<Color>();
            bool uncapped = Plugin.IsUncapped(stat);

            int value = def + currentAdd;
            if (!uncapped)
            {
                value = Mathf.Clamp(value, 0, stat.Max);
            }
            switch (stat.PropertyType)
            {
                case Mortal.Core.GameStatPropertyType.一般:
                case Mortal.Core.GameStatPropertyType.無:
                    valueText.text = value.ToString();
                    break;
                case Mortal.Core.GameStatPropertyType.陰陽:
                case Mortal.Core.GameStatPropertyType.善惡:
                {
                    int level = Mortal.Core.GameStatUtils.GetGameStatLevel(value, stat.Max, stat.LevelLength);
                    string key = stat.LevelText[level];
                    valueText.text = LocalizationManager.Instance.LocaleResolver.GetString("StatLevel/" + key);
                    break;
                }
            }
            // 颜色:突破原设计上限=金色(仅可破限类型);与初始不同=蓝色(改动色);否则默认色
            if (uncapped && value > stat.Max)
            {
                valueText.color = Plugin.FateOverCapColor;
            }
            else if (value != def)
            {
                valueText.color = modifyColor;
            }
            else
            {
                valueText.color = defaultColor;
            }
            barImage.fillAmount = Mathf.Clamp01((float)value / (float)stat.Max);
            UpdatePropertyButton(item);
        }

        internal static void UpdatePropertyButton(FateBonusProperty item)
        {
            Traverse t = Traverse.Create(item);
            Mortal.Core.GameStat stat = t.Field("_stat").GetValue<Mortal.Core.GameStat>();
            Mortal.Core.GameStat fateStat = t.Field("_fateStat").GetValue<Mortal.Core.GameStat>();
            int def = t.Field("_default").GetValue<int>();
            int currentAdd = t.Field("_currentAdd").GetValue<int>();
            Button addButton = t.Field("_addButton").GetValue<Button>();
            Button minusButton = t.Field("_minusButton").GetValue<Button>();
            bool disableMinus = t.Field("_disableMinus").GetValue<bool>();
            if (addButton == null || minusButton == null) return;
            bool uncapped = Plugin.IsUncapped(stat);
            int value = def + currentAdd;
            addButton.enabled = fateStat != null && fateStat.FinalValue > 0 && (uncapped || value < stat.Max);
            if (disableMinus)
            {
                // 原本没有减号的项(六维等):只在有已分配点数时出现
                minusButton.gameObject.SetActive(currentAdd != 0);
            }
            else
            {
                minusButton.enabled = currentAdd != 0;
            }
        }

        // ----------------------------------------------------------------
        // 人际项 FateBonusSocial(保持原版 100 上限,只改操作逻辑)
        // ----------------------------------------------------------------

        internal static void SocialClick(FateBonusSocial item, int direction)
        {
            Traverse t = Traverse.Create(item);
            Mortal.Core.RelationshipStat stat = t.Field("_stat").GetValue<Mortal.Core.RelationshipStat>();
            Mortal.Core.GameStat fateStat = t.Field("_fateStat").GetValue<Mortal.Core.GameStat>();
            int addPoint = t.Field("_addPoint").GetValue<int>();
            int max = t.Field("_max").GetValue<int>();
            int def = t.Field("_default").GetValue<int>();
            int currentAdd = t.Field("_currentAdd").GetValue<int>();
            if (stat == null || fateStat == null || addPoint == 0) return;

            int oldValue = def + currentAdd;
            int newValue = Mathf.Clamp(oldValue + direction * addPoint, def, max);
            if (newValue == oldValue) return;
            int steps = (newValue - oldValue) / addPoint; // 加=+n 扣费,减=-n 退费
            if (steps > 0 && fateStat.FinalValue < steps) return;

            stat.SetValue(newValue);
            fateStat.AddValue(-steps);
            t.Field("_usedFatePoint").SetValue(t.Field("_usedFatePoint").GetValue<int>() + steps);
            t.Field("_currentAdd").SetValue(newValue - def);
            Plugin.Log.LogInfo($"[天命] 人際 {stat.Type} {oldValue}->{newValue} 費用{steps} 剩餘{fateStat.FinalValue}");
            t.Method("UpdateReviewData").GetValue();
            object modifyEvent = t.Field("_modifyEvent").GetValue<object>();
            if (modifyEvent != null) Traverse.Create(modifyEvent).Method("Raise").GetValue();
        }

        internal static void ReviewSocial(FateBonusSocial item)
        {
            Traverse t = Traverse.Create(item);
            int max = t.Field("_max").GetValue<int>();
            int def = t.Field("_default").GetValue<int>();
            int currentAdd = t.Field("_currentAdd").GetValue<int>();
            Text valueText = t.Field("_valueText").GetValue<Text>();
            Image barImage = t.Field("_barImage").GetValue<Image>();
            Color defaultColor = t.Field("_defaultColor").GetValue<Color>();
            Color modifyColor = t.Field("_modifyColor").GetValue<Color>();

            int value = Mathf.Clamp(def + currentAdd, 0, max);
            valueText.text = value.ToString();
            valueText.color = value != def ? modifyColor : defaultColor;
            barImage.fillAmount = Mathf.Clamp01((float)value / (float)max);
            UpdateSocialButton(item);
        }

        internal static void UpdateSocialButton(FateBonusSocial item)
        {
            Traverse t = Traverse.Create(item);
            Mortal.Core.GameStat fateStat = t.Field("_fateStat").GetValue<Mortal.Core.GameStat>();
            int max = t.Field("_max").GetValue<int>();
            int def = t.Field("_default").GetValue<int>();
            int currentAdd = t.Field("_currentAdd").GetValue<int>();
            Button addButton = t.Field("_addButton").GetValue<Button>();
            if (addButton == null) return;
            addButton.enabled = fateStat != null && fateStat.FinalValue > 0 && def + currentAdd < max;
            Button minus = FindMinus(addButton, SocialMinusName);
            if (minus != null)
            {
                minus.gameObject.SetActive(currentAdd != 0);
            }
        }

        // ----------------------------------------------------------------
        // 门派项 FateBonusFlag(保持原版 _max 上限,只改操作逻辑)
        // ----------------------------------------------------------------

        internal static void FlagClick(FateBonusFlag item, int direction)
        {
            Traverse t = Traverse.Create(item);
            Mortal.Core.FlagData stat = t.Field("_stat").GetValue<Mortal.Core.FlagData>();
            Mortal.Core.GameStat fateStat = t.Field("_fateStat").GetValue<Mortal.Core.GameStat>();
            int addPoint = t.Field("_addPoint").GetValue<int>();
            int max = t.Field("_max").GetValue<int>();
            int def = t.Field("_default").GetValue<int>();
            int currentAdd = t.Field("_currentAdd").GetValue<int>();
            if (stat == null || fateStat == null || addPoint == 0) return;

            int oldValue = def + currentAdd;
            int newValue = Mathf.Clamp(oldValue + direction * addPoint, def, max);
            if (newValue == oldValue) return;
            int steps = (newValue - oldValue) / addPoint;
            if (steps > 0 && fateStat.FinalValue < steps) return;

            stat.State = newValue;
            fateStat.AddValue(-steps);
            t.Field("_usedFatePoint").SetValue(t.Field("_usedFatePoint").GetValue<int>() + steps);
            t.Field("_currentAdd").SetValue(newValue - def);
            Plugin.Log.LogInfo($"[天命] 門派 {stat.name} {oldValue}->{newValue} 費用{steps} 剩餘{fateStat.FinalValue}");
            t.Method("UpdateReviewData").GetValue();
            object modifyEvent = t.Field("_modifyEvent").GetValue<object>();
            if (modifyEvent != null) Traverse.Create(modifyEvent).Method("Raise").GetValue();
        }

        internal static void ReviewFlag(FateBonusFlag item)
        {
            Traverse t = Traverse.Create(item);
            int max = t.Field("_max").GetValue<int>();
            int def = t.Field("_default").GetValue<int>();
            int currentAdd = t.Field("_currentAdd").GetValue<int>();
            Text valueText = t.Field("_valueText").GetValue<Text>();
            Image barImage = t.Field("_barImage").GetValue<Image>();
            Color defaultColor = t.Field("_defaultColor").GetValue<Color>();
            Color modifyColor = t.Field("_modifyColor").GetValue<Color>();

            int value = Mathf.Clamp(def + currentAdd, 0, max);
            valueText.text = value.ToString();
            valueText.color = value != def ? modifyColor : defaultColor;
            barImage.fillAmount = Mathf.Clamp01((float)value / (float)max);
            UpdateFlagButton(item);
        }

        internal static void UpdateFlagButton(FateBonusFlag item)
        {
            Traverse t = Traverse.Create(item);
            Mortal.Core.GameStat fateStat = t.Field("_fateStat").GetValue<Mortal.Core.GameStat>();
            int max = t.Field("_max").GetValue<int>();
            int def = t.Field("_default").GetValue<int>();
            int currentAdd = t.Field("_currentAdd").GetValue<int>();
            Button addButton = t.Field("_addButton").GetValue<Button>();
            if (addButton == null) return;
            addButton.enabled = fateStat != null && fateStat.FinalValue > 0 && def + currentAdd < max;
            Button minus = FindMinus(addButton, FlagMinusName);
            if (minus != null)
            {
                minus.gameObject.SetActive(currentAdd != 0);
            }
        }

        // ----------------------------------------------------------------
        // 面板 FateBonusPanel
        // ----------------------------------------------------------------

        // 结束加点:剩余点数>0 且未勾选"不再提示" → 弹窗;否则直接放行进入下一环节
        internal static void TryClosePanel(FateBonusPanel panel)
        {
            Traverse t = Traverse.Create(panel);
            int remaining = GetRemainingFate(panel);
            if (remaining > 0 && !Plugin.FateSkipLeftoverWarning.Value)
            {
                FateLeftoverPopup.Show(panel, remaining);
                return;
            }
            t.Field("_pressCancel").SetValue(true);
        }

        internal static int GetRemainingFate(FateBonusPanel panel)
        {
            Traverse t = Traverse.Create(panel);
            List<FateBonusProperty> props = t.Field("_propertyList").GetValue<List<FateBonusProperty>>();
            if (props != null && props.Count > 0)
            {
                Mortal.Core.GameStat fateStat = Traverse.Create(props[0]).Field("_fateStat").GetValue<Mortal.Core.GameStat>();
                if (fateStat != null) return fateStat.FinalValue;
            }
            return 0;
        }

        // 复原=三页全部回到打开面板时的状态,点数全退
        internal static void ResetAll(FateBonusPanel panel)
        {
            Traverse t = Traverse.Create(panel);
            foreach (FateBonusProperty p in t.Field("_propertyList").GetValue<List<FateBonusProperty>>())
            {
                p.ResetToDefault();
            }
            foreach (FateBonusSocial s in t.Field("_socialList").GetValue<List<FateBonusSocial>>())
            {
                s.ResetToDefault();
            }
            foreach (FateBonusFlag f in t.Field("_flagList").GetValue<List<FateBonusFlag>>())
            {
                f.ResetToDefault();
            }
            panel.UpdateAllModifyButton();
            Plugin.Log.LogInfo("[天命] 復原:全部頁面回到打開時狀態,點數已全退,剩餘=" + GetRemainingFate(panel));
        }

        // 六个页签按钮(確定/復原 × 3)强制常显
        internal static void ForceMenuButtons(FateBonusPanel panel)
        {
            Traverse t = Traverse.Create(panel);
            string[] fields =
            {
                "_propertySaveButton", "_propertyResetButton",
                "_socialSaveButton", "_socialResetButton",
                "_flagSaveButton", "_flagResetButton"
            };
            foreach (string field in fields)
            {
                Mortal.Core.MenuToggleButton button = t.Field(field).GetValue<Mortal.Core.MenuToggleButton>();
                if (button != null)
                {
                    t.Method("EnableMenuButton", new object[] { button, true }).GetValue();
                }
            }
        }

        // 目标页签的内容列表是否已构建(0=属性 1=人际 2=门派)
        internal static bool PanelListReady(Traverse t, int index)
        {
            string field;
            switch (index)
            {
                case 0: field = "_propertyList"; break;
                case 1: field = "_socialList"; break;
                case 2: field = "_flagList"; break;
                default: return false;
            }
            System.Collections.ICollection list = t.Field(field).GetValue<System.Collections.ICollection>();
            return list != null && list.Count > 0;
        }
    }

    // ============================ Harmony 补丁 ============================

    [HarmonyPatch(typeof(FateBonusProperty), "Start")]
    internal static class FatePropertyStartPatch
    {
        private static void Postfix(FateBonusProperty __instance)
        {
            if (!FateTweak.On) return;
            Traverse t = Traverse.Create(__instance);
            FateTweak.WireRepeat(t.Field("_addButton").GetValue<Button>(), __instance.AddPoint);
            FateTweak.WireRepeat(t.Field("_minusButton").GetValue<Button>(), __instance.MinusPoint);
            __instance.UpdateButton();
        }
    }

    [HarmonyPatch(typeof(FateBonusProperty), nameof(FateBonusProperty.AddPoint))]
    internal static class FatePropertyAddPatch
    {
        private static bool Prefix(FateBonusProperty __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.PropertyClick(__instance, 1);
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusProperty), nameof(FateBonusProperty.MinusPoint))]
    internal static class FatePropertyMinusPatch
    {
        private static bool Prefix(FateBonusProperty __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.PropertyClick(__instance, -1);
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusProperty), "UpdateReviewData")]
    internal static class FatePropertyReviewPatch
    {
        private static bool Prefix(FateBonusProperty __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.ReviewProperty(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusProperty), nameof(FateBonusProperty.UpdateButton))]
    internal static class FatePropertyButtonPatch
    {
        private static bool Prefix(FateBonusProperty __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.UpdatePropertyButton(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusSocial), nameof(FateBonusSocial.Setup))]
    internal static class FateSocialSetupPatch
    {
        private static void Postfix(FateBonusSocial __instance)
        {
            if (!FateTweak.On) return;
            Traverse t = Traverse.Create(__instance);
            Button add = t.Field("_addButton").GetValue<Button>();
            FateTweak.WireRepeat(add, __instance.AddPoint);
            FateTweak.EnsureMinusButton(add, __instance.transform, __instance.MinusPoint, "FateMinusBtn");
            FateTweak.UpdateSocialButton(__instance);
        }
    }

    [HarmonyPatch(typeof(FateBonusSocial), nameof(FateBonusSocial.AddPoint))]
    internal static class FateSocialAddPatch
    {
        private static bool Prefix(FateBonusSocial __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.SocialClick(__instance, 1);
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusSocial), nameof(FateBonusSocial.MinusPoint))]
    internal static class FateSocialMinusPatch
    {
        private static bool Prefix(FateBonusSocial __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.SocialClick(__instance, -1);
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusSocial), "UpdateReviewData")]
    internal static class FateSocialReviewPatch
    {
        private static bool Prefix(FateBonusSocial __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.ReviewSocial(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusSocial), nameof(FateBonusSocial.UpdateButton))]
    internal static class FateSocialButtonPatch
    {
        private static bool Prefix(FateBonusSocial __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.UpdateSocialButton(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusFlag), nameof(FateBonusFlag.Setup))]
    internal static class FateFlagSetupPatch
    {
        private static void Postfix(FateBonusFlag __instance)
        {
            if (!FateTweak.On) return;
            Traverse t = Traverse.Create(__instance);
            Button add = t.Field("_addButton").GetValue<Button>();
            FateTweak.WireRepeat(add, __instance.AddPoint);
            FateTweak.EnsureMinusButton(add, __instance.transform, __instance.MinusPoint, "FateMinusBtn");
            FateTweak.UpdateFlagButton(__instance);
        }
    }

    [HarmonyPatch(typeof(FateBonusFlag), nameof(FateBonusFlag.AddPoint))]
    internal static class FateFlagAddPatch
    {
        private static bool Prefix(FateBonusFlag __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.FlagClick(__instance, 1);
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusFlag), nameof(FateBonusFlag.MinusPoint))]
    internal static class FateFlagMinusPatch
    {
        private static bool Prefix(FateBonusFlag __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.FlagClick(__instance, -1);
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusFlag), "UpdateReviewData")]
    internal static class FateFlagReviewPatch
    {
        private static bool Prefix(FateBonusFlag __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.ReviewFlag(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusFlag), nameof(FateBonusFlag.UpdateButton))]
    internal static class FateFlagButtonPatch
    {
        private static bool Prefix(FateBonusFlag __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.UpdateFlagButton(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusPanel), nameof(FateBonusPanel.ChangePanel))]
    internal static class FatePanelChangePatch
    {
        private static bool Prefix(FateBonusPanel __instance, int index)
        {
            if (!FateTweak.On) return true;
            // 三页自由切换:去掉"本页有未确认点数"的拦截
            try
            {
                Traverse t = Traverse.Create(__instance);
                // 面板刚激活(ToggleGroup.OnEnable 阶段)时列表可能还没构建,
                // 此时 ShowPanel 会经事件走到 SetPropertySelected 对空表取 [0] 越界;
                // 列表没好就只记页签,显示交给游戏自己的初始化流程
                if (FateTweak.PanelListReady(t, index))
                {
                    t.Method("ShowPanel", index).GetValue();
                    t.Method("ToggleMenuButton", index).GetValue();
                }
                t.Field("_currentTab").SetValue(index);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[天命] 页签切换异常:" + e.Message);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusPanel), nameof(FateBonusPanel.OnPanelOpen))]
    internal static class FatePanelOpenPatch
    {
        private static void Postfix(FateBonusPanel __instance)
        {
            if (!FateTweak.On) return;
            FateTweak.ForceMenuButtons(__instance);
            FateLeftoverPopup.EnsureCreated(__instance);
            FateRecon.DumpOnce(__instance);
        }
    }

    [HarmonyPatch(typeof(FateBonusPanel), nameof(FateBonusPanel.SaveProperty))]
    internal static class FatePanelSavePropertyPatch
    {
        private static bool Prefix(FateBonusPanel __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.TryClosePanel(__instance); // 確定 = 结束加点
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusPanel), nameof(FateBonusPanel.SaveSocial))]
    internal static class FatePanelSaveSocialPatch
    {
        private static bool Prefix(FateBonusPanel __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.TryClosePanel(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusPanel), nameof(FateBonusPanel.SaveFlag))]
    internal static class FatePanelSaveFlagPatch
    {
        private static bool Prefix(FateBonusPanel __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.TryClosePanel(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusPanel), nameof(FateBonusPanel.ResetProperty))]
    internal static class FatePanelResetPropertyPatch
    {
        private static bool Prefix(FateBonusPanel __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.ResetAll(__instance); // 復原 = 回到最初(三页全退)
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusPanel), nameof(FateBonusPanel.ResetSocial))]
    internal static class FatePanelResetSocialPatch
    {
        private static bool Prefix(FateBonusPanel __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.ResetAll(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusPanel), nameof(FateBonusPanel.ResetFlag))]
    internal static class FatePanelResetFlagPatch
    {
        private static bool Prefix(FateBonusPanel __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.ResetAll(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusPanel), nameof(FateBonusPanel.PressCancel))]
    internal static class FatePanelCancelPatch
    {
        private static bool Prefix(FateBonusPanel __instance)
        {
            if (!FateTweak.On) return true;
            FateTweak.TryClosePanel(__instance); // 右上角 X:剩余点数提示后直接放行
            return false;
        }
    }

    [HarmonyPatch(typeof(FateBonusPanel), nameof(FateBonusPanel.OpenConfirmPanel))]
    internal static class FatePanelConfirmSuppressPatch
    {
        private static bool Prefix()
        {
            // FateTweak 开启时:原版"未确认点数"弹窗永不出现
            return !FateTweak.On;
        }
    }
}
