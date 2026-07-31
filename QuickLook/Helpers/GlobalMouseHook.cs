// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLook program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Common.NativeMethods;
using System;

namespace QuickLook.Helpers;

internal sealed class GlobalMouseHook : IDisposable
{
    private User32.MouseHookProc _callback;
    private nint _hook;

    internal GlobalMouseHook()
    {
        _callback = HookProc;
        var module = Kernel32.LoadLibrary("user32.dll");
        _hook = User32.SetWindowsHookEx(User32.WH_MOUSE_LL, _callback, module, 0);
    }

    internal event EventHandler MiddleButtonDown;

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (_hook != IntPtr.Zero)
        {
            User32.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        _callback = null;
    }

    private int HookProc(int code, int wParam, ref User32.MouseHookStruct data)
    {
        if (code >= 0 && wParam == User32.WM_MBUTTONDOWN)
            MiddleButtonDown?.Invoke(this, EventArgs.Empty);

        return User32.CallNextHookEx(_hook, code, wParam, ref data);
    }
}
