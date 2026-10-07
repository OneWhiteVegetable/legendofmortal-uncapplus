using System.Collections.Generic;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace UncapSixStats
{
    [BepInPlugin(GUID, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.lom.mod.uncapsixstats";
        public const string Name = "UncapSixStats";
        public const string Version = "2.0.1";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> ExtraUncapTypes;
        internal static ConfigEntry<float> RadarLogFactor;
        internal static ConfigEntry<float> RadarVisualMax;
        internal static ConfigEntry<float> OverlayAlpha;
        internal static ConfigEntry<string> OverlayColor;
        internal static ConfigEntry<bool> OverlayPulse;
        internal static ConfigEntry<float> OverlayPulseSpeed;
        internal static ConfigEntry<bool> CurveExtrapolate;
        internal static ConfigEntry<float> CurveExtrapolateFactor;
        internal static ConfigEntry<bool> DebugHotkey;
        internal static ConfigEntry<bool> MuscleSwitch;
        internal static ConfigEntry<bool> FateTweakSwitch;
        internal static ConfigEntry<bool> FateSkipLeftoverWarning;
        internal static ConfigEntry<string> FateUncapExtraTypes;

        internal static Color OverlayColorValue = new Color(0.92f, 0.95f, 1f);
        // 天命分配破限变色:突破原设计上限后数值改为此色(与雷达破限层同基调的金色)
        internal static readonly Color FateOverCapColor = new Color(1.0f, 0.84f, 0.30f);

        // 體力=0 內力=1 輕功=2
        internal static readonly HashSet<int> BaseUncapTypes = new HashSet<int> { 0, 1, 2 };
        internal static readonly HashSet<int> ExtraTypes = new HashSet<int>();
        // 天命分配扩展解限:銀兩=3 鍛造=17 毒藥(煉丹)=18 武學點數=31
        internal static readonly HashSet<int> FateExtraTypes = new HashSet<int>();

        // 全部武功类 StatType:刀劍100 暗器101 拳掌102 腿法103 奇門104 軟兵器105 槍棍106 內功107。
        // 注意:这些资产的 PropertyType 序列化值实为「一般」而非「武功」,不能靠 PropertyType 判定。
        internal static bool IsMartialStatType(int typeId)
        {
            return typeId >= 100 && typeId <= 107;
        }

        private static SynchronizationContext _mainThread;
        private int _tickCount;

        // Awake 期创建的标记物体:用于验证"Awake 期物体是否会在场景切换时被销毁"
        private static GameObject _awakeMarker;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "总开关:true=取消六维/武功上限");
            ExtraUncapTypes = Config.Bind("General", "ExtraUncapTypes", "",
                "额外解除上限的 GameStatType 数字ID,逗号分隔(默认已含 體力/內力/輕功/全部武功类)");
            RadarLogFactor = Config.Bind("Radar", "LogFactor", 1.0f,
                "雷达图超出部分的对数压缩系数,越大顶得越远(1.0:200→1.3,500→1.7,1000→2.0)");
            RadarVisualMax = Config.Bind("Radar", "VisualMax", 2.0f,
                "雷达图顶点半径的视觉上限(1.0=贴外框,2.0=最多顶到两倍半径)");
            OverlayAlpha = Config.Bind("Radar", "OverlayAlpha", 0.35f,
                "破限层不透明度(0~1)");
            OverlayColor = Config.Bind("Radar", "OverlayColor", "0.92,0.95,1.0",
                "破限层颜色 R,G,B(0~1),默认亮银白色");
            OverlayPulse = Config.Bind("Radar", "OverlayPulse", true,
                "破限层流动效果:true=三层亮银相位闪光流动(顶点固定不动);false=静态");
            OverlayPulseSpeed = Config.Bind("Radar", "OverlayPulseSpeed", 2.0f,
                "破限层流动速度");
            CurveExtrapolate = Config.Bind("Formula", "CurveExtrapolate", true,
                "曲线类加成破限延伸:true=属性超过100后,曲线型转换(如部分血量/加成)按曲线尾部斜率继续增长;false=100处饱和(与原版满值一致)");
            CurveExtrapolateFactor = Config.Bind("Formula", "CurveExtrapolateFactor", 1.0f,
                "曲线破限延伸的斜率倍率:1.0=按曲线尾部自然延伸,0.5=减半更平缓,2.0=加倍");
            DebugHotkey = Config.Bind("Debug", "EnableHotkey", false,
                "调试热键:true=游戏内 F9 武功拳掌+10(真实加点,存档后永久生效);F10/F2 不受此开关影响,随时可用");
            MuscleSwitch = Config.Bind("Muscle", "MusclePortrait", false,
                "赵活:你在唐门只能算是个萝莉——true=任一六维(體力/內力/輕功/刀劍/暗器/拳掌)突破100后立绘切换为肌肉版,掉回100以下自动还原原版");
            FateTweakSwitch = Config.Bind("FateTweak", "Enabled", true,
                "更好的天命点分配:true=继承界面加点立即落实、各项可减(只退已分配)、三页自由切换、复原=回到最初、确定=结束加点、剩余点数提示、长按连点、六维/武学点/银两/锻造/炼丹加点破限变色");
            FateSkipLeftoverWarning = Config.Bind("FateTweak", "SkipLeftoverWarning", false,
                "跳过剩余天命点提示:true=结束加点时不再提示未用完的点数(弹窗里勾选'之后不再提示'会自动置真)");
            FateUncapExtraTypes = Config.Bind("FateTweak", "UncapExtraTypes", "3,17,18,31",
                "天命分配额外解除加点上限的 GameStatType 数字ID,逗号分隔(默认 銀兩3/鍛造17/毒藥(煉丹)18/武學點數31;六维由总开关控制)");

            MusclePortraits.Init();

            ParseExtraTypes();
            ParseFateExtraTypes();
            ParseOverlayColor();

            // 逐类隔离打补丁:任何一个补丁失败只影响自己并写日志,不再连坐
            Harmony harmony = new Harmony(GUID);
            int applied = 0;
            System.Text.StringBuilder failed = new System.Text.StringBuilder();
            System.Type[] types;
            try
            {
                types = GetType().Assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException loadError)
            {
                types = loadError.Types;
            }
            foreach (System.Type type in types)
            {
                if (type == null) continue;
                if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try
                {
                    harmony.PatchAll(type);
                    applied++;
                }
                catch (System.Exception e)
                {
                    failed.Append(type.Name).Append('[').Append(e.Message).Append("]; ");
                }
            }

            _awakeMarker = new GameObject("UncapSixStats.AwakeMarker");
            UnityEngine.Object.DontDestroyOnLoad(_awakeMarker);

            _mainThread = SynchronizationContext.Current;
            HotkeyThread.Start();

            Log.LogInfo("[UncapSixStats] v" + Version + " 已加载。取消上限名单:體力/內力/輕功/全部武功类(StatType 100-107)"
                + (ExtraTypes.Count > 0 ? " + 额外ID:" + string.Join(",", ExtraTypes) : "")
                + (FateTweakSwitch.Value ? " | 天命分配破限ID:" + string.Join(",", FateExtraTypes) : ""));
            Log.LogInfo($"[UncapSixStats] 补丁应用:{applied} 类成功" +
                (failed.Length > 0 ? ",失败:" + failed : ",无失败"));
            Log.LogInfo($"[UncapSixStats] 配置: Enabled={Enabled.Value}, LogFactor={RadarLogFactor.Value}, VisualMax={RadarVisualMax.Value}, "
                + $"OverlayAlpha={OverlayAlpha.Value}, Pulse={OverlayPulse.Value}, CurveExtrapolate={CurveExtrapolate.Value}×{CurveExtrapolateFactor.Value}, DebugHotkey={DebugHotkey.Value}");
            Log.LogInfo("[UncapSixStats] 读档/新游戏进游戏后:系统菜单(右上角齿轮)内注入「上限解除」按钮;F2 直接开关面板;F10 打印诊断");
        }

        // 自检用:Awake 标记物体是否还活着(Unity 假 null 语义)
        internal static bool AwakeMarkerAlive => _awakeMarker != null;

        // 把操作投递到 Unity 主线程执行(Unity API 非线程安全)
        internal static void PostToMain(System.Action action)
        {
            try
            {
                if (_mainThread != null)
                {
                    _mainThread.Post(_ => action(), null);
                    return;
                }
            }
            catch { }
            action();
        }

        private void ParseExtraTypes()
        {
            ExtraTypes.Clear();
            string raw = ExtraUncapTypes.Value;
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (string part in raw.Split(','))
            {
                if (int.TryParse(part.Trim(), out int id))
                {
                    ExtraTypes.Add(id);
                }
            }
        }

        private void ParseFateExtraTypes()
        {
            FateExtraTypes.Clear();
            string raw = FateUncapExtraTypes.Value;
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (string part in raw.Split(','))
            {
                if (int.TryParse(part.Trim(), out int id))
                {
                    FateExtraTypes.Add(id);
                }
            }
        }

        private static void ParseOverlayColor()
        {
            string raw = OverlayColor.Value;
            if (string.IsNullOrWhiteSpace(raw)) return;
            string[] parts = raw.Split(',');
            if (parts.Length != 3) return;
            if (float.TryParse(parts[0], out float r) && float.TryParse(parts[1], out float g) && float.TryParse(parts[2], out float b))
            {
                OverlayColorValue = new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b));
            }
        }

        internal static void SetOverlayColorPublic(string rgb)
        {
            OverlayColor.Value = rgb;
            ParseOverlayColor();
        }

        internal static bool IsUncapped(Mortal.Core.GameStat stat)
        {
            if (stat == null) return false;
            int typeId = (int)stat.StatType;
            // 天命分配扩展解限(银两/锻造/炼丹/武学点):FateTweak 开关独立控制。
            // 继承界面立即落实写入后,必须让这些类型全程不被钳回 Max,
            // 否则破限部分会在开新周目/读档时丢失。
            if (FateTweakSwitch.Value && FateExtraTypes.Contains(typeId)) return true;
            if (!Enabled.Value) return false;
            if (IsMartialStatType(typeId)) return true;
            if (stat.PropertyType == Mortal.Core.GameStatPropertyType.武功) return true;
            return BaseUncapTypes.Contains(typeId) || ExtraTypes.Contains(typeId);
        }

        // 六维/武功类是否解限(不含天命扩展类型):用于继承界面区分"破限变色"的适用对象
        internal static bool IsSixStatUncapped(Mortal.Core.GameStat stat)
        {
            if (!Enabled.Value || stat == null) return false;
            int typeId = (int)stat.StatType;
            if (IsMartialStatType(typeId)) return true;
            if (stat.PropertyType == Mortal.Core.GameStatPropertyType.武功) return true;
            return BaseUncapTypes.Contains(typeId) || ExtraTypes.Contains(typeId);
        }

        internal static float SoftMapRadar(float distance)
        {
            if (distance > 1f)
            {
                distance = 1f + Mathf.Log10(distance) * RadarLogFactor.Value;
            }
            return Mathf.Clamp(distance, 0.02f, RadarVisualMax.Value);
        }

        // 心跳与主线程上下文兜底捕获:由 EventSystem.Update / SteamManager.Update 补丁驱动。
        // (这两个补丁只用于诊断,面板与热键不依赖它们)
        private int _lastTickFrame = -1;

        internal void OnGameTick()
        {
            if (Time.frameCount == _lastTickFrame) return;
            _lastTickFrame = Time.frameCount;
            if (_mainThread == null)
            {
                _mainThread = SynchronizationContext.Current;
            }
            _tickCount++;
            if (_tickCount == 1)
            {
                Log.LogInfo("[UncapSixStats] 游戏组件 tick 可用(EventSystem/SteamManager)");
            }
            else if (_tickCount % 1800 == 0)
            {
                Log.LogInfo($"[UncapSixStats] 心跳:{_tickCount} 帧");
            }
        }

        internal void AddStatPublic(Mortal.Core.GameStatType type, int delta)
        {
            AddStat(type, delta);
        }

        internal void DumpDiagnosticsPublic()
        {
            DumpDiagnostics();
        }

        private void AddStat(Mortal.Core.GameStatType type, int delta)
        {
            try
            {
                Mortal.Core.PlayerStatManagerData manager = Mortal.Core.PlayerStatManagerData.Instance;
                if (manager == null || manager.Stats == null) return;
                Mortal.Core.GameStat stat = manager.Stats.Get(type);
                if (stat == null) return;
                stat.AddValue(delta);
                Log.LogInfo($"[UncapSixStats] {type} {(delta > 0 ? "+" : "")}{delta} → 当前 FinalValue={stat.FinalValue}(真实数值,存档后永久生效)");
            }
            catch (System.Exception e)
            {
                Log.LogWarning(e.ToString());
            }
        }

        private void DumpDiagnostics()
        {
            try
            {
                Log.LogInfo("===== 数值转换诊断开始 =====");
                Mortal.Core.PlayerStatManagerData manager = Mortal.Core.PlayerStatManagerData.Instance;
                if (manager != null && manager.Stats != null)
                {
                    DumpStatAddition(manager, Mortal.Core.GameStatType.體力);
                    DumpStatAddition(manager, Mortal.Core.GameStatType.內力);
                    DumpStatAddition(manager, Mortal.Core.GameStatType.輕功);
                    Mortal.Core.GameStat life = manager.Stats.Get(Mortal.Core.GameStatType.體力);
                    Mortal.Combat.CombatManager combatManager = UnityEngine.Object.FindObjectOfType<Mortal.Combat.CombatManager>();
                    if (combatManager != null)
                    {
                        Mortal.Core.StatConvertCollectionData[] collections = Traverse.Create(combatManager)
                            .Field("_playerTotalHealth").GetValue<Mortal.Core.StatConvertCollectionData[]>();
                        int sample = life != null ? life.FinalValue : 100;
                        Log.LogInfo($"[诊断] 玩家血量转换(_playerTotalHealth),样本體力={sample}:");
                        DumpCollections(collections, sample);
                    }
                    else
                    {
                        Log.LogInfo("[诊断] 当前不在战斗场景,读不到血量转换(进一场战斗后再按F10)");
                    }
                }
                else
                {
                    Log.LogInfo("[诊断] 角色数值系统未加载(请先读档进入游戏)");
                }
                DumpPortraitImages();
                Log.LogInfo("===== 数值转换诊断结束 =====");
            }
            catch (System.Exception e)
            {
                Log.LogWarning(e.ToString());
            }
        }

        // 立绘普查:找出所有正在显示"大人尺寸立绘"的 Image/SpriteRenderer,打印层级路径
        private void DumpPortraitImages()
        {
            try
            {
                Log.LogInfo("[诊断] --- 立绘组件普查(纹理高度>=1000) ---");
                foreach (UnityEngine.UI.Image img in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Image>())
                {
                    try
                    {
                        if (img == null || img.sprite == null || img.sprite.texture == null) continue;
                        if (!img.gameObject.activeInHierarchy || img.sprite.texture.height < 1000) continue;
                        Log.LogInfo($"[诊断] Image sprite={img.sprite.name} {img.sprite.texture.width}x{img.sprite.texture.height} 路径={GetPath(img.transform)}");
                    }
                    catch { }
                }
                foreach (SpriteRenderer sr in Resources.FindObjectsOfTypeAll<SpriteRenderer>())
                {
                    try
                    {
                        if (sr == null || sr.sprite == null || sr.sprite.texture == null) continue;
                        if (!sr.gameObject.activeInHierarchy || sr.sprite.texture.height < 1000) continue;
                        Log.LogInfo($"[诊断] SpriteRenderer sprite={sr.sprite.name} {sr.sprite.texture.width}x{sr.sprite.texture.height} 路径={GetPath(sr.transform)}");
                    }
                    catch { }
                }
            }
            catch (System.Exception e)
            {
                Log.LogWarning("[诊断] 立绘普查异常:" + e.Message);
            }
        }

        private static string GetPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        private void DumpStatAddition(Mortal.Core.PlayerStatManagerData manager, Mortal.Core.GameStatType type)
        {
            Mortal.Core.GameStat stat = manager.Stats.Get(type);
            if (stat == null) return;
            Mortal.Core.StatConvertCollectionData[] collections = Traverse.Create(stat)
                .Field("_statAdditionData").GetValue<Mortal.Core.StatConvertCollectionData[]>();
            Log.LogInfo($"[诊断] {type} 的数值转换加成(FinalValue={stat.FinalValue}):");
            DumpCollections(collections, stat.FinalValue);
        }

        private void DumpCollections(Mortal.Core.StatConvertCollectionData[] collections, int sampleValue)
        {
            if (collections == null || collections.Length == 0)
            {
                Log.LogInfo("    (无转换集合)");
                return;
            }
            foreach (Mortal.Core.StatConvertCollectionData collection in collections)
            {
                if (collection == null) continue;
                float totalRate = Traverse.Create(collection).Field("_totalRate").GetValue<float>();
                Log.LogInfo($"  集合 [{collection.name}] 总和倍率={totalRate}");
                foreach (Mortal.Core.StatConvertData entry in collection.List)
                {
                    if (entry == null) continue;
                    float resultNow = entry.GetResult(sampleValue);
                    float resultMore = entry.GetResult(sampleValue + 200);
                    string source = "";
                    if (entry is Mortal.Core.GameStatConvertData || entry is Mortal.Core.GameStatRateConvertData)
                    {
                        Mortal.Core.GameStat src = Traverse.Create(entry).Field("_statData").GetValue<Mortal.Core.GameStat>();
                        if (src != null) source = $" 来源={src.StatType}";
                    }
                    string trend = resultMore > resultNow ? "(+200后继续增长)" : "(+200后饱和)";
                    Log.LogInfo($"    - {entry.GetType().Name} [{entry.name}]{source}: {sampleValue}→{resultNow:F1} / {sampleValue + 200}→{resultMore:F1} {trend}");
                }
            }
        }
    }
}
