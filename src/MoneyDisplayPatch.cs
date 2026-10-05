using HarmonyLib;
using Mortal.Core;
using UnityEngine;
using UnityEngine.UI;

namespace UncapSixStats
{
    /// <summary>
    /// 银两超过 99999(原设计上限,正好五位数)后,各处绑钱数的 GameStatText 宽度放不下第六位。
    /// 给钱的数值文本开"最佳适配自动缩字",六、七位也能完整显示;只动显示,不动数值。
    /// </summary>
    [HarmonyPatch(typeof(GameStatText), "UpdateText")]
    internal static class MoneyDisplayPatch
    {
        private static void Postfix(GameStatText __instance)
        {
            if (!Plugin.FateTweakSwitch.Value) return;
            try
            {
                GameStat stat = Traverse.Create(__instance).Field("_statData").GetValue<GameStat>();
                if (stat == null || stat.StatType != GameStatType.銀兩) return;

                Text text = Traverse.Create(__instance).Field("_text").GetValue<Text>();
                if (text != null)
                {
                    if (!text.resizeTextForBestFit)
                    {
                        text.resizeTextForBestFit = true;
                        text.resizeTextMaxSize = text.fontSize;
                        text.resizeTextMinSize = Mathf.Max(8, Mathf.RoundToInt(text.fontSize * 0.55f));
                    }
                    // 兜底:有些布局对 resizeTextForBestFit 不生效,直接放行水平溢出
                    if (text.horizontalOverflow != HorizontalWrapMode.Overflow)
                    {
                        text.horizontalOverflow = HorizontalWrapMode.Overflow;
                    }
                    return;
                }

                Component tmp = Traverse.Create(__instance).Field("_textMeshPro").GetValue<Component>();
                if (tmp != null)
                {
                    Traverse t = Traverse.Create(tmp);
                    if (!t.Property("enableAutoSizing").GetValue<bool>())
                    {
                        float size = t.Property("fontSize").GetValue<float>();
                        t.Property("enableAutoSizing").SetValue(true);
                        t.Property("fontSizeMax").SetValue(size);
                        t.Property("fontSizeMin").SetValue(Mathf.Max(8f, size * 0.55f));
                    }
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[银两显示] 适配失败:" + e.Message);
            }
        }
    }
}
