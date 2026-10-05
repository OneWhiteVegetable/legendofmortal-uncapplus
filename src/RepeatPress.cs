using UnityEngine;
using UnityEngine.EventSystems;

namespace UncapSixStats
{
    /// <summary>
    /// 长按连点:按下立即触发一次,按住 0.4s 后开始连发。
    /// 挂在游戏自己的按钮物体上(组件消息循环正常),替代 Button.onClick 的抬起触发。
    /// </summary>
    internal class RepeatPress : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        internal System.Action Action;

        private const float FirstDelay = 0.4f;
        private const float Interval = 0.07f;

        private bool _held;
        private float _timer;
        private float _next;

        public void OnPointerDown(PointerEventData eventData)
        {
            _held = true;
            _timer = 0f;
            _next = FirstDelay;
            if (Action != null) Action();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _held = false;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _held = false;
        }

        private void Update()
        {
            if (!_held) return;
            _timer += Time.unscaledDeltaTime;
            while (_timer >= _next)
            {
                _next += Interval;
                if (Action != null) Action();
            }
        }

        private void OnDisable()
        {
            _held = false;
        }
    }
}
