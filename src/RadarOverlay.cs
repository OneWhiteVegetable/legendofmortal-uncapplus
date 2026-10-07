using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UI.Extensions;

namespace UncapSixStats
{
    /// <summary>
    /// 雷达图"破限层":克隆三个银白灰色调的多边形垫到面板最底层,
    /// 只显示超出上限的区域,顶点位置固定不动,靠三层相位错开的透明度波
    /// 营造"内部空间在流动"的微光效果,且绝不遮挡文字和其它 UI。
    /// </summary>
    internal static class RadarOverlay
    {
        internal const int LayerCount = 3;

        // 三层亮银色调:银灰 / 亮银 / 荧光银白
        private static readonly float[] LayerTones = { 0.82f, 0.92f, 1.0f };

        internal static string LayerName(int index)
        {
            return "UncapRadarOverlay_" + index;
        }

        internal static RadarOverlayPulse[] EnsureAll(UIPolygon original)
        {
            if (original == null) return null;
            Transform parent = original.transform.parent;
            if (parent == null) return null;
            RadarOverlayPulse[] pulses = new RadarOverlayPulse[LayerCount];
            for (int i = 0; i < LayerCount; i++)
            {
                Transform found = parent.Find(LayerName(i));
                UIPolygon overlay = found != null ? found.GetComponent<UIPolygon>() : null;
                if (overlay == null)
                {
                    GameObject clone = Object.Instantiate(original.gameObject, parent);
                    clone.name = LayerName(i);
                    // 清掉克隆体携带的子物体(顶点文字标签等),破限层只保留多边形本身
                    for (int c = clone.transform.childCount - 1; c >= 0; c--)
                    {
                        Object.Destroy(clone.transform.GetChild(c).gameObject);
                    }
                    // 垫到同层级最底部:背景之上、白色六边形和文字之下,顶得再远也遮不住任何东西
                    clone.transform.SetAsFirstSibling();
                    overlay = clone.GetComponent<UIPolygon>();
                    if (overlay == null)
                    {
                        Object.Destroy(clone);
                        continue;
                    }
                    overlay.raycastTarget = false;
                    RadarOverlayPulse pulse = clone.AddComponent<RadarOverlayPulse>();
                    pulse.layerIndex = i;
                    pulse.tone = LayerTones[i];
                }
                pulses[i] = overlay.GetComponent<RadarOverlayPulse>();
            }
            return pulses;
        }

        /// <summary>只查不建:总开关关闭时用来把已存在的破限层藏起来。</summary>
        internal static RadarOverlayPulse[] FindAll(UIPolygon original)
        {
            if (original == null || original.transform.parent == null) return null;
            Transform parent = original.transform.parent;
            RadarOverlayPulse[] pulses = new RadarOverlayPulse[LayerCount];
            bool any = false;
            for (int i = 0; i < LayerCount; i++)
            {
                Transform found = parent.Find(LayerName(i));
                if (found != null)
                {
                    pulses[i] = found.GetComponent<RadarOverlayPulse>();
                    any = true;
                }
            }
            return any ? pulses : null;
        }

        internal static void SetBase(RadarOverlayPulse[] pulses, int index, float distance)
        {
            if (pulses == null) return;
            foreach (RadarOverlayPulse pulse in pulses)
            {
                if (pulse != null)
                {
                    pulse.SetBase(index, distance);
                }
            }
        }

        internal static void SetOverCap(RadarOverlayPulse[] pulses, bool overCap)
        {
            if (pulses == null) return;
            foreach (RadarOverlayPulse pulse in pulses)
            {
                if (pulse != null)
                {
                    pulse.overCap = overCap;
                }
            }
        }
    }

    /// <summary>
    /// 单层破限层控制:几何形状固定(SetBase 时一次写死,顶点不动不摆动),
    /// 每层按自身相位做透明度波动,三层叠加形成流动虚幻的微光。
    /// 无破限时完全隐藏(保持原版观感)。
    /// </summary>
    internal class RadarOverlayPulse : MonoBehaviour
    {
        internal bool overCap;
        internal int layerIndex;
        internal float tone = 1f;

        private readonly float[] _baseDistances = new float[8];
        private Graphic _graphic;
        private UIPolygon _polygon;

        private void Awake()
        {
            _graphic = GetComponent<Graphic>();
            _polygon = GetComponent<UIPolygon>();
        }

        internal void SetBase(int index, float distance)
        {
            if (index < 0 || index >= _baseDistances.Length) return;
            _baseDistances[index] = distance;
            if (_polygon != null && index < _polygon.VerticesDistances.Length)
            {
                _polygon.VerticesDistances[index] = distance;
                _polygon.SetVerticesDirty();
            }
        }

        private void Update()
        {
            if (_graphic == null) return;
            float alpha = Plugin.OverlayAlpha.Value;
            if (!overCap)
            {
                alpha = 0f;
            }
            else if (Plugin.OverlayPulse.Value)
            {
                // 荧光闪闪:正弦取正后六次方压出尖锐亮点(快速亮起-缓慢熄灭),
                // 三层相位错开形成此起彼伏的闪烁流光;再叠一层高频微颤模拟星点
                float time = Time.unscaledTime;
                float speed = Plugin.OverlayPulseSpeed.Value;
                float phase = layerIndex * 2.094f;
                float s = Mathf.Max(Mathf.Sin(time * speed + phase), 0f);
                float glint = Mathf.Pow(s, 6f);
                float wave = 0.18f + 0.82f * glint + 0.06f * Mathf.Sin(time * speed * 5.3f + phase * 2.3f);
                alpha *= wave;
            }
            else
            {
                alpha *= 0.45f;
            }
            Color color = Plugin.OverlayColorValue * tone;
            color.a = Mathf.Clamp01(alpha);
            if (_graphic.color != color)
            {
                _graphic.color = color;
            }
        }
    }
}
