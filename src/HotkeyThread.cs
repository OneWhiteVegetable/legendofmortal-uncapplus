using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace UncapSixStats
{
    /// <summary>
    /// 后台线程热键监听:用 Win32 GetAsyncKeyState 直接轮询,
    /// 完全不依赖 Unity 的消息循环(本游戏里我们组件的 Update/OnGUI 不会被执行)。
    /// 前台判定用"前台窗口的线程 PID == 游戏 PID",不依赖 MainWindowHandle。
    /// 检测到按键后通过 Unity 同步上下文投递到主线程执行。
    /// </summary>
    internal static class HotkeyThread
    {
        private const int VK_F2 = 0x71;
        private const int VK_F9 = 0x78;
        private const int VK_F10 = 0x79;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static Thread _thread;
        private static int _gamePid;
        private static bool _foregroundLogged;
        private static bool _anyKeyLogged;

        internal static void Start()
        {
            _gamePid = Process.GetCurrentProcess().Id;
            _thread = new Thread(Loop) { IsBackground = true, Name = "UncapSixStats.Hotkeys" };
            _thread.Start();
        }

        private static bool Down(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        // 短按可能落在 25ms 轮询间隔之外(只看到"已弹起"),补看 0x0001 位:
        // 该位表示"上次调用本函数以来被按过",正好覆盖轮询间隙
        private static bool Pressed(int vk)
        {
            short state = GetAsyncKeyState(vk);
            return (state & 0x0001) != 0;
        }

        private static bool GameInForeground()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            uint pid;
            GetWindowThreadProcessId(foreground, out pid);
            return pid == _gamePid;
        }

        private static void Loop()
        {
            bool prevF2 = false;
            bool prevF10 = false;
            bool prevF9 = false;
            while (true)
            {
                try
                {
                    if (GameInForeground())
                    {
                        if (!_foregroundLogged)
                        {
                            _foregroundLogged = true;
                            Plugin.Log.LogInfo("[UncapSixStats] 热键线程前台检测通过(线程存活)");
                        }
                        bool f2 = Down(VK_F2);
                        if ((Pressed(VK_F2) || (f2 && !prevF2)))
                        {
                            LogFirstDetection("F2");
                            Plugin.PostToMain(ModPanelUI.Toggle);
                        }
                        prevF2 = f2;

                        bool f10 = Down(VK_F10);
                        if ((Pressed(VK_F10) || (f10 && !prevF10)))
                        {
                            LogFirstDetection("F10");
                            Plugin.PostToMain(() => Plugin.Instance.DumpDiagnosticsPublic());
                        }
                        prevF10 = f10;

                        if (Plugin.DebugHotkey.Value)
                        {
                            bool f9 = Down(VK_F9);
                            if ((Pressed(VK_F9) || (f9 && !prevF9)))
                            {
                                LogFirstDetection("F9");
                                Plugin.PostToMain(() => Plugin.Instance.AddStatPublic(Mortal.Core.GameStatType.武功拳掌, 10));
                            }
                            prevF9 = f9;
                        }
                    }
                    else
                    {
                        prevF2 = prevF10 = prevF9 = false;
                    }
                }
                catch { }
                Thread.Sleep(25);
            }
        }

        private static void LogFirstDetection(string keyName)
        {
            if (_anyKeyLogged) return;
            _anyKeyLogged = true;
            Plugin.Log.LogInfo("[UncapSixStats] 热键线程已检测到按键:" + keyName);
        }
    }
}
