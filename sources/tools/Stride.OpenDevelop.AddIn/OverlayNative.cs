// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// The native-window operations StrideSceneEditorViewport performs on the SDL window that renders the scene, behind one seam
// so the viewport has one code path for both desktop systems that pin a real native window over a WPF element:
//   macOS   - CocoaOverlayInterop (the SDL NSWindow is an addChildWindow: child of the host NSWindow).
//   Windows - Win32OverlayInterop (the SDL HWND is a borderless popup OWNED by the host window, kept over the element).
// On Windows the process cannot use Stride's own WinForms path: System.Windows.Forms is LibreWinForms there, so a game form's
// Handle is not a real HWND and DXGI refuses a swap chain on it (E_ACCESSDENIED in CreateSwapChainForDesktop). SDL creates a
// genuine HWND, which is what this path relies on.

using System;
using System.Runtime.InteropServices;

namespace ICSharpCode.StrideGameStudio
{
	static class OverlayNative
	{
		public static IntPtr GetNativeWindow(Stride.Graphics.SDL.Window window)
			=> OperatingSystem.IsWindows() ? SdlNativeWindow.GetWin32Hwnd(window) : SdlNativeWindow.GetCocoaNsWindow(window);

		public static void MakeBorderless(IntPtr window)
		{
			if (OperatingSystem.IsWindows()) Win32OverlayInterop.MakeBorderless(window);
			else CocoaOverlayInterop.MakeBorderless(window);
		}

		public static void MakeTitled(IntPtr window)
		{
			if (OperatingSystem.IsWindows()) Win32OverlayInterop.MakeTitled(window);
			else CocoaOverlayInterop.MakeTitled(window);
		}

		/// <summary>macOS lets clicks fall through the overlay to the WPF element (which forwards them); a Windows overlay is a real
		/// window that receives its own input, so this does nothing there.</summary>
		public static void SetIgnoresMouseEvents(IntPtr window, bool value)
		{
			if (OperatingSystem.IsMacOS()) CocoaOverlayInterop.SetIgnoresMouseEvents(window, value);
		}

		public static bool GetIgnoresMouseEvents(IntPtr window)
			=> OperatingSystem.IsMacOS() && CocoaOverlayInterop.GetIgnoresMouseEvents(window);

		public static void AddChildWindow(IntPtr host, IntPtr child)
		{
			if (OperatingSystem.IsWindows()) Win32OverlayInterop.SetOwner(child, host);
			else CocoaOverlayInterop.AddChildWindow(host, child);
		}

		public static void RemoveChildWindow(IntPtr host, IntPtr child)
		{
			if (OperatingSystem.IsWindows()) Win32OverlayInterop.SetOwner(child, IntPtr.Zero);
			else CocoaOverlayInterop.RemoveChildWindow(host, child);
		}

		public static void OrderOut(IntPtr window)
		{
			if (OperatingSystem.IsWindows()) Win32OverlayInterop.Hide(window);
			else CocoaOverlayInterop.OrderOut(window);
		}

		public static void OrderFront(IntPtr window)
		{
			if (OperatingSystem.IsWindows()) Win32OverlayInterop.ShowNoActivate(window);
			else CocoaOverlayInterop.OrderFront(window);
		}

		public static void MakeKey(IntPtr window)
		{
			if (OperatingSystem.IsWindows()) Win32OverlayInterop.Focus(window);
			else CocoaOverlayInterop.MakeKey(window);
		}

		public static bool IsKeyWindow(IntPtr window)
			=> OperatingSystem.IsWindows() ? Win32OverlayInterop.IsForeground(window) : CocoaOverlayInterop.IsKeyWindow(window);

		public static bool IsMainWindow(IntPtr window)
			=> OperatingSystem.IsWindows() ? Win32OverlayInterop.IsForeground(window) : CocoaOverlayInterop.IsMainWindow(window);

		public static long GetWindowNumber(IntPtr window)
			=> OperatingSystem.IsWindows() ? window.ToInt64() : CocoaOverlayInterop.GetWindowNumber(window);

		public static long WindowNumberAtPoint(double x, double y)
			=> OperatingSystem.IsWindows() ? Win32OverlayInterop.WindowAtPoint((int)x, (int)y) : CocoaOverlayInterop.WindowNumberAtPoint(x, y);

		/// <summary>The overlay window's frame. Windows: virtual-screen pixels with a TOP-left origin (macOS: Cocoa points, bottom-left).</summary>
		public static CocoaOverlayInterop.NSRect GetFrame(IntPtr window)
			=> OperatingSystem.IsWindows() ? Win32OverlayInterop.GetFrame(window) : CocoaOverlayInterop.GetFrame(window);
	}

