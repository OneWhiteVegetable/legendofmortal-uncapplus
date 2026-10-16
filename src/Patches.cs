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
    ///    命中自定义上限覆盖(如貢獻度20→500)的按覆盖上限钳。
    /// </summary>
    [HarmonyPatch(typeof(GameStat), nameof(GameStat.AddValue))]
    internal static class GameStatAddValuePatch
    {
        private static bool Prefix(GameStat __instance, int value, bool totalAdd)
        {
            bool uncapped = Plugin.IsUncapped(__instance);
            int overrideMax = 0;
            bool overridden = !uncapped && Plugin.TryGetMaxOverride(__instance, out overrideMax);
            if (!uncapped && !overridden) return true;
            Traverse traverse = Traverse.Create(__instance);
            int oldValue = traverse.Field("_value").GetValue<int>();
            int newValue = oldValue + value;
            int upper = uncapped ? int.MaxValue : overrideMax;
            if (newValue > upper)
            {
                newValue = upper;
            }
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
            bool uncapped = Plugin.IsUncapped(__instance);
            int overrideMax = 0;
            bool overridden = !uncapped && Plugin.TryGetMaxOverride(__instance, out overrideMax);
            if (!uncapped && !overridden) return true;
            Traverse traverse = Traverse.Create(__instance);
            int oldValue = traverse.Field("_value").GetValue<int>();
            int newValue = value < __instance.Min ? __instance.Min : value;
            if (overridden && newValue > overrideMax)
            {
                newValue = overrideMax;
            }
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
    /// 3. FinalValue 读取:名单内属性去掉上界(保留加成、保留下界);
    ///    命中自定义上限覆盖的按覆盖上限钳(_limitAddition 保留)。
    /// </summary>
    [HarmonyPatch(typeof(GameStat), nameof(GameStat.FinalValue), MethodType.Getter)]
    internal static class GameStatFinalValuePatch
    {
        private static void Postfix(GameStat __instance, ref int __result)
        {
            bool uncapped = Plugin.IsUncapped(__instance);
            int overrideMax = 0;
            bool overridden = !uncapped && Plugin.TryGetMaxOverride(__instance, out overrideMax);
            if (!uncapped && !overridden) return;
            int baseValue = Traverse.Create(__instance).Field("_value").GetValue<int>();
            if (uncapped)
            {
                __result = System.Math.Max(baseValue + __instance.AdditionValue, __instance.Min);
                return;
            }
            int limitAddition = Traverse.Create(__instance).Field("_limitAddition").GetValue<int>();
            __result = Mathf.Clamp(baseValue + __instance.AdditionValue, __instance.Min, overrideMax + limitAddition);
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
    /// 6. 战斗面板雷达图(2026-10-15 新版签名):官方新增 _statRadar2 画 100~200 的溢出部分,
    ///    与 mod 的对数压缩破限层二选一 —— 主雷达仍由 mod 软映射+金色破限层,
    ///    官方第二雷达压制为 0,避免同一溢出画两遍。
    /// </summary>
    [HarmonyPatch(typeof(CombatCharacterStatusUI), "SetRadarStat")]
    internal static class CombatRadarPatch
    {
        private static void Postfix(CombatCharacterStatusUI __instance, int value, UIPolygon radar, int max, int index)
        {
            Traverse traverse = Traverse.Create(__instance);
            UIPolygon main = traverse.Field("_statRadar").GetValue<UIPolygon>();
            UIPolygon second = traverse.Field("_statRadar2").GetValue<UIPolygon>();
            if (second != null && radar == second)
            {
                // 官方溢出层:mod 开启时压制(mod 破限层接管),关闭时放行原版
                if (Plugin.Enabled.Value && index >= 0 && index < radar.VerticesDistances.Length)
                {
                    radar.VerticesDistances[index] = 0f;
                }
                return;
            }
            if (main == null || radar != main) return;
            if (!Plugin.Enabled.Value)
            {
                // 总开关关闭:把已存在的破限层藏起来(只查不建)
                RadarOverlay.SetOverCap(RadarOverlay.FindAll(main), false);
                return;
            }
            if (index < 0 || index >= main.VerticesDistances.Length) return;
            RadarOverlayPulse[] pulses = RadarOverlay.EnsureAll(main);
            float raw = max > 0 ? (float)value / max : 0f;
            float soft = Plugin.SoftMapRadar(raw);
            main.VerticesDistances[index] = Mathf.Clamp(soft, 0.02f, 1f);
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
    /// 7. 左侧数值变动白条:原版显示的"新值"按 Max 钳制(破限后仍显示 100)。
    ///    这里在 Setup 后把数值文本改成真实新值;进度条(fillAmount)保持原版钳制不动。
    ///    命中自定义上限覆盖(貢獻度→500)的按覆盖上限显示。
    /// </summary>
    [HarmonyPatch(typeof(NormalStatModification), nameof(NormalStatModification.Setup))]
    internal static class StatModificationDisplayPatch
    {
        private static void Postfix(NormalStatModification __instance, GameStat stat, int addValue)
        {
            if (stat == null) return;
            bool uncapped = Plugin.IsUncapped(stat);
            int overrideMax = 0;
            bool overridden = !uncapped && Plugin.TryGetMaxOverride(stat, out overrideMax);
            if (!uncapped && !overridden) return;

            Traverse traverse = Traverse.Create(__instance);
            int currentValue = traverse.Field("_currentValue").GetValue<int>();
            int maxValue = traverse.Field("_maxValue").GetValue<int>();
            int ceiling = uncapped ? int.MaxValue : overrideMax;
            int shown = Mathf.Clamp(currentValue + addValue, 0, ceiling);
            int vanilla = Mathf.Clamp(currentValue + addValue, 0, maxValue);
            if (shown == vanilla) return; // 没破限,显示本来就对
            Text valueText = traverse.Field("_valueText").GetValue<Text>();
            if (valueText != null)
            {
                valueText.text = shown.ToString();
            }
        }
    }

    /// <summary>
    /// 9. 勇者模式公平钳制(S_053 数值平衡):官方读取玩家六维 FinalValue 总和来强化敌人,
    ///    破上限玩家会被额外针对。本补丁把"玩家侧六维"按原版上限 100 逐项封顶后再求和,
    ///    敌人侧数值与公式完全不动(含官方自身的暗器重复/拳掌缺失,这里顺手修正为六项齐全)。
    /// </summary>
    [HarmonyPatch(typeof(Mortal.Combat.S_053), "Init")]
    internal static class BraveFairClampPatch
    {
        private static readonly CombatStatType[] SixStats =
        {
            CombatStatType.體力, CombatStatType.內力, CombatStatType.輕功,
            CombatStatType.刀劍, CombatStatType.拳掌, CombatStatType.暗器
        };

        private static bool Prefix(CombatStateEffectParameter param, ref IEnumerator __result)
        {
            if (!Plugin.Enabled.Value || !Plugin.BraveFairClamp.Value) return true;
            __result = RunFair(param);
            return false;
        }

        private static IEnumerator RunFair(CombatStateEffectParameter param)
        {
            string key = param.Effect.Key;
            CombatActionController target = param.Target;   // 玩家侧
            CombatActionController source = param.Source;   // 敌人侧
            CombatStatController stat = source.Stat;
            CombatStatBalanceData balance = source.Stat.Data.StatBalanceData;
            // 与原版唯一的区别:玩家六维按原版上限 100 逐项封顶
            int playerTotal = 0;
            foreach (CombatStatType type in SixStats)
            {
                playerTotal += Mathf.Min(target.Stat.GetStatItem(type).FinalValue, 100);
            }
            float playerScale = playerTotal / 100f;
            float enemyScale = source.Stat.GetSixStatTotalValue() / 100f;
            float diff = Mathf.Max(0f, playerScale - enemyScale);
            float over = Mathf.Max(0f, diff - balance.CatchUpThreshold);
            if (over > 0f)
            {
                float curve = over / (over + balance.CatchUpK);
                float ratio = balance.CatchUpMaxBonus * curve;
                foreach (CombatStatType type in SixStats)
                {
                    CombatStatItem item = stat.GetStatItem(type);
                    int add = Mathf.RoundToInt(item.FinalValue * ratio);
                    item.AddModify(key, add);
                }
                Plugin.Log.LogInfo($"[勇者] 數值平衡(公平鉗制): 玩家六維(封頂後)={playerTotal} 敵方={Mathf.RoundToInt(enemyScale * 100f)} 加成比例={ratio:P1}");
            }
            yield return null;
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
