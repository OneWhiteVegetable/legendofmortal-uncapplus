using System.Collections;
using HarmonyLib;
using Mortal.Combat;
using Mortal.Core;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UI.Extensions;

namespace UncapSixStats
{
    /// <summary>
    /// 0a. 帧驱动源1:EventSystem.Update —— 游戏 UI 每帧都跑,必定执行。
    /// 本游戏里 BepInEx 插件宿主物体的 Update/OnGUI 不执行,借游戏自身组件的 Update 驱动。
    /// </summary>
    [HarmonyPatch(typeof(UnityEngine.EventSystems.EventSystem), "Update")]
    internal static class EventSystemTickPatch
    {
        private static void Postfix()
        {
            if (Plugin.Instance != null)
            {
                Plugin.Instance.OnGameTick();
            }
        }
    }

    /// <summary>
    /// 0b. 帧驱动源2:Mortal.Core.dll 里游戏自己的 SteamManager(全局命名空间,常驻单例)。
    /// 与 0a 互为冗余,同一帧内由 OnGameTick 按 Time.frameCount 去重。
    /// </summary>
    [HarmonyPatch(typeof(SteamManager), "Update")]
    internal static class SteamManagerTickPatch
    {
        private static void Postfix()
        {
            if (Plugin.Instance != null)
            {
                Plugin.Instance.OnGameTick();
            }
        }
    }

    /// <summary>
    /// 1. 练功/事件加点入口:名单内属性不再钳到 _max,保留下界、累加器和变更事件。
    /// </summary>
    [HarmonyPatch(typeof(GameStat), nameof(GameStat.AddValue))]
    internal static class GameStatAddValuePatch
    {
        private static bool Prefix(GameStat __instance, int value, bool totalAdd)
        {
            if (!Plugin.IsUncapped(__instance)) return true;
            Traverse traverse = Traverse.Create(__instance);
            int oldValue = traverse.Field("_value").GetValue<int>();
            int newValue = oldValue + value;
            if (newValue < __instance.Min)
            {
                newValue = __instance.Min;
            }
            traverse.Field("_value").SetValue(newValue);
            if (totalAdd && value > 0)
            {
                GameStat totalAddData = traverse.Field("_totalAddData").GetValue<GameStat>();
                if (totalAddData != null)
                {
                    totalAddData.AddValue(value);
                }
            }
            traverse.Method("RaiseValueChangedEvent", oldValue, newValue).GetValue();
            return false;
        }

        private static void Postfix()
        {
            MusclePortraits.NotifyStatChanged();
        }
    }

    /// <summary>
    /// 2. 直接赋值入口(读档也走这里):名单内属性不被钳回 100。
    ///    postfix 顺带作为"读档完成"信号,首次触发时创建 Mod 面板。
    /// </summary>
    [HarmonyPatch(typeof(GameStat), nameof(GameStat.SetValue))]
    internal static class GameStatSetValuePatch
    {
        private static bool Prefix(GameStat __instance, int value)
        {
            if (!Plugin.IsUncapped(__instance)) return true;
            Traverse traverse = Traverse.Create(__instance);
            int oldValue = traverse.Field("_value").GetValue<int>();
            int newValue = value < __instance.Min ? __instance.Min : value;
            traverse.Field("_value").SetValue(newValue);
            traverse.Method("RaiseValueChangedEvent", oldValue, newValue).GetValue();
            return false;
        }

        private static void Postfix()
        {
            ModPanelUI.TryCreate();
            MusclePortraits.NotifyStatChanged();
        }
    }

    /// <summary>
    /// 2b. 新游戏开局走 Reset:同样作为面板创建信号(一次性守卫在 TryCreate 内)。
    /// </summary>
    [HarmonyPatch(typeof(GameStat), nameof(GameStat.Reset))]
    internal static class GameStatResetPatch
    {
        private static void Postfix()
        {
            ModPanelUI.TryCreate();
        }
    }

    /// <summary>
    /// 3. FinalValue 读取:名单内属性去掉上界(保留加成、保留下界)。
    /// </summary>
    [HarmonyPatch(typeof(GameStat), nameof(GameStat.FinalValue), MethodType.Getter)]
    internal static class GameStatFinalValuePatch
    {
        private static void Postfix(GameStat __instance, ref int __result)
        {
            if (!Plugin.IsUncapped(__instance)) return;
            int baseValue = Traverse.Create(__instance).Field("_value").GetValue<int>();
            __result = System.Math.Max(baseValue + __instance.AdditionValue, __instance.Min);
        }
    }