	/// <summary>
	/// Win32 side of <see cref="OverlayNative"/>. All coordinates are physical pixels of the virtual screen, top-left origin.
	/// </summary>
	static class Win32OverlayInterop
	{
		const int GWL_STYLE = -16;
		const int GWL_EXSTYLE = -20;
		const int GWLP_HWNDPARENT = -8;
		const long WS_POPUP = 0x80000000L;
		const long WS_CAPTION = 0x00C00000L;
		const long WS_THICKFRAME = 0x00040000L;
		const long WS_SYSMENU = 0x00080000L;
		const long WS_MINIMIZEBOX = 0x00020000L;
		const long WS_MAXIMIZEBOX = 0x00010000L;
		const long WS_BORDER = 0x00800000L;
		const long WS_DLGFRAME = 0x00400000L;
		const long WS_EX_TOOLWINDOW = 0x00000080L;
		const long WS_EX_APPWINDOW = 0x00040000L;
		const long WS_EX_WINDOWEDGE = 0x00000100L;
		const long WS_EX_CLIENTEDGE = 0x00000200L;
		const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;
		const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;

		[StructLayout(LayoutKind.Sequential)]
		struct RECT { public int Left, Top, Right, Bottom; }

		[StructLayout(LayoutKind.Sequential)]
		struct POINT { public int X, Y; }

		[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
		[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);
		[DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
		[DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
		[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
		[DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
		[DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
		[DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
		[DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hWnd);
		[DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
		[DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
		[DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);

		static long GetLong(IntPtr hWnd, int index) => GetWindowLongPtr(hWnd, index).ToInt64();
		static void SetLong(IntPtr hWnd, int index, long value) => SetWindowLongPtr(hWnd, index, new IntPtr(value));

		public static void MakeBorderless(IntPtr hWnd)
		{
			var style = GetLong(hWnd, GWL_STYLE);
			style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_BORDER | WS_DLGFRAME);
			style |= WS_POPUP;
			SetLong(hWnd, GWL_STYLE, style);
			// A tool window: no taskbar button and no Alt+Tab entry for what is only a surface inside the IDE.
			var ex = GetLong(hWnd, GWL_EXSTYLE);
			ex &= ~(WS_EX_APPWINDOW | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE);
			ex |= WS_EX_TOOLWINDOW;
			SetLong(hWnd, GWL_EXSTYLE, ex);
			SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
		}

		public static void MakeTitled(IntPtr hWnd)
		{
			var style = GetLong(hWnd, GWL_STYLE);
			style &= ~WS_POPUP;
			style |= WS_CAPTION | WS_THICKFRAME | WS_SYSMENU;
			SetLong(hWnd, GWL_STYLE, style);
			SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
		}

		/// <summary>Sets the OWNER (not the parent: GWLP_HWNDPARENT on a popup sets the owner), which keeps the overlay above the host
		/// window, hides it with it and minimises it with it - the Windows counterpart of Cocoa's addChildWindow:.</summary>
		public static void SetOwner(IntPtr hWnd, IntPtr owner) => SetWindowLongPtr(hWnd, GWLP_HWNDPARENT, owner);

		public static bool IsVisible(IntPtr hWnd) => IsWindowVisible(hWnd);

		public static void Hide(IntPtr hWnd) { if (IsWindow(hWnd)) ShowWindow(hWnd, SW_HIDE); }

		public static void ShowNoActivate(IntPtr hWnd) { if (IsWindow(hWnd)) ShowWindow(hWnd, SW_SHOWNOACTIVATE); }

		public static void Focus(IntPtr hWnd) { if (IsWindow(hWnd)) SetFocus(hWnd); }

		public static bool IsForeground(IntPtr hWnd) => hWnd != IntPtr.Zero && GetForegroundWindow() == hWnd;

		public static long WindowAtPoint(int x, int y)
			=> WindowFromPoint(new POINT { X = x, Y = y }).ToInt64();

		/// <summary>Moves and sizes the overlay to a screen rectangle, without activating it or changing its z-order relative to its owner.</summary>
		public static void SetScreenRect(IntPtr hWnd, int x, int y, int width, int height)
			=> SetWindowPos(hWnd, IntPtr.Zero, x, y, Math.Max(1, width), Math.Max(1, height), SWP_NOZORDER | SWP_NOACTIVATE);

		public static CocoaOverlayInterop.NSRect GetFrame(IntPtr hWnd)
			=> GetWindowRect(hWnd, out var r)
				? new CocoaOverlayInterop.NSRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)
				: default;

		/// <summary>The top-left of a window's client area in screen pixels.</summary>
		public static bool TryGetClientOrigin(IntPtr hWnd, out int x, out int y)
		{
			var p = new POINT();
			var ok = hWnd != IntPtr.Zero && ClientToScreen(hWnd, ref p);
			x = p.X;
			y = p.Y;
			return ok;
		}
	}
}
