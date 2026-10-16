using HarmonyLib;
using Mortal.Core;
using UnityEngine;
using UnityEngine.UI;

namespace UncapSixStats
{
    /// <summary>
    /// 更好的天命点分配(v2.1 重写,适配 2026-10-15 游戏更新)。
    ///
    /// 官方这版重写了继承系统:FateBonusProperty/Social/Flag 统一继承新基类 FateBonusItem,
    /// 三页签自由切换、减号退费(三种规则)、剩余点数确认窗均已官方实现,mod 不再接管这些逻辑
    /// (旧版的立即落实/复原/确定改造、自制减号按钮、剩余点数弹窗已全部废弃)。
    ///
    /// mod 现在只补官方没有的功能:
    /// 1) 破限加点:破限名单内的属性项(默认 銀兩3/鍛造17/毒藥(煉丹)18/武學點數31,
    ///    以及总开关下的六维/武功类)在继承界面可点过原上限并显示为金色;
    ///    落实路径 ConfirmStart → ModifyExecute → ApplyValue → GameStat.AddValue
    ///    由 GameStat 解限补丁接管,写入/读档全程不钳回 Max。
    /// 2) 长按连点:三类条目的加/减按钮都接 RepeatPress(官方仍是普通 Button)。
    /// 3) 可选:跳过"開始遊戲"确认窗(配置 FateTweak.SkipStartConfirm,默认关,尊重官方流程)。
    /// </summary>
    internal static class FateTweak
    {
        internal static bool On => Plugin.FateTweakSwitch != null && Plugin.FateTweakSwitch.Value;

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

        // 属性项的 GameStat;非属性项(人际/门派)返回 null
        internal static Mortal.Core.GameStat GetPropertyStat(FateBonusItem item)
        {
            FateBonusProperty property = item as FateBonusProperty;
            if (property == null) return null;
            return Traverse.Create(property).Field("_stat").GetValue<Mortal.Core.GameStat>();
        }

        private static bool IsUncappedProperty(FateBonusItem item, out Mortal.Core.GameStat stat)
        {
            stat = GetPropertyStat(item);
            return stat != null && Plugin.IsUncapped(stat);
        }

        /// <summary>
        /// 复刻新版 FateBonusItem.CanStep,唯一差别:破限属性项的 Max 视为 int.MaxValue。
        /// 返回 false 表示已接管(__result 有效);true 表示放行原版。
        /// </summary>
        internal static bool CanStepPrefix(FateBonusItem item, int direction, out int currentAdd, out int usedFatePoint, ref bool result)
        {
            currentAdd = 0;
            usedFatePoint = 0;
            if (!On) return true;
            if (!IsUncappedProperty(item, out Mortal.Core.GameStat stat)) return true;

            Traverse t = Traverse.Create(item);
            int addPoint = t.Field("_addPoint").GetValue<int>();
            if (addPoint == 0)
            {
                result = false;
                return false;
            }
            FateBonusRule rule = t.Field("_rule").GetValue<FateBonusRule>();
            int minLimit = t.Property("Min").GetValue<int>();
            int def = t.Field("_default").GetValue<int>();
            int current = t.Field("_currentAdd").GetValue<int>();
            int used = t.Field("_usedFatePoint").GetValue<int>();
            int fateCost = t.Field("_fateCost").GetValue<int>();
            Mortal.Core.GameStat fateStat = t.Field("_fateStat").GetValue<Mortal.Core.GameStat>();

            int min = rule == FateBonusRule.OnlyIncrease ? Mathf.Max(minLimit, def) : minLimit;
            int num = Mathf.Clamp(def + current + direction * addPoint, min, int.MaxValue);
            currentAdd = num - def;
            if (rule == FateBonusRule.PayEveryStep)
            {
                usedFatePoint = used + fateCost;
            }
            else
            {
                int steps = Mathf.CeilToInt((float)Mathf.Abs(currentAdd) / (float)addPoint);
                usedFatePoint = steps * fateCost;
            }
            if (currentAdd == current || (currentAdd - current) * direction < 0)
            {
                result = false;
                return false;
            }
            result = fateStat != null && usedFatePoint - used <= fateStat.FinalValue;
            return false;
        }