    /// <summary>
    /// 4. 等级文字防御钳制:数值超过原上限后仍显示最高级文字,不越界、不空白。
    /// </summary>
    [HarmonyPatch(typeof(GameStatUtils), nameof(GameStatUtils.GetGameStatLevel))]
    internal static class GameStatLevelPatch
    {
        private static void Postfix(ref int __result, int levelLength)
        {
            if (levelLength <= 0) return;
            if (__result >= levelLength)
            {
                __result = levelLength - 1;
            }
            else if (__result < 0)
            {
                __result = 0;
            }
        }
    }

    /// <summary>
    /// 5. 主属性面板雷达图:原六边形只画到 100%(保持原版白色实心),
    /// 超出部分由金色破限层顶出外框(对数压缩,不霸屏)。
    /// </summary>
    [HarmonyPatch(typeof(RadarChartPanel), nameof(RadarChartPanel.UpdateStatus))]
    internal static class RadarChartPanelPatch
    {
        private static bool Prefix(RadarChartPanel __instance)
        {
            if (!Plugin.Enabled.Value)
            {
                // 总开关关闭:把已存在的破限层藏起来(只查不建),其余完全走原版
                UIPolygon poly0 = Traverse.Create(__instance).Field("_uiPolygon").GetValue<UIPolygon>();
                RadarOverlay.SetOverCap(RadarOverlay.FindAll(poly0), false);
                return true;
            }
            Traverse traverse = Traverse.Create(__instance);
            GameStat[] stats = traverse.Field("_stats").GetValue<GameStat[]>();
            UIPolygon polygon = traverse.Field("_uiPolygon").GetValue<UIPolygon>();
            if (stats == null || polygon == null) return true;
            RadarOverlayPulse[] pulses = RadarOverlay.EnsureAll(polygon);
            bool anyOverCap = false;
            int count = Mathf.Min(stats.Length, polygon.VerticesDistances.Length);
            for (int i = 0; i < count; i++)
            {
                GameStat stat = stats[i];
                float raw = (float)stat.FinalValue / stat.Max;
                float soft = Plugin.SoftMapRadar(raw);
                if (raw > 1f)
                {
                    anyOverCap = true;
                }
                polygon.VerticesDistances[i] = Mathf.Clamp(soft, 0.02f, 1f);
                RadarOverlay.SetBase(pulses, i, soft);
            }
            RadarOverlay.SetOverCap(pulses, anyOverCap);
            IEnumerator routine = traverse.Method("DelayDraw").GetValue<IEnumerator>();
            __instance.StartCoroutine(routine);
            return false;
        }
    }

    /// <summary>
    /// 6. 战斗面板雷达图:原版 Clamp01 削平,改为同样的双层结构(白层贴框 + 金色破限层)。
    /// </summary>
    [HarmonyPatch(typeof(CombatCharacterStatusUI), "SetRadarStat")]
    internal static class CombatRadarPatch
    {
        private static void Postfix(CombatCharacterStatusUI __instance, CombatStatItem statItem, int max, int index)
        {
            UIPolygon polygon = Traverse.Create(__instance).Field("_statRadar").GetValue<UIPolygon>();
            if (!Plugin.Enabled.Value)
            {
                // 总开关关闭:把已存在的破限层藏起来(只查不建)
                RadarOverlay.SetOverCap(RadarOverlay.FindAll(polygon), false);
                return;
            }
            if (polygon == null) return;
            if (index < 0 || index >= polygon.VerticesDistances.Length) return;
            RadarOverlayPulse[] pulses = RadarOverlay.EnsureAll(polygon);
            float raw = (float)statItem.FinalValue / max;
            float soft = Plugin.SoftMapRadar(raw);
            polygon.VerticesDistances[index] = Mathf.Clamp(soft, 0.02f, 1f);
            // UpdateRadarStat 按 index 0→5 顺序刷新,0 时重置累计标记
            if (index == 0)
            {
                RadarOverlay.SetOverCap(pulses, false);
            }
            RadarOverlay.SetBase(pulses, index, soft);
            if (raw > 1f)
            {
                RadarOverlay.SetOverCap(pulses, true);
            }
        }
    }

