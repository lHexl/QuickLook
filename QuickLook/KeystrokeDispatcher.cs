// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLook program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using QuickLook.Common.Helpers;
using QuickLook.Common.NativeMethods;
using QuickLook.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace QuickLook;

internal class KeystrokeDispatcher : IDisposable
{
    private static KeystrokeDispatcher _instance;

    private static HashSet<Keys> _validKeys;

    private GlobalKeyboardHook _hook;
    private GlobalMouseHook _mouseHook;
    private nint _winEventHook;
    private User32.WinEventProc _winEventProc; // keep reference to prevent GC
    private bool _isPreviewRequest;
    private bool _isMousePreviewRequest;
    private bool _middleButtonIsDown;
    private long _middleButtonHoldTick;
    private long _lastInvalidKeyPressTick;

    private const long HOLD_TO_PREVIEW_DURATION = TimeSpan.TicksPerMillisecond * 750;
    private const long VALID_KEY_PRESS_DELAY = TimeSpan.TicksPerSecond * 1;

    protected KeystrokeDispatcher()
    {
        InstallKeyHook(KeyDownEventHandler, KeyUpEventHandler);
        InstallMouseHook();
        InstallForegroundWindowHook();

        _validKeys =
        [
            Keys.Up, Keys.Down, Keys.Left, Keys.Right,
            Keys.Enter, Keys.Escape,
            Keys.F5, Keys.F11,
        ];
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _hook = null;

        _mouseHook?.Dispose();
        _mouseHook = null;

        if (_winEventHook != IntPtr.Zero)
        {
            User32.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
    }

    private void KeyDownEventHandler(object sender, KeyEventArgs e)
    {
        CallViewWindowManagerInvokeRoutine(e, true);
    }

    private void KeyUpEventHandler(object sender, KeyEventArgs e)
    {
        CallViewWindowManagerInvokeRoutine(e, false);
    }

    private void CallViewWindowManagerInvokeRoutine(KeyEventArgs e, bool isKeyDown)
    {
        // Space is no longer a QuickLook command. Let it pass through without
        // treating it as an invalid key that temporarily suppresses navigation.
        if (e.KeyCode == Keys.Space)
            return;

        // skip invalid keys, but record the timestamp
        if (!_validKeys.Contains(e.KeyCode))
        {
            Debug.WriteLine($"Invalid keypress: key={e.KeyCode},down={isKeyDown}, time={_lastInvalidKeyPressTick}");
            _lastInvalidKeyPressTick = DateTime.Now.Ticks;
            return;
        }

        // skip valid keys when modifiers are used
        if (isKeyDown && e.Modifiers != Keys.None)
            return;

        // skip if key is valid but too close after pressing an invalid key
        if (DateTime.Now.Ticks - _lastInvalidKeyPressTick < VALID_KEY_PRESS_DELAY)
            return;
        _lastInvalidKeyPressTick = 0L;

        // check if the valid key is a preview request
        if (isKeyDown)
        {
            _isPreviewRequest = NativeMethods.QuickLook.GetFocusedWindowType() !=
                                NativeMethods.QuickLook.FocusedWindowType.Invalid;
            _isPreviewRequest |= WindowHelper.IsForegroundWindowBelongToSelf();
        } // else (when isKeyDown is false), _isPreviewRequest retain its current state

        // Call InvokeRoutine only when the key was pressed in a valid window.
        if (_isPreviewRequest)
            InvokeRoutine(e.KeyCode, isKeyDown);

        // when the key has been released, reset variables
        if (!isKeyDown)
            _isPreviewRequest = false;
    }

    private void InvokeRoutine(Keys key, bool isKeyDown)
    {
        Debug.WriteLine($"InvokeRoutine: key={key},down={isKeyDown}");

        if (isKeyDown)
        {
            switch (key)
            {
                case Keys.Enter:
                    PipeServerManager.SendMessage(PipeMessages.RunAndClose);
                    break;

                case Keys.F5:
                    PipeServerManager.SendMessage(PipeMessages.Reload);
                    break;

                case Keys.F11:
                    PipeServerManager.SendMessage(PipeMessages.Fullscreen);
                    break;
            }
        }
        else
        {
            switch (key)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.Left:
                case Keys.Right:
                    PipeServerManager.SendMessage(PipeMessages.Switch);
                    break;

                case Keys.Escape:
                    PipeServerManager.SendMessage(PipeMessages.Close);
                    break;

            }
        }
    }

