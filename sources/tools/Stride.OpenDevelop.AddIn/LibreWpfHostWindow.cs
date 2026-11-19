// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Resolves a WPF Window's real Cocoa NSWindow handle under LibreWPF, using LibreWPF.ProGPU's
// public diagnostics entry point - see doc/technotes/stride-game-studio.md "Composition-bridge
// probes". This is the LibreWPF-side counterpart to SdlNativeWindow (the SDL/Stride side).
//
// Note: ProGpuWpfDiagnostics is diagnostics-surface naming, not a hardened public contract - if
// a future LibreWPF.ProGPU version renames or removes it, this is the one place to fix.

using System;
using System.Windows;

using System.Windows.Media.ProGPU;

namespace ICSharpCode.StrideGameStudio
{
	static class LibreWpfHostWindow
	{
		/// <summary>The host window's real HWND (Windows): the one the user sees. LibreWPF can expose both its Silk/GLFW render window and
		/// an HwndSource proxy; prefer whichever is visible, because the overlay is owned by it and positioned against the screen.</summary>
		public static bool TryGetWin32Hwnd(Window window, out IntPtr hwnd)
		{
			hwnd = IntPtr.Zero;
			IntPtr silk = IntPtr.Zero;
			if (ProGpuWpfDiagnostics.TryGetWindowHost(window, out var host) && host?.SilkWindow?.Native?.Win32 is { } win32)
				silk = win32.Hwnd;
			IntPtr proxy = IntPtr.Zero;
			try { proxy = new System.Windows.Interop.WindowInteropHelper(window).Handle; } catch (InvalidOperationException) { }
			if (silk != IntPtr.Zero && Win32OverlayInterop.IsVisible(silk)) hwnd = silk;
			else if (proxy != IntPtr.Zero && Win32OverlayInterop.IsVisible(proxy)) hwnd = proxy;
			else hwnd = silk != IntPtr.Zero ? silk : proxy;
			return hwnd != IntPtr.Zero;
		}
		/// <summary>The host window's X11 window id (Linux, X11 or XWayland): the render window LibreWPF created through Silk/GLFW.</summary>
		public static bool TryGetX11Window(Window window, out IntPtr x11Window)
		{
			x11Window = IntPtr.Zero;
			if (ProGpuWpfDiagnostics.TryGetWindowHost(window, out var host) && host?.SilkWindow?.Native?.X11 is { } x11)
				x11Window = (IntPtr)(long)x11.Window;
			return x11Window != IntPtr.Zero;
		}

		public static bool TryGetCocoaNsWindow(Window window, out IntPtr nsWindow)
		{
			nsWindow = IntPtr.Zero;
			if (!ProGpuWpfDiagnostics.TryGetWindowHost(window, out var host) || host?.SilkWindow == null)
				return false;

			var native = host.SilkWindow.Native;
			if (native?.Cocoa is not { } cocoa || cocoa == IntPtr.Zero)
				return false;

			nsWindow = cocoa;
			return true;
		}
	}
}