    /// <summary>
    /// 7a. 命运点加点执行:名单内属性去掉 "Max-初始值" 的预钳制,可以点过 100。
    ///     FateTweak 开启后由 FateTweak 接管(立即落实,没有 Save 环节),这里直接 no-op,
    ///     防止原版 ModifyExecute 把破限值钳回 Max。
    /// </summary>
    [HarmonyPatch(typeof(FateBonusProperty), nameof(FateBonusProperty.ModifyExecute))]
    internal static class FateModifyExecutePatch
    {
        private static bool Prefix(FateBonusProperty __instance)
        {
            if (Plugin.FateTweakSwitch.Value) return false;
            Traverse traverse = Traverse.Create(__instance);
            GameStat stat = traverse.Field("_stat").GetValue<GameStat>();
            if (!Plugin.IsUncapped(stat)) return true;
            int currentAdd = traverse.Field("_currentAdd").GetValue<int>();
            if (currentAdd != 0)
            {
                stat.AddValue(currentAdd);
            }
            traverse.Field("_usedFatePoint").SetValue(0);
            traverse.Field("_currentAdd").SetValue(0);
            Text valueText = traverse.Field("_valueText").GetValue<Text>();
            valueText.text = stat.FinalValue.ToString();
            valueText.color = traverse.Field("_defaultColor").GetValue<Color>();
            traverse.Field("_default").SetValue(stat.FinalValue);
            traverse.Method("UpdateReviewData").GetValue();
            return false;
        }
    }

    /// <summary>
    /// 7b. 命运点界面按钮:原版在 初始+加点 >= Max 时禁用加号,名单内属性改为只受命运点余额限制。
    ///     FateTweak 开启后按钮逻辑由 FateTweak.UpdatePropertyButton 全面接管。
    /// </summary>
    [HarmonyPatch(typeof(FateBonusProperty), nameof(FateBonusProperty.UpdateButton))]
    internal static class FateUpdateButtonPatch
    {
        private static void Postfix(FateBonusProperty __instance)
        {
            if (Plugin.FateTweakSwitch.Value) return;
            Traverse traverse = Traverse.Create(__instance);
            GameStat stat = traverse.Field("_stat").GetValue<GameStat>();
            if (!Plugin.IsUncapped(stat)) return;
            GameStat fateStat = traverse.Field("_fateStat").GetValue<GameStat>();
            if (fateStat != null && fateStat.FinalValue > 0)
            {
                traverse.Field("_addButton").GetValue<Button>().enabled = true;
            }
        }
    }

    /// <summary>
    /// 8. 曲线类转换破限延伸:原版 InverseLerp 把时间钳在 [0,1],属性超 100 后曲线饱和。
    /// 这里在 value > max 时按曲线尾部斜率(最后 5% 的斜率)线性外推,
    /// 100 以下与原版完全一致;100 以上沿曲线的设计趋势继续增长(递增但递减)。
    /// 只会在数值真的超过 max 时触发(原版不可能发生),不影响任何未解限属性。
    /// </summary>
    [HarmonyPatch(typeof(CurveStatConvertData), nameof(CurveStatConvertData.GetResult), new System.Type[] { typeof(int) })]
    internal static class CurveExtrapolatePatch
    {
        private static void Postfix(CurveStatConvertData __instance, int value, ref float __result)
        {
            if (!Plugin.Enabled.Value || !Plugin.CurveExtrapolate.Value) return;
            Traverse traverse = Traverse.Create(__instance);
            int min;
            int max;
            try
            {
                min = traverse.Method("GetMin").GetValue<int>();
                max = traverse.Method("GetMax").GetValue<int>();
            }
            catch
            {
                return;
            }
            if (max <= min || value <= max) return;
            AnimationCurve curve = traverse.Field("_curve").GetValue<AnimationCurve>();
            if (curve == null) return;
            float curveEnd = curve.Evaluate(1f);
            float curveNearEnd = curve.Evaluate(0.95f);
            float tailSlope = (curveEnd - curveNearEnd) / 0.05f;
            if (tailSlope <= 0f) return;
            float time = (float)(value - min) / (max - min);
            __result = curveEnd + (time - 1f) * tailSlope * Plugin.CurveExtrapolateFactor.Value;
        }
    }
}
