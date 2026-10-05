using System.Collections.Generic;
using HarmonyLib;
using Mortal.Core;
using UnityEngine;
using UnityEngine.UI;

namespace UncapSixStats
{
    /// <summary>
    /// 运行时侦察:继承界面首次打开时把关键事实写进日志——
    /// 属性项的 StatType/Max/初始值/每点增量(确认武学/银两/锻造/炼丹的类型 ID 与上限)、
    /// 天命点池 _fateStat 的身份、确认弹窗的结构(供克隆改造)、加号按钮布局(供减号对位)。
    /// </summary>
    internal static class FateRecon
    {
        private static bool _done;

        internal static void DumpOnce(FateBonusPanel panel)
        {
            if (_done) return;
            _done = true;
            try
            {
                Traverse t = Traverse.Create(panel);
                Plugin.Log.LogInfo("[偵察] ===== 天命繼承介面 =====");

                List<FateBonusProperty> props = t.Field("_propertyList").GetValue<List<FateBonusProperty>>();
                Plugin.Log.LogInfo($"[偵察] 屬性項 {props.Count} 個:");
                foreach (FateBonusProperty p in props)
                {
                    Traverse pt = Traverse.Create(p);
                    Mortal.Core.GameStat stat = pt.Field("_stat").GetValue<Mortal.Core.GameStat>();
                    Mortal.Core.GameStat fate = pt.Field("_fateStat").GetValue<Mortal.Core.GameStat>();
                    int addPoint = pt.Field("_addPoint").GetValue<int>();
                    bool disableMinus = pt.Field("_disableMinus").GetValue<bool>();
                    Button add = pt.Field("_addButton").GetValue<Button>();
                    Button minus = pt.Field("_minusButton").GetValue<Button>();
                    string rect = add != null ? RectInfo((RectTransform)add.transform) : "null";
                    string minusRect = minus != null ? RectInfo((RectTransform)minus.transform) : "null";
                    Plugin.Log.LogInfo($"[偵察]   {stat.StatType}({(int)stat.StatType}) 類型={stat.PropertyType} "
                        + $"Max={stat.Max} Min={stat.Min} 初值={stat.FinalValue} 每點+{addPoint} 藏減號={disableMinus} "
                        + $"加號{rect} 減號{minusRect}");
                    if (fate != null)
                    {
                        Plugin.Log.LogInfo($"[偵察]   → 天命點池 _fateStat = {fate.StatType}({(int)fate.StatType}) "
                            + $"Max={fate.Max} 現值={fate.FinalValue}");
                    }
                }

                List<FateBonusSocial> socials = t.Field("_socialList").GetValue<List<FateBonusSocial>>();
                Plugin.Log.LogInfo($"[偵察] 人際項 {socials.Count} 個:");
                foreach (FateBonusSocial s in socials)
                {
                    Traverse st = Traverse.Create(s);
                    Mortal.Core.RelationshipStat stat = st.Field("_stat").GetValue<Mortal.Core.RelationshipStat>();
                    Button add = st.Field("_addButton").GetValue<Button>();
                    int max = st.Field("_max").GetValue<int>();
                    int addPoint = st.Field("_addPoint").GetValue<int>();
                    Plugin.Log.LogInfo($"[偵察]   {stat.Type} 值={stat.Value} Max={max} 每點+{addPoint} 加號{RectInfo((RectTransform)add.transform)}");
                }

                List<FateBonusFlag> flags = t.Field("_flagList").GetValue<List<FateBonusFlag>>();
                Plugin.Log.LogInfo($"[偵察] 門派項 {flags.Count} 個:");
                foreach (FateBonusFlag f in flags)
                {
                    Traverse ft = Traverse.Create(f);
                    Mortal.Core.FlagData stat = ft.Field("_stat").GetValue<Mortal.Core.FlagData>();
                    Button add = ft.Field("_addButton").GetValue<Button>();
                    int max = ft.Field("_max").GetValue<int>();
                    int addPoint = ft.Field("_addPoint").GetValue<int>();
                    Plugin.Log.LogInfo($"[偵察]   {stat.name} State={stat.State} Max={max} 每點+{addPoint} 加號{RectInfo((RectTransform)add.transform)}");
                }

                GameObject confirm = t.Field("_confirmBonusPanel").GetValue<GameObject>();
                Plugin.Log.LogInfo("[偵察] 確認彈窗結構:");
                DumpHierarchy(confirm != null ? confirm.transform : null, "  ");

                Plugin.Log.LogInfo($"[偵察] 剩餘天命點 = {FateTweak.GetRemainingFate(panel)}");
                Plugin.Log.LogInfo("[偵察] ===================");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[偵察] 失敗:" + e);
            }
        }

        private static string RectInfo(RectTransform r)
        {
            return $"(pos={r.anchoredPosition.x:F0},{r.anchoredPosition.y:F0} size={r.sizeDelta.x:F0}x{r.sizeDelta.y:F0})";
        }

        private static void DumpHierarchy(Transform root, string indent)
        {
            if (root == null) return;
            string components = "";
            foreach (Component c in root.GetComponents<Component>())
            {
                if (c is Transform || c is RectTransform) continue;
                if (c == null) { components += "[null]"; continue; }
                components += c.GetType().Name + " ";
            }
            string text = "";
            Text label = root.GetComponent<Text>();
            if (label != null) text = " 文本=\"" + label.text + "\"";
            Plugin.Log.LogInfo($"[偵察]{indent}{root.name} <{components}>{text}");
            foreach (Transform child in root)
            {
                DumpHierarchy(child, indent + "  ");
            }
        }
    }
}