    private void InstallMouseHook()
    {
        _mouseHook = new GlobalMouseHook();
        _mouseHook.MiddleButtonDown += MiddleButtonDownEventHandler;
        _mouseHook.MiddleButtonUp += MiddleButtonUpEventHandler;
    }

    private void MiddleButtonDownEventHandler(object sender, EventArgs e)
    {
        PreviewPerformanceLogger.WriteGlobal("Input.MiddleButtonDown.Received");
        if (_middleButtonIsDown)
        {
            PreviewPerformanceLogger.WriteGlobal("Input.MiddleButtonDown.Ignored", "reason=alreadyDown");
            return;
        }

        _middleButtonHoldTick = DateTime.Now.Ticks;
        _isMousePreviewRequest = NativeMethods.QuickLook.GetFocusedWindowType() !=
                                 NativeMethods.QuickLook.FocusedWindowType.Invalid;
        _isMousePreviewRequest |= WindowHelper.IsForegroundWindowBelongToSelf();

        if (_isMousePreviewRequest)
        {
            PreviewPerformanceLogger.WriteGlobal("Input.MiddleButtonDown.ToggleSending");
            PipeServerManager.SendMessage(PipeMessages.Toggle);
            _middleButtonIsDown = true;
            PreviewPerformanceLogger.WriteGlobal("Input.MiddleButtonDown.ToggleSent");
        }
        else
            PreviewPerformanceLogger.WriteGlobal("Input.MiddleButtonDown.Ignored", "reason=invalidForegroundWindow");
    }

    private void MiddleButtonUpEventHandler(object sender, EventArgs e)
    {
        if (_isMousePreviewRequest && _middleButtonIsDown &&
            DateTime.Now.Ticks - _middleButtonHoldTick >= HOLD_TO_PREVIEW_DURATION &&
            SettingHelper.Get("AutoCloseHolding", true, "QuickLook"))
        {
            PipeServerManager.SendMessage(PipeMessages.Toggle);
        }

        _isMousePreviewRequest = false;
        _middleButtonIsDown = false;
    }

    private void InstallKeyHook(KeyEventHandler downHandler, KeyEventHandler upHandler)
    {
        _hook = GlobalKeyboardHook.GetInstance();

        _hook.KeyDown += downHandler;
        _hook.KeyUp += upHandler;
    }

    private void InstallForegroundWindowHook()
    {
        // When the foreground window changes (e.g. via Alt+Tab), reset the invalid-key
        // Delay reset lets the first valid command in the newly focused Explorer window work.
        // https://github.com/QL-Win/QuickLook/issues/1939
        _winEventProc = OnForegroundWindowChanged;
        _winEventHook = User32.SetWinEventHook(
            User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc,
            0, 0, User32.WINEVENT_OUTOFCONTEXT);
    }

    private void OnForegroundWindowChanged(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        // A different window is now in the foreground. Any invalid key presses that happened
        // before this switch (e.g. Alt+Tab keystrokes) belong to the previous context,
        // so they must not suppress valid keys in the new window.
        _lastInvalidKeyPressTick = 0L;

#if false // The problem of requiring two spaces has been solved -- comment the test code first.
        Debug.WriteLine($"Foreground window changed to {hwnd:X}, invalid-key delay cleared.");
#endif
    }

    internal static KeystrokeDispatcher GetInstance()
    {
        return _instance ??= new KeystrokeDispatcher();
    }
}
