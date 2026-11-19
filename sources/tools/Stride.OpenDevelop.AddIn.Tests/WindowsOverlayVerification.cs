using OpenDevelop.IntegrationTests;
using Xunit;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Stride.OpenDevelop.AddIn.Tests;

/// <summary>
/// What "the scene viewport is running" does not prove on Windows: that the SDL window is genuinely over the WPF element, on top, that the game
/// keeps producing frames without faulting, that real pixels of a rendered scene are on the screen there, and that real OS mouse input reaches
/// the game through the native window. Each is checked against the live IDE and the failure message carries the full status JSON.
/// </summary>
internal static class WindowsOverlayVerification
{
	const int DpiPerMonitorV2 = -4;
	const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;
	const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

	[DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
	[DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
	[DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
	[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
	[DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
	[StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
	const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

	public static async Task VerifyAsync(IsolatedIde ide, string shotDirectory, Func<Func<Task<bool>>, TimeSpan, Task<bool>> pollUntil)
	{
		SetProcessDpiAwarenessContext(new IntPtr(DpiPerMonitorV2));
		Directory.CreateDirectory(shotDirectory);

		// 1. Placement: the native frame equals the WPF element's screen rectangle, and the overlay is the topmost window at its own centre.
		JsonElement status = default;
		string placement = "";
		bool placed = await pollUntil(async () =>
		{
			status = await ide.InvokeAsync("od.stride.scene-status");
			var f = status.GetProperty("frame");
			var r = status.GetProperty("screenRect");
			if (!r.GetProperty("valid").GetBoolean()) { placement = "screenRect is not valid yet"; return false; }
			double fx = f.GetProperty("x").GetDouble(), fy = f.GetProperty("y").GetDouble(), fw = f.GetProperty("w").GetDouble(), fh = f.GetProperty("h").GetDouble();
			double rx = r.GetProperty("x").GetDouble(), ry = r.GetProperty("y").GetDouble(), rw = r.GetProperty("w").GetDouble(), rh = r.GetProperty("h").GetDouble();
			placement = $"frame=({fx},{fy},{fw},{fh}) element=({rx},{ry},{rw},{rh})";
			bool sameRect = Math.Abs(fx - rx) <= 3 && Math.Abs(fy - ry) <= 3 && Math.Abs(fw - rw) <= 3 && Math.Abs(fh - rh) <= 3 && fw > 100 && fh > 100;
			bool onTop = status.GetProperty("windowNumber").GetInt64() == status.GetProperty("windowNumberAtCentre").GetInt64();
			bool shown = status.GetProperty("sdlVisible").GetBoolean();
			placement += $" onTop={onTop} sdlVisible={shown}";
			return sameRect && onTop && shown;
		}, TimeSpan.FromSeconds(30));
		Assert.True(placed, "The overlay is not positioned over the viewport element and on top: " + placement + "\n" + status);

		// 2. Frames: the game's own frame counter advances and the game has not faulted.
		long frames0 = Input(status).GetProperty("gameFrames").GetInt64();
		// Steady progress, not speed: a machine without a GPU (a CI runner renders with WARP) manages only a few frames a second, a GPU
		// dozens. Wait up to 20 s for 5 more frames; a frozen game advances none.
		long frames1 = frames0;
		var framesDeadline = DateTime.UtcNow.AddSeconds(20);
		while (frames1 - frames0 < 5 && DateTime.UtcNow < framesDeadline)
		{
			await Task.Delay(500);
			status = await ide.InvokeAsync("od.stride.scene-status");
			frames1 = Input(status).GetProperty("gameFrames").GetInt64();
		}
		Assert.True(frames1 - frames0 >= 5, $"The game is not producing frames: gameFrames {frames0} -> {frames1} in 20 s.\n{status}");
		Assert.False(Input(status).TryGetProperty("gameFaulted", out var faulted) && faulted.ValueKind == JsonValueKind.True,
			"The editor game is faulted (it ticks but renders nothing): " + (Input(status).TryGetProperty("lastFault", out var fault) ? fault.GetString() : "?") + "\n" + status + "\n" + ide.Output);

		// 3. Pixels: what is on the screen inside the frame is a rendered scene, not a flat or empty surface.
		var fr = status.GetProperty("frame");
		var rect = new Rectangle((int)fr.GetProperty("x").GetDouble(), (int)fr.GetProperty("y").GetDouble(), (int)fr.GetProperty("w").GetDouble(), (int)fr.GetProperty("h").GetDouble());
		string shot = Path.Combine(shotDirectory, "viewport.png");
		// The first frames are the empty editor (grid, gizmo) while the game's assets load and its shaders compile: on a machine without a GPU
		// that takes tens of seconds, so look until the scene shows up and judge the last capture.
		// The scene must also keep its materials' colours (PixelStats: blue and yellow walls, not one tint).
		var pixels = CapturePixels(rect, shot);
		var sceneDeadline = DateTime.UtcNow.AddSeconds(40);
		while (!SceneRendered(pixels, 200) && DateTime.UtcNow < sceneDeadline)
		{
			await Task.Delay(2000);
			pixels = CapturePixels(rect, shot);
		}
		Assert.True(SceneRendered(pixels, 200),
			$"The screen area of the viewport is not the rendered scene: {pixels.Distinct} distinct colours, the most common covers {pixels.DominantShare:P0}, hues: {pixels.Hues} (a correct render has two distinct hues). Screenshot: {shot}\n{status}\n{ide.Output}");

		// 4. Input: real OS pointer input over the overlay reaches the game (SDL receives it natively on Windows; nothing is forwarded from WPF).
		int cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
		MoveTo(cx, cy);
		await Task.Delay(300);
		MoveTo(cx + rect.Width / 5, cy + rect.Height / 6);
		string mouse = "";
		bool moved = await pollUntil(async () =>
		{
			status = await ide.InvokeAsync("od.stride.scene-status");
			var m = Input(status).GetProperty("mouse");
			double mx = m.GetProperty("x").GetDouble(), my = m.GetProperty("y").GetDouble();
			mouse = $"({mx:F3},{my:F3})";
			// The cursor sits at (0.5 + 0.2, 0.5 + 1/6) of the element: normalised position, within a generous tolerance.
			return Math.Abs(mx - 0.7) < 0.08 && Math.Abs(my - (0.5 + 1.0 / 6)) < 0.08;
		}, TimeSpan.FromSeconds(10));
		Assert.True(moved, $"Pointer movement over the overlay did not reach the game: mouse={mouse}, expected about (0.700,0.667).\n{status}");

		mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
		bool down = await pollUntil(async () =>
		{
			status = await ide.InvokeAsync("od.stride.scene-status");
			return Input(status).GetProperty("downButtons").GetInt32() >= 1;
		}, TimeSpan.FromSeconds(10));
		mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
		Assert.True(down, "A left-button press over the overlay did not reach the game (downButtons stayed 0).\n" + status);

		// 5. Following the host window: an owned popup does not move with its owner by itself, so moving and resizing the IDE window must carry the
		// overlay with it, and the scene must still render into the new rectangle.
		var host = new IntPtr(status.GetProperty("hostWindowNumber").GetInt64());
		Assert.True(GetWindowRect(host, out var hostRect), "The host window handle reported by the add-in is not a window: " + host);
		var before = rect;
		SetWindowPos(host, IntPtr.Zero, hostRect.Left + 60, hostRect.Top + 40, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
		string followed = "";
		bool moveOk = await pollUntil(async () =>
		{
			status = await ide.InvokeAsync("od.stride.scene-status");
			var f = status.GetProperty("frame"); var r = status.GetProperty("screenRect");
			followed = $"frame=({f.GetProperty("x")},{f.GetProperty("y")},{f.GetProperty("w")},{f.GetProperty("h")}) element=({r.GetProperty("x")},{r.GetProperty("y")}) expected about ({before.X + 60},{before.Y + 40})";
			return Math.Abs(f.GetProperty("x").GetDouble() - (before.X + 60)) <= 3 && Math.Abs(f.GetProperty("y").GetDouble() - (before.Y + 40)) <= 3
				&& Math.Abs(f.GetProperty("x").GetDouble() - r.GetProperty("x").GetDouble()) <= 3 && Math.Abs(f.GetProperty("y").GetDouble() - r.GetProperty("y").GetDouble()) <= 3;
		}, TimeSpan.FromSeconds(10));
		Assert.True(moveOk, "The overlay did not follow the host window when it moved: " + followed + "\n" + status);

		GetWindowRect(host, out hostRect);
		SetWindowPos(host, IntPtr.Zero, 0, 0, hostRect.Right - hostRect.Left - 160, hostRect.Bottom - hostRect.Top - 100, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
		bool resizeOk = await pollUntil(async () =>
		{
			status = await ide.InvokeAsync("od.stride.scene-status");
			var f = status.GetProperty("frame"); var r = status.GetProperty("screenRect");
			followed = $"frame=({f.GetProperty("x")},{f.GetProperty("y")},{f.GetProperty("w")},{f.GetProperty("h")}) element=({r.GetProperty("x")},{r.GetProperty("y")},{r.GetProperty("w")},{r.GetProperty("h")}) before=({before.Width}x{before.Height})";
			var gw = Input(status).GetProperty("gameWindow");
			return f.GetProperty("w").GetDouble() < before.Width - 20 && Math.Abs(f.GetProperty("w").GetDouble() - r.GetProperty("w").GetDouble()) <= 3 && Math.Abs(f.GetProperty("h").GetDouble() - r.GetProperty("h").GetDouble()) <= 3
				&& Math.Abs(gw.GetProperty("w").GetDouble() - f.GetProperty("w").GetDouble()) <= 4;
		}, TimeSpan.FromSeconds(10));
		Assert.True(resizeOk, "The overlay did not follow the host window when it was resized (frame and game window should track the element): " + followed + "\n" + status);
		await Task.Delay(1200);
		status = await ide.InvokeAsync("od.stride.scene-status");
		var finalFrame = status.GetProperty("frame");
		var movedRect = new Rectangle((int)finalFrame.GetProperty("x").GetDouble(), (int)finalFrame.GetProperty("y").GetDouble(), (int)finalFrame.GetProperty("w").GetDouble(), (int)finalFrame.GetProperty("h").GetDouble());
		string shot2 = Path.Combine(shotDirectory, "viewport-after-move-resize.png");
		// A resize recreates the swap chain and the game redraws at the new size: a slow (GPU-less) machine shows a blank surface for a while.
		var pixels2 = CapturePixels(movedRect, shot2);
		var redrawDeadline = DateTime.UtcNow.AddSeconds(40);
		while (!SceneRendered(pixels2, 100) && DateTime.UtcNow < redrawDeadline)
		{
			await Task.Delay(2000);
			pixels2 = CapturePixels(movedRect, shot2);
		}
		Assert.True(SceneRendered(pixels2, 100),
			$"After moving and resizing the host window the viewport is not the rendered scene: {pixels2.Distinct} distinct colours, the most common covers {pixels2.DominantShare:P0}, hues: {pixels2.Hues}. Screenshot: {shot2}\n{status}\n{ide.Output}");
	}

	static bool SceneRendered(PixelStats.Result p, int minDistinct) => p.Distinct >= minDistinct && p.DominantShare < 0.90 && p.HasDistinctHues;

	static JsonElement Input(JsonElement status) => status.GetProperty("input");

	static void MoveTo(int x, int y)
	{
		int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
		int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
		int ax = (int)Math.Round((x - vx) * 65535.0 / (vw - 1)), ay = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
		mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, ax, ay, 0, UIntPtr.Zero);
	}

	/// <summary>Copies a screen rectangle to a PNG and analyses its pixels (sampled on a grid), see <see cref="PixelStats"/>.</summary>
	static PixelStats.Result CapturePixels(Rectangle rect, string path)
	{
		using var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
		using (var g = Graphics.FromImage(bitmap))
			g.CopyFromScreen(rect.X, rect.Y, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
		bitmap.Save(path, ImageFormat.Png);
		var samples = new List<int>();
		for (int y = 0; y < rect.Height; y += 4)
			for (int x = 0; x < rect.Width; x += 4)
				samples.Add(bitmap.GetPixel(x, y).ToArgb() & 0x00FFFFFF);
		return PixelStats.Analyse(samples);
	}
}
