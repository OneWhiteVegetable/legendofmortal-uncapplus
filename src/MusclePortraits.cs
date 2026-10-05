using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace UncapSixStats
{
    /// <summary>
    /// 肌肉立绘联动:开关打开且任一六维(體力/內力/輕功/武功刀劍/武功暗器/武功拳掌)突破上限时,
    /// 拦截 Addressables 的 Sprite 加载,用 GigaMuscle 目录下的同名 PNG 现场构造 Sprite 顶替;
    /// 条件不满足(或开关关闭)时放行原版加载,掉回 100 以下自动还原。
    /// 只加文件不改游戏任何原始资源;判定在每次立绘加载时现场进行,数值变化后下一张立绘即生效。
    /// 反向切换(破百→回落/关闭开关)时主动把屏幕上正在显示的肌肉立绘刷回原版,不必等场景切换。
    /// </summary>
    internal static class MusclePortraits
    {
        // 面板开关是否打开(纯配置,与数值条件无关)
        internal static bool SwitchOn => Plugin.MuscleSwitch != null && Plugin.MuscleSwitch.Value;

        private static string _assetsRoot;
        private static readonly HashSet<string> _availableKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        // 反查表:我们服务出去的 Sprite → 它的 Addressables key,用于还原时定位屏幕上显示中的肌肉立绘
        private static readonly Dictionary<Sprite, string> _reverse = new Dictionary<Sprite, string>();
        // 原版立绘实例 → key(放行原版时由补丁 postfix 登记),用于破百瞬间定位屏幕上显示中的原版立绘
        private static readonly Dictionary<Sprite, string> _originalSprites = new Dictionary<Sprite, string>();
        // key → 原版立绘实例(还原时优先直接用,避免 Addressables 同步加载)
        private static readonly Dictionary<string, Sprite> _originalByKey = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private static bool _ready;

        // 诊断:已记录过的 sprite key(每个只记一次)
        private static readonly HashSet<string> _seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 按帧缓存数值条件,避免每次立绘加载都遍历六次
        private static int _condFrame = -1;
        private static bool _condValue;
        private static bool _flipLogged;

        internal static void Init()
        {
            try
            {
                _assetsRoot = Path.Combine(Paths.PluginPath, "GigaMuscle");
                if (!Directory.Exists(_assetsRoot))
                {
                    Plugin.Log.LogWarning("[Muscle] 未找到资源目录 " + _assetsRoot + ",肌肉立绘功能不可用");
                    return;
                }
                foreach (string file in Directory.GetFiles(_assetsRoot, "*.png", SearchOption.AllDirectories))
                {
                    string key = file.Substring(_assetsRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
                    _availableKeys.Add(key);
                }
                _ready = _availableKeys.Count > 0;
                Plugin.Log.LogInfo($"[Muscle] 资源目录就绪: {_availableKeys.Count} 张立绘 ({_assetsRoot})");
                // 开关拨动时立刻重算条件,翻转分支会即时刷新屏幕上的立绘(双向)
                Plugin.MuscleSwitch.SettingChanged += (s, e) =>
                {
                    _condFrame = -1;
                    try
                    {
                        IsActiveNow();
                    }
                    catch { }
                };
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Muscle] 初始化失败: " + e);
            }
        }

        /// <summary>数值条件:任一六维 FinalValue 突破各自上限(原上限 100)。读档前/标题画面返回 false。</summary>
        private static bool ComputeOverCap()
        {
            try
            {
                Mortal.Core.PlayerStatManagerData manager = Mortal.Core.PlayerStatManagerData.Instance;
                if (manager == null || manager.Stats == null) return false;
                Mortal.Core.GameStatType[] types =
                {
                    Mortal.Core.GameStatType.體力, Mortal.Core.GameStatType.內力, Mortal.Core.GameStatType.輕功,
                    Mortal.Core.GameStatType.武功刀劍, Mortal.Core.GameStatType.武功暗器, Mortal.Core.GameStatType.武功拳掌
                };
                foreach (Mortal.Core.GameStatType type in types)
                {
                    Mortal.Core.GameStat stat = manager.Stats.Get(type);
                    if (stat != null && stat.FinalValue > stat.Max) return true;
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private static bool IsActiveNow()
        {
            if (Time.frameCount == _condFrame) return _condValue;
            _condFrame = Time.frameCount;
            _condValue = SwitchOn && ComputeOverCap();
            if (_condValue != _flipLogged)
            {
                _flipLogged = _condValue;
                Plugin.Log.LogInfo(_condValue
                    ? "[Muscle] 检测到六维突破上限,立绘切换为肌肉版"
                    : "[Muscle] 六维已回落至上限内,立绘还原为原版");
                if (_condValue)
                {
                    Plugin.PostToMain(RefreshDisplayedToMuscle);
                }
                else
                {
                    Plugin.PostToMain(RefreshDisplayedToOriginal);
                }
            }
            return _condValue;
        }

        /// <summary>
        /// Image.sprite 赋值拦截:条件满足时把"登记过的原版立绘"替换为肌肉版;条件不满足时把
        /// 肌肉版换回原版。庭院常驻立绘(路径 [UI]/MainUI/Layer_1/Avatar)首次见到未登记的
        /// 原版 Sprite 时自学习登记。我们自己换图触发的赋值天然通过(已是目标版本)。
        /// </summary>
        internal static void InterceptAssignment(UnityEngine.UI.Image img, ref Sprite value)
        {
            if (!_ready || img == null || value == null) return;
            if (IsActiveNow())
            {
                if (_reverse.ContainsKey(value)) return; // 已是肌肉版
                if (_originalSprites.TryGetValue(value, out string key))
                {
                    Sprite muscle = LoadCached(key);
                    if (muscle != null) value = muscle;
                    return;
                }
                // 庭院常驻立绘的自学习:它持有的是序列化/动画赋值的原版 Sprite
                if (IsCourtyardAvatar(img))
                {
                    RegisterOriginal(PlayerAvatarPanelPatch.NormalKey, value);
                    Sprite muscle = LoadCached(PlayerAvatarPanelPatch.NormalKey);
                    if (muscle != null) value = muscle;
                }
            }
            else
            {
                if (_reverse.TryGetValue(value, out string key)
                    && _originalByKey.TryGetValue(key, out Sprite original) && original != null)
                {
                    value = original;
                }
            }
        }

        /// <summary>登记一次原版立绘加载(仅 GigaMuscle 有对应文件的 key),供破百瞬间反查。</summary>
        internal static void RegisterOriginal(string key, Sprite sprite)
        {
            if (sprite == null) return;
            if (_reverse.ContainsKey(sprite)) return; // 我们自己的肌肉图不算
            _originalSprites[sprite] = key;
            _originalByKey[key] = sprite;
        }

        internal static bool IsTrackable(object keyObj)
        {
            return keyObj is string key && _availableKeys.Contains(key);
        }

        /// <summary>条件满足时返回指定 key 的肌肉版 Sprite,否则 null。供不走 Addressables 的序列化立绘面板使用。</summary>
        internal static Sprite GetMuscleSpriteIfActive(string key)
        {
            if (!_ready || !SwitchOn) return null;
            if (!_availableKeys.Contains(key)) return null;
            if (!IsActiveNow()) return null;
            return LoadCached(key);
        }

        /// <summary>
        /// 破百瞬间主动刷新:把屏幕上正在显示"原版立绘"的 UI Image 换成肌肉版,
        /// 并处理不走 Addressables 的序列化立绘面板(PlayerAvatarPanel 等)。
        /// </summary>
        private static void RefreshDisplayedToMuscle()
        {
            try
            {
                int refreshed = 0;
                foreach (UnityEngine.UI.Image img in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Image>())
                {
                    try
                    {
                        if (img == null || img.sprite == null || !img.gameObject.activeInHierarchy) continue;
                        if (!_originalSprites.TryGetValue(img.sprite, out string key)) continue;
                        Sprite muscle = LoadCached(key);
                        if (muscle != null)
                        {
                            img.sprite = muscle;
                            refreshed++;
                        }
                    }
                    catch { }
                }
                foreach (Mortal.Core.PlayerAvatarPanel panel in Resources.FindObjectsOfTypeAll<Mortal.Core.PlayerAvatarPanel>())
                {
                    try
                    {
                        PlayerAvatarPanelPatch.ApplyIfActive(panel, ref refreshed);
                    }
                    catch { }
                }
                if (refreshed > 0)
                {
                    Plugin.Log.LogInfo($"[Muscle] 已即时刷新 {refreshed} 处显示中立绘为肌肉版");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Muscle] 即时刷新(肌肉)失败: " + e.Message);
            }
        }

        /// <summary>属性写入点(GameStat.AddValue/SetValue postfix)直接调用:立刻重算条件,不等下一次立绘加载。</summary>
        internal static void NotifyStatChanged()
        {
            try
            {
                _condFrame = -1;
                IsActiveNow();
            }
            catch { }
        }

        /// <summary>
        /// 还原时主动刷新:找出当前正在显示我们所服务 Sprite 的 UI Image,换回原版图
        /// (优先用登记过的原版实例,没有再向 Addressables 请求——此时条件已不满足,会穿过拦截走原版 bundle)。
        /// </summary>
        private static void RefreshDisplayedToOriginal()
        {
            try
            {
                int refreshed = 0;
                foreach (UnityEngine.UI.Image img in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Image>())
                {
                    try
                    {
                        if (img == null || img.sprite == null || !img.gameObject.activeInHierarchy) continue;
                        if (!_reverse.TryGetValue(img.sprite, out string key)) continue;
                        Sprite original;
                        if (!_originalByKey.TryGetValue(key, out original) || original == null)
                        {
                            original = Addressables.LoadAssetAsync<Sprite>(key).WaitForCompletion();
                        }
                        if (original != null)
                        {
                            img.sprite = original;
                            refreshed++;
                        }
                    }
                    catch { }
                }
                if (refreshed > 0)
                {
                    Plugin.Log.LogInfo($"[Muscle] 已即时刷新 {refreshed} 处显示中立绘为原版");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Muscle] 即时刷新失败: " + e.Message);
            }
        }

        // 庭院常驻立绘(HUD)的实测路径:[UI]/MainUI/Layer_1/Avatar
        // 它由游戏入场动画用序列化 Sprite 直接赋值,不经过 Addressables,实例反查不到,
        // 按路径识别后自学习登记其原版 Sprite,纳入同步
        private static bool IsCourtyardAvatar(UnityEngine.UI.Image img)
        {
            Transform t = img.transform;
            return t.name == "Avatar"
                && t.parent != null && t.parent.name == "Layer_1"
                && t.parent.parent != null && t.parent.parent.name == "MainUI";
        }

        /// <summary>
        /// 看门狗式同步(由 PortraitSyncWatcher 低频调用):有些界面(如庭院常驻立绘)会被
        /// 游戏自己的动画/入场逻辑在 OnEnable 之后重新赋值,一次性的翻转刷新盖不住,
        /// 这里持续巡检所有显示中的 Image,把状态不对的立绘纠正到当前条件应有的版本。
        /// </summary>
        internal static void SyncDisplayedPortraits()
        {
            if (!_ready) return;
            bool muscle = _flipLogged; // 最近一次确认的条件状态
            foreach (UnityEngine.UI.Image img in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Image>())
            {
                try
                {
                    if (img == null || img.sprite == null || !img.gameObject.activeInHierarchy) continue;
                    // 庭院常驻立绘:见到未登记的原版 Sprite 就先学习登记
                    if (IsCourtyardAvatar(img) && !_reverse.ContainsKey(img.sprite)
                        && !_originalSprites.ContainsKey(img.sprite))
                    {
                        RegisterOriginal(PlayerAvatarPanelPatch.NormalKey, img.sprite);
                    }
                    if (muscle)
                    {
                        if (_originalSprites.TryGetValue(img.sprite, out string key))
                        {
                            Sprite m = LoadCached(key);
                            if (m != null) img.sprite = m;
                        }
                    }
                    else
                    {
                        if (_reverse.TryGetValue(img.sprite, out string key)
                            && _originalByKey.TryGetValue(key, out Sprite o) && o != null)
                        {
                            img.sprite = o;
                        }
                    }
                }
                catch { }
            }
        }

        /// <summary>尝试为 Addressables key 提供肌肉版 Sprite;不满足条件或没有对应文件时返回 false(放行原版)。</summary>
        internal static bool TryGetSprite(object keyObj, out Sprite sprite)
        {
            sprite = null;
            string key = keyObj as string;
            if (string.IsNullOrEmpty(key)) return false;
            // 诊断:记录所有角色立绘类 key 的真实格式(每个只记一次)
            if (key.IndexOf("Characters", StringComparison.OrdinalIgnoreCase) >= 0 && _seenKeys.Add(key))
            {
                Plugin.Log.LogInfo("[Muscle][诊断] sprite key: " + key);
            }
            if (!_ready || !SwitchOn) return false;
            // 快速路径:只关心角色立绘目录
            if (!key.StartsWith("Assets/__Project/Images/Characters/", StringComparison.OrdinalIgnoreCase)) return false;
            if (!_availableKeys.Contains(key)) return false;
            if (!IsActiveNow()) return false;
            sprite = LoadCached(key);
            return sprite != null;
        }

        private static Sprite LoadCached(string key)
        {
            if (_cache.TryGetValue(key, out Sprite cached) && cached != null) return cached;
            try
            {
                string path = Path.Combine(_assetsRoot, key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) return null;
                byte[] bytes = File.ReadAllBytes(path);
                Texture2D tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, bytes))
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                // 与原 mod(Binarizer)默认参数一致:左下 pivot、PPU=100、Tight 网格
                Sprite sprite = Sprite.Create(tex,
                    new Rect(0f, 0f, tex.width, tex.height),
                    new Vector2(0f, 0f), 100f, 0, SpriteMeshType.Tight);
                sprite.name = Path.GetFileNameWithoutExtension(key);
                _cache[key] = sprite;
                _reverse[sprite] = key;
                Plugin.Log.LogInfo("[Muscle] 立绘来自文件: " + key);
                return sprite;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Muscle] 加载失败 " + key + ": " + e.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// 拦截 Addressables.LoadAssetAsync&lt;Sprite&gt;(object key):
    /// 条件满足且 GigaMuscle 有同名文件时直接返回已完成的操作(跳过原版 bundle),
    /// 否则放行原版逻辑。对剧情立绘/战斗立绘/状态头像等所有走 Addressables 的 Sprite 生效。
    /// </summary>
    [HarmonyPatch]
    internal static class MuscleSpriteLoadPatch
    {
        private static MethodBase TargetMethod()
        {
            foreach (MethodInfo method in typeof(Addressables).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "LoadAssetAsync" || !method.IsGenericMethodDefinition) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(object))
                {
                    return method.MakeGenericMethod(typeof(Sprite));
                }
            }
            Plugin.Log.LogWarning("[Muscle] 未找到 Addressables.LoadAssetAsync<Sprite>(object) 方法,补丁未应用");
            return null;
        }

        private static bool Prefix(object key, ref AsyncOperationHandle<Sprite> __result)
        {
            try
            {
                if (MusclePortraits.TryGetSprite(key, out Sprite sprite))
                {
                    __result = Addressables.ResourceManager.CreateCompletedOperation(sprite, null);
                    return false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Muscle] 拦截异常(放行原版): " + e.Message);
            }
            return true;
        }

        // 放行原版时登记 原版Sprite实例→key,供破百瞬间反查并即时替换显示中的立绘
        private static void Postfix(object key, AsyncOperationHandle<Sprite> __result)
        {
            try
            {
                if (!MusclePortraits.IsTrackable(key)) return;
                __result.Completed += op => MusclePortraits.RegisterOriginal(key as string, op.Result);
            }
            catch { }
        }
    }

    /// <summary>
    /// 终极兜底:拦截所有 Image.sprite 赋值。庭院常驻立绘(HUD)会被游戏入场动画反复重设,
    /// 巡检/事件刷新都盖不住;直接在赋值点换图,任何来源的写入都会被纠正。
    /// 代价:每次赋值多两次字典查找,可忽略。
    /// </summary>
    [HarmonyPatch(typeof(UnityEngine.UI.Image), "sprite", MethodType.Setter)]
    internal static class ImageSpriteAssignPatch
    {
        private static void Prefix(UnityEngine.UI.Image __instance, ref Sprite value)
        {
            try
            {
                MusclePortraits.InterceptAssignment(__instance, ref value);
            }
            catch { }
        }
    }

    /// <summary>低频巡检组件:挂在面板根物体(DontDestroyOnLoad)上,每 0.5 秒纠正一次显示中立绘的状态。</summary>
    internal class PortraitSyncWatcher : MonoBehaviour
    {
        private float _next;

        private void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;
            MusclePortraits.SyncDisplayedPortraits();
        }
    }

    /// <summary>
    /// 状态页等处的主角立绘走的是序列化 Sprite 字段(PlayerAvatarPanel._normal/_normal2),
    /// 不经过 Addressables,这里在 OnEnable 后补上肌肉版替换。
    /// _normal2 是差分造型(无对应素材),保持原样。
    /// </summary>
    [HarmonyPatch(typeof(Mortal.Core.PlayerAvatarPanel), "OnEnable")]
    internal static class PlayerAvatarPanelPatch
    {
        internal const string NormalKey = "Assets/__Project/Images/Characters/Player_主角/normal.png";

        private static void Postfix(Mortal.Core.PlayerAvatarPanel __instance)
        {
            try
            {
                // 把序列化的原版立绘实例登记进反查表:庭院常驻立绘等复用同一 Sprite 资产,
                // 登记后看门狗巡检就能覆盖到它们
                Sprite normal = Traverse.Create(__instance).Field("_normal").GetValue<Sprite>();
                if (normal != null)
                {
                    MusclePortraits.RegisterOriginal(NormalKey, normal);
                }
                int dummy = 0;
                ApplyIfActive(__instance, ref dummy);
            }
            catch { }
        }

        internal static void ApplyIfActive(Mortal.Core.PlayerAvatarPanel panel, ref int refreshed)
        {
            if (panel == null || !panel.isActiveAndEnabled) return;
            Traverse traverse = Traverse.Create(panel);
            UnityEngine.UI.Image img = traverse.Field("_avatarImage").GetValue<UnityEngine.UI.Image>();
            Sprite normal = traverse.Field("_normal").GetValue<Sprite>();
            if (img == null || normal == null || img.sprite != normal) return;
            Sprite muscle = MusclePortraits.GetMuscleSpriteIfActive(NormalKey);
            if (muscle == null) return;
            img.sprite = muscle;
            refreshed++;
        }
    }
}
