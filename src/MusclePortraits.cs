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
    /// 把屏幕上显示的主角立绘换成 GigaMuscle 目录下的同名肌肉版;条件不满足(或开关关闭)时还原原版。
    /// 只加文件不改游戏任何原始资源;判定在每次立绘赋值时现场进行,数值变化后下一张立绘即生效。
    ///
    /// 实现路径(纯显示层,不碰 Addressables 加载):
    /// 1) Image.sprite 赋值拦截(ImageSpriteAssignPatch):已登记的原版立绘 ↔ 肌肉版双向即时换图;
    ///    首次见到的原版立绘按归属界面(剧情角色 holder / 战斗状态面板)反查 key 即时登记,无闪现;
    /// 2) Fungus 一致性补丁:GetPortrait 返回当前显示版本(防同姿势重显误隐藏),
    ///    SetPortraitImageBySprite 查找前归一 sprite 版本(防 portraitImage=null 引发 NRE 卡死剧情);
    /// 3) 看门狗巡检(PortraitSyncWatcher,0.5s)兜底:纠正被游戏动画/入场逻辑重设的立绘 + 补充登记;
    /// 4) 庭院常驻立绘(路径 [UI]/MainUI/Layer_1/Avatar)与 PlayerAvatarPanel 的序列化立绘走自学习登记。
    ///
    /// ⚠️ 历史教训:曾经用 Harmony 补丁拦截 Addressables.LoadAssetAsync&lt;Sprite&gt;(object) 来做加载期替换,
    /// 但 Mono 对所有引用类型 T 的泛型方法共享机器码,Harmony 补丁会把 T 烘焙成 Sprite,
    /// 导致 LoadAssetAsync&lt;RuntimeAnimatorController&gt;(GUID) 等调用全部被劫持成按 Sprite 加载
    /// → 团战全部单位动画控制器加载失败(InvalidKeyException)、白色方块、动画机死亡、技能卡死。
    /// 因此:绝不补丁任何泛型方法的单个实例;加载一律放行原版,替换只发生在显示层。
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
        // 原版立绘实例 → key(由看门狗从游戏数据结构中登记),用于破百瞬间定位屏幕上显示中的原版立绘
        private static readonly Dictionary<Sprite, string> _originalSprites = new Dictionary<Sprite, string>();
        // key → 原版立绘实例(还原时优先直接用,避免 Addressables 同步加载)
        private static readonly Dictionary<string, Sprite> _originalByKey = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private static bool _ready;

        // 剧情角色控制器类型(Mortal.Story.StoryCharacterController,反射缓存,不引用程序集)
        private static System.Type _storyCtrlType;
        private static bool _storyCtrlSearched;

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
            // 首次见到的原版立绘:先按归属界面(剧情角色/战斗头像)反查 key 登记,
            // 登记成功立刻就能换图 —— 不等看门狗巡检,消除"先闪原版再变肌肉"的延迟
            if (SwitchOn && !_reverse.ContainsKey(value) && !_originalSprites.ContainsKey(value))
            {
                LazyRegisterFromContext(img, value);
            }
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

        /// <summary>
        /// 按 Image 的归属界面反查 Addressables key 并登记原版立绘(纯读取游戏数据,不碰加载):
        /// 剧情立绘 Image 的父物体是 "{角色名} holder"(Fungus 约定),按名字找到
        /// StoryCharacterController 后用它 Data 里的立绘映射表精确对位;
        /// 战斗状态面板头像则取 CombatCharacterStatusUI 的 AvatarAddressKey。
        /// </summary>
        private static void LazyRegisterFromContext(UnityEngine.UI.Image img, Sprite sprite)
        {
            try
            {
                Transform t = img.transform;
                while (t != null)
                {
                    if (TryRegisterCombatAvatar(t, sprite)) return;
                    string n = t.name;
                    if (n.EndsWith(" holder", StringComparison.Ordinal))
                    {
                        string charName = n.Substring(0, n.Length - " holder".Length);
                        if (TryRegisterStoryPortrait(charName, sprite)) return;
                    }
                    t = t.parent;
                }
            }
            catch { }
        }

        // 剧情立绘:角色名下立绘映射表中,文件名与 sprite 同名且 GigaMuscle 有对应文件的那个 key
        private static bool TryRegisterStoryPortrait(string charName, Sprite sprite)
        {
            Component ctrl = FindStoryController(charName);
            if (ctrl == null) return false;
            return RegisterFromStoryController(ctrl, sprite);
        }

        private static Component FindStoryController(string gameObjectName)
        {
            EnsureStoryCtrlType();
            if (_storyCtrlType == null) return null;
            foreach (UnityEngine.Object obj in Resources.FindObjectsOfTypeAll(_storyCtrlType))
            {
                Component c = obj as Component;
                if (c != null && c.gameObject.name == gameObjectName) return c;
            }
            return null;
        }

        private static void EnsureStoryCtrlType()
        {
            if (_storyCtrlSearched) return;
            _storyCtrlSearched = true;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "Mortal.Story")
                {
                    _storyCtrlType = asm.GetType("Mortal.Story.StoryCharacterController");
                    break;
                }
            }
        }

        // 从单个剧情角色控制器登记:立绘 key 的文件名 = sprite 名(游戏 LoadPortrait 的构造方式),
        // 在角色自己的映射表范围内按文件名对位,不会撞衫。
        // spriteFilter 非 null 时只登记这个实例(赋值拦截时的即时登记)。
        // 注意:同一 key 的原版 Sprite 可能有多个实例(Addressables 加载 / 序列化引用各一份),
        // 必须按实例登记,不能按 key 去重,否则剧情里的立绘实例永远登记不上。
        private static bool RegisterFromStoryController(Component ctrl, Sprite spriteFilter)
        {
            bool registered = false;
            object data = ReadMember(ctrl, "Data");
            System.Collections.IEnumerable resourceList = ReadMember(data, "PortraitResourceList") as System.Collections.IEnumerable;
            if (resourceList == null) return false;
            List<Sprite> portraits = ReadMember(ctrl, "portraits") as List<Sprite>;
            if (portraits == null) return false;
            foreach (object item in resourceList)
            {
                string addressKey = ReadMember(item, "AddressKey") as string;
                if (string.IsNullOrEmpty(addressKey) || !_availableKeys.Contains(addressKey)) continue;
                string fileName = Path.GetFileNameWithoutExtension(addressKey);
                Sprite sprite = portraits.Find(s => s != null && string.Equals(s.name, fileName, StringComparison.OrdinalIgnoreCase));
                if (sprite == null) continue;
                if (spriteFilter != null && sprite != spriteFilter) continue;
                if (_originalSprites.ContainsKey(sprite)) continue;
                RegisterOriginal(addressKey, sprite);
                registered = true;
            }
            return registered;
        }

        // 战斗状态面板头像:CombatCharacterStatusUI._avatar + AvatarAddressKey
        private static bool TryRegisterCombatAvatar(Transform t, Sprite sprite)
        {
            Mortal.Combat.CombatCharacterStatusUI ui = t.GetComponent<Mortal.Combat.CombatCharacterStatusUI>();
            if (ui == null) return false;
            string key = GetCombatAvatarKey(ui);
            if (string.IsNullOrEmpty(key) || !_availableKeys.Contains(key)) return false;
            if (!string.Equals(Path.GetFileNameWithoutExtension(key), sprite.name, StringComparison.OrdinalIgnoreCase)) return false;
            if (_originalSprites.ContainsKey(sprite)) return true;
            RegisterOriginal(key, sprite);
            return true;
        }

        private static string GetCombatAvatarKey(Mortal.Combat.CombatCharacterStatusUI ui)
        {
            object controller = ReadMember(ui, "_actionController");
            object stat = ReadMember(controller, "Stat");
            object statData = ReadMember(stat, "Data");
            return ReadMember(statData, "AvatarAddressKey") as string;
        }

        /// <summary>登记一次原版立绘加载(仅 GigaMuscle 有对应文件的 key),供破百瞬间反查。</summary>
        internal static void RegisterOriginal(string key, Sprite sprite)
        {
            if (sprite == null) return;
            if (_reverse.ContainsKey(sprite)) return; // 我们自己的肌肉图不算
            _originalSprites[sprite] = key;
            _originalByKey[key] = sprite;
        }

        /// <summary>条件满足时返回指定 key 的肌肉版 Sprite,否则 null。供不走 Addressables 的序列化立绘面板使用。</summary>
        internal static Sprite GetMuscleSpriteIfActive(string key)
        {
            if (!_ready || !SwitchOn) return null;
            if (!_availableKeys.Contains(key)) return null;
            if (!IsActiveNow()) return null;
            return LoadCached(key);
        }

        /// <summary>已登记的原版立绘 → 当前应显示版本(条件满足时为肌肉版,否则 null)。供 Fungus GetPortrait 补丁用。</summary>
        internal static Sprite GetMuscleSpriteForOriginal(Sprite original)
        {
            if (!_ready || original == null) return null;
            if (!IsActiveNow()) return null;
            if (!_originalSprites.TryGetValue(original, out string key)) return null;
            return LoadCached(key);
        }

        /// <summary>
        /// Fungus PortraitState.SetPortraitImageBySprite 的防 NRE 修正:
        /// 立绘在显示层被换成肌肉版后,Image.sprite 与 portraits 列表里的原版实例不再相等,
        /// 原版按 sprite 引用查找会找不到 → portraitImage=null → Show 下一行取 .rectTransform 直接 NRE,
        /// Lua 剧情协程随之死掉(对话卡死)。这里在查找前把 sprite 归一到当前实际显示的版本
        /// (双向:原版↔肌肉),两个方向都兜底,翻转过渡期也不会找不到。
        /// </summary>
        internal static void FixPortraitLookup(object portraitState, ref Sprite sprite)
        {
            if (!_ready || sprite == null || portraitState == null) return;
            List<UnityEngine.UI.Image> all = Traverse.Create(portraitState).Field("allPortraits").GetValue<List<UnityEngine.UI.Image>>();
            if (all == null) return;
            Sprite wanted = sprite; // ref 参数不能进 lambda
            if (all.Exists(x => x != null && x.sprite == wanted)) return;
            if (_originalSprites.TryGetValue(sprite, out string key))
            {
                Sprite muscle = LoadCached(key);
                if (muscle != null && all.Exists(x => x != null && x.sprite == muscle))
                {
                    sprite = muscle;
                    return;
                }
            }
            if (_reverse.TryGetValue(sprite, out string originalKey)
                && _originalByKey.TryGetValue(originalKey, out Sprite original) && original != null
                && all.Exists(x => x != null && x.sprite == original))
            {
                sprite = original;
            }
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

        // 反射读取字段或属性(沿继承链向上找),取不到返回 null
        private static object ReadMember(object obj, string name)
        {
            if (obj == null) return null;
            System.Type type = obj.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null) return field.GetValue(obj);
                PropertyInfo prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null && prop.CanRead) return prop.GetValue(obj, null);
                type = type.BaseType;
            }
            return null;
        }

        /// <summary>
        /// 剧情立绘登记(看门狗兜底):遍历所有剧情角色控制器,逐个做立绘名→key 精确对位登记。
        /// 即时登记已在 Image.sprite 赋值拦截里按归属界面完成(LazyRegisterFromContext),
        /// 这里只是兜底(比如某些未经赋值拦截的路径),0.5s 一轮,纯读不写。
        /// </summary>
        private static void RegisterFromStoryControllers()
        {
            try
            {
                EnsureStoryCtrlType();
                if (_storyCtrlType == null) return;
                foreach (UnityEngine.Object obj in Resources.FindObjectsOfTypeAll(_storyCtrlType))
                {
                    try
                    {
                        Component ctrl = obj as Component;
                        if (ctrl != null)
                        {
                            RegisterFromStoryController(ctrl, null);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// 战斗状态面板头像登记(看门狗兜底):CombatCharacterStatusUI.Setup 用 AvatarAddressKey
        /// 加载头像赋给 _avatar(Image)。从面板实例反查 key 与 sprite 精确登记。
        /// </summary>
        private static void RegisterFromCombatStatusUI()
        {
            try
            {
                foreach (Mortal.Combat.CombatCharacterStatusUI ui in Resources.FindObjectsOfTypeAll<Mortal.Combat.CombatCharacterStatusUI>())
                {
                    try
                    {
                        UnityEngine.UI.Image avatar = ReadMember(ui, "_avatar") as UnityEngine.UI.Image;
                        if (avatar == null || avatar.sprite == null) continue;
                        if (_reverse.ContainsKey(avatar.sprite) || _originalSprites.ContainsKey(avatar.sprite)) continue;
                        string key = GetCombatAvatarKey(ui);
                        if (!string.IsNullOrEmpty(key) && _availableKeys.Contains(key))
                        {
                            RegisterOriginal(key, avatar.sprite);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// 看门狗式同步(由 PortraitSyncWatcher 低频调用):有些界面(如庭院常驻立绘)会被
        /// 游戏自己的动画/入场逻辑在 OnEnable 之后重新赋值,一次性的翻转刷新盖不住,
        /// 这里持续巡检所有显示中的 Image,把状态不对的立绘纠正到当前条件应有的版本。
        /// 巡检前先跑一轮"原版立绘登记":从剧情角色控制器/战斗状态面板的数据结构里
        /// 反查 Addressables key(加载期不拦截,key 只能在显示层事后登记)。
        /// </summary>
        internal static void SyncDisplayedPortraits()
        {
            if (!_ready) return;
            if (SwitchOn)
            {
                RegisterFromStoryControllers();
                RegisterFromCombatStatusUI();
            }
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

    /// <summary>
    /// Fungus.Character.GetPortrait(string) postfix:肌肉激活且该立绘已登记时,返回肌肉版 sprite,
    /// 让 PortraitOptions.portrait 与屏幕上 Image 实际显示的 sprite 保持同一实例。
    /// 否则 State.portrait(实时显示的肌肉版)与 options.portrait(原版)永远不相等,
    /// 同姿势重显会误触发 HidePortrait 把当前立绘隐藏掉(闪立绘/立绘消失)。
    /// 非泛型方法,按名定位,不引用 Fungus 程序集。
    /// </summary>
    [HarmonyPatch]
    internal static class FungusGetPortraitPatch
    {
        private static MethodBase TargetMethod()
        {
            System.Type type = System.Type.GetType("Fungus.Character, Fungus");
            if (type == null)
            {
                Plugin.Log.LogWarning("[Muscle] 未找到 Fungus.Character,GetPortrait 补丁未应用");
                return null;
            }
            return type.GetMethod("GetPortrait", BindingFlags.Public | BindingFlags.Instance, null, new System.Type[] { typeof(string) }, null);
        }

        private static void Postfix(ref Sprite __result)
        {
            try
            {
                if (__result == null) return;
                Sprite muscle = MusclePortraits.GetMuscleSpriteForOriginal(__result);
                if (muscle != null)
                {
                    __result = muscle;
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Fungus.PortraitState.SetPortraitImageBySprite(Sprite) prefix:
    /// 原版实现是 allPortraits.Find(x => x.sprite == sprite),找不到就 portraitImage=null,
    /// 紧接着 Show() 里取 portraitImage.rectTransform 直接 NRE,Lua 剧情协程随之死掉(对话卡死)。
    /// 立绘在显示层被换过(原版↔肌肉)时引用对不上,这里在查找前把 sprite 归一到实际显示版本。
    /// </summary>
    [HarmonyPatch]
    internal static class FungusSetPortraitImagePatch
    {
        private static MethodBase TargetMethod()
        {
            System.Type type = System.Type.GetType("Fungus.PortraitState, Fungus");
            if (type == null)
            {
                Plugin.Log.LogWarning("[Muscle] 未找到 Fungus.PortraitState,SetPortraitImageBySprite 补丁未应用");
                return null;
            }
            return type.GetMethod("SetPortraitImageBySprite", BindingFlags.Public | BindingFlags.Instance, null, new System.Type[] { typeof(Sprite) }, null);
        }

        private static void Prefix(object __instance, ref Sprite portrait)
        {
            try
            {
                MusclePortraits.FixPortraitLookup(__instance, ref portrait);
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
