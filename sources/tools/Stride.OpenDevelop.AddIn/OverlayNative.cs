// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// The native-window operations StrideSceneEditorViewport performs on the SDL window that renders the scene, behind one seam
// so the viewport has one code path for the desktop systems that pin a real native window over a WPF element:
//   macOS   - CocoaOverlayInterop (the SDL NSWindow is an addChildWindow: child of the host NSWindow).
//   Windows - Win32OverlayInterop (the SDL HWND is a borderless popup OWNED by the host window, kept over the element).
//   Linux   - LinuxOverlayInterop (X11: the SDL window is a borderless window TRANSIENT for the host window, kept over the element; SDL
//             itself moves, sizes, shows and hides it, and feeds the game its input natively).
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
			=> OperatingSystem.IsWindows() ? SdlNativeWindow.GetWin32Hwnd(window)
				: OperatingSystem.IsLinux() ? window.SdlHandle // the SDL_Window*: Linux drives the overlay through SDL's own API
				: SdlNativeWindow.GetCocoaNsWindow(window);

		public static void MakeBorderless(IntPtr window)
		{
			if (OperatingSystem.IsWindows()) Win32OverlayInterop.MakeBorderless(window);
			else if (OperatingSystem.IsLinux()) LinuxOverlayInterop.SetBordered(window, false);
			else CocoaOverlayInterop.MakeBorderless(window);
		}

		public static void MakeTitled(IntPtr window)
		{
			if (OperatingSystem.IsWindows()) Win32OverlayInterop.MakeTitled(window);
			else if (OperatingSystem.IsLinux()) LinuxOverlayInterop.SetBordered(window, true);
			else CocoaOverlayInterop.MakeTitled(window);
		}

		/// <summary>macOS lets clicks fall through the overlay to the WPF element (which forwards them); a Windows or Linux overlay is a real
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
			else if (OperatingSystem.IsLinux()) LinuxOverlayInterop.SetTransientFor(child, host);
			else CocoaOverlayInterop.AddChildWindow(host, child);
		}

		public static void RemoveChildWindow(IntPtr host, IntPtr child)
		{
			if (OperatingSystem.IsWindows()) Win32OverlayInterop.SetOwner(child, IntPtr.Zero);
			else if (OperatingSystem.IsLinux()) LinuxOverlayInterop.SetTransientFor(child, IntPtr.Zero);
			else CocoaOverlayInterop.RemoveChildWindow(host, child);
		}

		public static void OrderOut(IntPtr window)
		{
			if (OperatingSystem.IsWindows()) Win32OverlayInterop.Hide(window);
			else if (OperatingSystem.IsLinux()) LinuxOverlayInterop.Hide(window);
			else CocoaOverlayInterop.OrderOut(window);
		}

		public static void OrderFront(IntPtr window)
		{
			if (OperatingSystem.IsWindows()) Win32OverlayInterop.ShowNoActivate(window);
			else if (OperatingSystem.IsLinux()) LinuxOverlayInterop.Show(window);
			else CocoaOverlayInterop.OrderFront(window);
		}

		public static void MakeKey(IntPtr window)
		{
			if (OperatingSystem.IsWindows()) Win32OverlayInterop.Focus(window);
			else if (OperatingSystem.IsLinux()) LinuxOverlayInterop.Focus(window);
			else CocoaOverlayInterop.MakeKey(window);
		}

		public static bool IsKeyWindow(IntPtr window)
			=> OperatingSystem.IsWindows() ? Win32OverlayInterop.IsForeground(window)
				: OperatingSystem.IsLinux() ? LinuxOverlayInterop.HasInputFocus(window)
				: CocoaOverlayInterop.IsKeyWindow(window);

		public static bool IsMainWindow(IntPtr window)
			=> OperatingSystem.IsWindows() ? Win32OverlayInterop.IsForeground(window)
				: OperatingSystem.IsLinux() ? LinuxOverlayInterop.HasInputFocus(window)
				: CocoaOverlayInterop.IsMainWindow(window);

		public static long GetWindowNumber(IntPtr window)
			=> OperatingSystem.IsWindows() ? window.ToInt64()
				: OperatingSystem.IsLinux() ? LinuxOverlayInterop.NativeId(window)
				: CocoaOverlayInterop.GetWindowNumber(window);

		public static long WindowNumberAtPoint(double x, double y)
			=> OperatingSystem.IsWindows() ? Win32OverlayInterop.WindowAtPoint((int)x, (int)y)
				: OperatingSystem.IsLinux() ? 0 // X11 has no cheap top-most-at-point query; the Linux verification judges what is on screen instead
				: CocoaOverlayInterop.WindowNumberAtPoint(x, y);

		/// <summary>The overlay window's frame. Windows and Linux: virtual-screen pixels with a TOP-left origin (macOS: Cocoa points, bottom-left).</summary>
		public static CocoaOverlayInterop.NSRect GetFrame(IntPtr window)
			=> OperatingSystem.IsWindows() ? Win32OverlayInterop.GetFrame(window)
				: OperatingSystem.IsLinux() ? LinuxOverlayInterop.GetFrame(window)
				: CocoaOverlayInterop.GetFrame(window);

		/// <summary>Places the overlay over a screen rectangle in pixels, top-left origin (Windows and Linux).</summary>
		public static void SetScreenRect(IntPtr window, int x, int y, int width, int height)
		{
			if (OperatingSystem.IsLinux()) LinuxOverlayInterop.SetScreenRect(window, x, y, width, height);
			else Win32OverlayInterop.SetScreenRect(window, x, y, width, height);
		}
	}

	/// <summary>
	/// Linux (X11) side of <see cref="OverlayNative"/>. The overlay handle is the SDL_Window*, so SDL moves, sizes, shows, hides and focuses the
	/// window; the one thing SDL cannot say is that it belongs to another window, which libX11's WM_TRANSIENT_FOR hint does (the host's X11 window
	/// comes from the LibreWPF render window): a window manager then keeps the overlay above its host, hides it with it and out of the task bar.
	/// Coordinates are screen pixels, top-left origin.
	/// </summary>
	static unsafe class LinuxOverlayInterop
	{
		const uint WindowFlagInputFocus = 0x200;
		const nuint XaWmTransientFor = 68;

		[DllImport("libX11.so.6")] static extern int XSetTransientForHint(IntPtr display, nuint window, nuint transientFor);
		[DllImport("libX11.so.6")] static extern int XDeleteProperty(IntPtr display, nuint window, nuint property);
		[DllImport("libX11.so.6")] static extern int XFlush(IntPtr display);

		static Silk.NET.SDL.Sdl Sdl { get { return Stride.Graphics.SDL.Window.SDL; } }

		static Silk.NET.SDL.Window* Win(IntPtr handle) { return (Silk.NET.SDL.Window*)handle; }

		public static void SetBordered(IntPtr handle, bool bordered)
			=> Sdl.SetWindowBordered(Win(handle), bordered ? Silk.NET.SDL.SdlBool.True : Silk.NET.SDL.SdlBool.False);

		public static void SetScreenRect(IntPtr handle, int x, int y, int width, int height)
		{
			Sdl.SetWindowPosition(Win(handle), x, y);
			Sdl.SetWindowSize(Win(handle), Math.Max(1, width), Math.Max(1, height));
		}

		public static void Show(IntPtr handle)
		{
			Sdl.ShowWindow(Win(handle));
			Sdl.RaiseWindow(Win(handle));
		}

		public static void Hide(IntPtr handle) => Sdl.HideWindow(Win(handle));

		public static void Focus(IntPtr handle) => Sdl.SetWindowInputFocus(Win(handle));

		public static bool HasInputFocus(IntPtr handle) => (Sdl.GetWindowFlags(Win(handle)) & WindowFlagInputFocus) != 0;

		/// <summary>The overlay's X11 window id (its SDL_Window* is not an id the rest of the system knows).</summary>
		public static long NativeId(IntPtr handle)
		{
			if (!TryGetX11(handle, out _, out nuint window))
				return 0;
			return (long)window;
		}

		/// <summary>Marks the overlay as transient for the host's X11 window; a zero host removes the relation.</summary>
		public static void SetTransientFor(IntPtr handle, IntPtr hostX11Window)
		{
			if (!TryGetX11(handle, out IntPtr display, out nuint window))
				return;
			if (hostX11Window == IntPtr.Zero)
				XDeleteProperty(display, window, XaWmTransientFor);
			else
				XSetTransientForHint(display, window, (nuint)hostX11Window.ToInt64());
			XFlush(display);
		}

		public static CocoaOverlayInterop.NSRect GetFrame(IntPtr handle)
		{
			int x, y, w, h;
			Sdl.GetWindowPosition(Win(handle), &x, &y);
			Sdl.GetWindowSize(Win(handle), &w, &h);
			return new CocoaOverlayInterop.NSRect(x, y, w, h);
		}

		static bool TryGetX11(IntPtr handle, out IntPtr display, out nuint window)
		{
			display = IntPtr.Zero;
			window = 0;
			var info = new Silk.NET.SDL.SysWMInfo();
			Sdl.GetVersion(ref info.Version);
			if (!Sdl.GetWindowWMInfo(Win(handle), &info))
				return false;
			display = (IntPtr)info.Info.X11.Display;
			window = (nuint)info.Info.X11.Window;
			return display != IntPtr.Zero && window != 0;
		}
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