        /// <summary>
        /// 复刻新版 FateBonusItem.UpdateReviewData:破限项显示真实预览值(不钳 Max),
        /// 超上限染金色;进度条保持原版(钳到 Max,顶满为止,不变形)。
        /// </summary>
        internal static bool UpdateReviewDataPrefix(FateBonusItem item)
        {
            if (!On) return true;
            if (!IsUncappedProperty(item, out Mortal.Core.GameStat stat)) return true;

            Traverse t = Traverse.Create(item);
            int def = t.Field("_default").GetValue<int>();
            int current = t.Field("_currentAdd").GetValue<int>();
            int max = stat.Max;
            int value = Mathf.Max(def + current, stat.Min);

            Text valueText = t.Field("_valueText").GetValue<Text>();
            Image barImage = t.Field("_barImage").GetValue<Image>();
            Color defaultColor = t.Field("_defaultColor").GetValue<Color>();
            Color modifyColor = t.Field("_modifyColor").GetValue<Color>();

            valueText.text = t.Method("GetValueText", new object[] { value }).GetValue<string>();
            valueText.color = value > max ? Plugin.FateOverCapColor : (value != def ? modifyColor : defaultColor);
            if (barImage != null && max > 0)
            {
                barImage.fillAmount = Mathf.Clamp01((float)value / (float)max);
            }
            item.UpdateButton();
            return false;
        }

        internal static void WireButtons(FateBonusItem item)
        {
            if (!On) return;
            Traverse t = Traverse.Create(item);
            WireRepeat(t.Field("_addButton").GetValue<Button>(), item.AddPoint);
            WireRepeat(t.Field("_minusButton").GetValue<Button>(), item.MinusPoint);
        }
    }

    // ============================ Harmony 补丁 ============================

    /// <summary>破限加点核心:接管基类 CanStep(三类条目共用),破限属性项的 Max 视为无穷。</summary>
    [HarmonyPatch(typeof(FateBonusItem), "CanStep")]
    internal static class FateItemCanStepPatch
    {
        private static bool Prefix(FateBonusItem __instance, int direction, out int currentAdd, out int usedFatePoint, ref bool __result)
        {
            try
            {
                return FateTweak.CanStepPrefix(__instance, direction, out currentAdd, out usedFatePoint, ref __result);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[天命] CanStep 接管异常,放行原版:" + e.Message);
                currentAdd = 0;
                usedFatePoint = 0;
                __result = false;
                return true;
            }
        }
    }

    /// <summary>破限项的预览显示:真实值 + 金色;进度条不变。</summary>
    [HarmonyPatch(typeof(FateBonusItem), "UpdateReviewData")]
    internal static class FateItemReviewPatch
    {
        private static bool Prefix(FateBonusItem __instance)
        {
            try
            {
                return FateTweak.UpdateReviewDataPrefix(__instance);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[天命] UpdateReviewData 接管异常,放行原版:" + e.Message);
                return true;
            }
        }
    }

    /// <summary>审计日志:真实加点/减点(Step 成功执行)才记录,CanStep 纯检查不记。</summary>
    [HarmonyPatch(typeof(FateBonusItem), "Step")]
    internal static class FateItemStepPatch
    {
        private static void Postfix(FateBonusItem __instance, int direction)
        {
            try
            {
                Mortal.Core.GameStat stat = FateTweak.GetPropertyStat(__instance);
                if (stat == null || !Plugin.IsUncapped(stat)) return;
                Traverse t = Traverse.Create(__instance);
                int def = t.Field("_default").GetValue<int>();
                int current = t.Field("_currentAdd").GetValue<int>();
                Mortal.Core.GameStat fateStat = t.Field("_fateStat").GetValue<Mortal.Core.GameStat>();
                Plugin.Log.LogInfo($"[天命] {stat.StatType} {(direction > 0 ? "+" : "-")}步 → 預覽 {def + current} 剩餘天命 {fateStat?.FinalValue}");
            }
            catch { }
        }
    }

    /// <summary>长按连点:三类条目 Setup 后给加/减按钮接 RepeatPress。</summary>
    [HarmonyPatch(typeof(FateBonusProperty), nameof(FateBonusProperty.Setup))]
    internal static class FatePropertySetupPatch
    {
        private static void Postfix(FateBonusProperty __instance)
        {
            FateTweak.WireButtons(__instance);
        }
    }

    [HarmonyPatch(typeof(FateBonusSocial), nameof(FateBonusSocial.Setup))]
    internal static class FateSocialSetupPatch
    {
        private static void Postfix(FateBonusSocial __instance)
        {
            FateTweak.WireButtons(__instance);
        }
    }

    [HarmonyPatch(typeof(FateBonusFlag), nameof(FateBonusFlag.Setup))]
    internal static class FateFlagSetupPatch
    {
        private static void Postfix(FateBonusFlag __instance)
        {
            FateTweak.WireButtons(__instance);
        }
    }

    /// <summary>可选:跳过"開始遊戲"确认窗,点開始遊戲直接落实(默认关,尊重官方确认流程)。</summary>
    [HarmonyPatch(typeof(FateBonusPanel), nameof(FateBonusPanel.OpenStartConfirm))]
    internal static class FatePanelSkipConfirmPatch
    {
        private static bool Prefix(FateBonusPanel __instance)
        {
            if (!FateTweak.On || !Plugin.FateSkipStartConfirm.Value) return true;
            __instance.ConfirmStart();
            return false;
        }
    }
}
