using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using OpenDevelop.IntegrationTests;
using Xunit;

namespace Stride.OpenDevelop.AddIn.Tests;

/// <summary>
/// What "the scene viewport is running" does not prove on Linux (X11, here Xvfb): that the SDL window is genuinely over the WPF element, that the
/// game keeps producing frames without faulting, that real pixels of a rendered scene are on the screen there, that real pointer input reaches
/// the game through the native window, and that the overlay follows the host window when it moves and is resized. The screen is read with
/// ImageMagick's <c>import</c> and input is injected with <c>xdotool</c>, so it is the real display server that decides what is visible.
/// There is no top-most check: X11 has no cheap window-at-point query, so the pixels are what shows the overlay is in front.
/// </summary>
internal static class LinuxOverlayVerification
{
	public static async Task VerifyAsync(IsolatedIde ide, string shotDirectory, Func<Func<Task<bool>>, TimeSpan, Task<bool>> pollUntil)
	{
		Directory.CreateDirectory(shotDirectory);

		// 1. Placement: the native frame equals the WPF element's screen rectangle and SDL considers the window shown.
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
			bool shown = status.GetProperty("sdlVisible").GetBoolean();
			placement += $" sdlVisible={shown}";
			return sameRect && shown;
		}, TimeSpan.FromSeconds(30));
		Assert.True(placed, "The overlay is not positioned over the viewport element: " + placement + "\n" + status + "\n" + ide.Output);

		// 2. Frames: the game's own frame counter advances and the game has not faulted. Steady progress, not speed (software Vulkan is slow).
		long frames0 = Input(status).GetProperty("gameFrames").GetInt64();
		long frames1 = frames0;
		var framesDeadline = DateTime.UtcNow.AddSeconds(30);
		while (frames1 - frames0 < 5 && DateTime.UtcNow < framesDeadline)
		{
			await Task.Delay(500);
			status = await ide.InvokeAsync("od.stride.scene-status");
			frames1 = Input(status).GetProperty("gameFrames").GetInt64();
		}
		Assert.True(frames1 - frames0 >= 5, $"The game is not producing frames: gameFrames {frames0} -> {frames1} in 30 s.\n{status}\n{ide.Output}");
		Assert.False(Input(status).TryGetProperty("gameFaulted", out var faulted) && faulted.ValueKind == JsonValueKind.True,
			"The editor game is faulted (it ticks but renders nothing): " + (Input(status).TryGetProperty("lastFault", out var fault) ? fault.GetString() : "?") + "\n" + status + "\n" + ide.Output);

		// 3. Pixels: what is on the screen inside the frame is a rendered scene, not a flat or empty surface. The first frames are the empty editor
		// while the game's assets load and its shaders compile, which software Vulkan takes tens of seconds over: look until the scene shows up.
		var fr = status.GetProperty("frame");
		var rect = new Rect((int)fr.GetProperty("x").GetDouble(), (int)fr.GetProperty("y").GetDouble(), (int)fr.GetProperty("w").GetDouble(), (int)fr.GetProperty("h").GetDouble());
		string shot = Path.Combine(shotDirectory, "viewport.png");
		var (distinct, dominantShare) = await CapturePixelsAsync(rect, shot);
		var sceneDeadline = DateTime.UtcNow.AddSeconds(60);
		while (!(distinct >= 200 && dominantShare < 0.90) && DateTime.UtcNow < sceneDeadline)
		{
			await Task.Delay(2000);
			(distinct, dominantShare) = await CapturePixelsAsync(rect, shot);
		}
		Assert.True(distinct >= 200 && dominantShare < 0.90,
			$"The screen area of the viewport looks empty or flat: {distinct} distinct colours, the most common covers {dominantShare:P0}. Screenshot: {shot}\n{status}\n{ide.Output}");

		// 4. Input: real pointer input over the overlay reaches the game (SDL receives it natively; nothing is forwarded from WPF).
		int cx = rect.X + rect.W / 2, cy = rect.Y + rect.H / 2;
		await XdotoolAsync($"mousemove {cx} {cy}");
		await Task.Delay(300);
		await XdotoolAsync($"mousemove {cx + rect.W / 5} {cy + rect.H / 6}");
		string mouse = "";
		bool moved = await pollUntil(async () =>
		{
			status = await ide.InvokeAsync("od.stride.scene-status");
			var m = Input(status).GetProperty("mouse");
			double mx = m.GetProperty("x").GetDouble(), my = m.GetProperty("y").GetDouble();
			mouse = $"({mx:F3},{my:F3})";
			return Math.Abs(mx - 0.7) < 0.08 && Math.Abs(my - (0.5 + 1.0 / 6)) < 0.08;
		}, TimeSpan.FromSeconds(10));
		Assert.True(moved, $"Pointer movement over the overlay did not reach the game: mouse={mouse}, expected about (0.700,0.667).\n{status}");

		await XdotoolAsync("mousedown 1");
		bool down = await pollUntil(async () =>
		{
			status = await ide.InvokeAsync("od.stride.scene-status");
			return Input(status).GetProperty("downButtons").GetInt32() >= 1;
		}, TimeSpan.FromSeconds(10));
		await XdotoolAsync("mouseup 1");
		Assert.True(down, "A left-button press over the overlay did not reach the game (downButtons stayed 0).\n" + status);

		// 5. Following the host window: moving and resizing the IDE window must carry the overlay with it, and the scene must still render there.
		long host = status.GetProperty("hostWindowNumber").GetInt64();
		Assert.True(host != 0, "The add-in reported no host window id.\n" + status);
		var before = rect;
		var (hx, hy, hw, hh) = await GeometryAsync(host);
		await XdotoolAsync($"windowmove {host} {hx + 60} {hy + 40}");
		string followed = "";
		bool moveOk = await pollUntil(async () =>
		{
			status = await ide.InvokeAsync("od.stride.scene-status");
			var f = status.GetProperty("frame"); var r = status.GetProperty("screenRect");
			followed = $"frame=({f.GetProperty("x")},{f.GetProperty("y")}) element=({r.GetProperty("x")},{r.GetProperty("y")}) expected about ({before.X + 60},{before.Y + 40})";
			return Math.Abs(f.GetProperty("x").GetDouble() - (before.X + 60)) <= 3 && Math.Abs(f.GetProperty("y").GetDouble() - (before.Y + 40)) <= 3
				&& Math.Abs(f.GetProperty("x").GetDouble() - r.GetProperty("x").GetDouble()) <= 3 && Math.Abs(f.GetProperty("y").GetDouble() - r.GetProperty("y").GetDouble()) <= 3;
		}, TimeSpan.FromSeconds(15));
		Assert.True(moveOk, "The overlay did not follow the host window when it moved: " + followed + "\n" + status);

		await XdotoolAsync($"windowsize {host} {hw - 160} {hh - 100}");
		bool resizeOk = await pollUntil(async () =>
		{
			status = await ide.InvokeAsync("od.stride.scene-status");
			var f = status.GetProperty("frame"); var r = status.GetProperty("screenRect");
			followed = $"frame=({f.GetProperty("x")},{f.GetProperty("y")},{f.GetProperty("w")},{f.GetProperty("h")}) element=({r.GetProperty("x")},{r.GetProperty("y")},{r.GetProperty("w")},{r.GetProperty("h")}) before=({before.W}x{before.H})";
			var gw = Input(status).GetProperty("gameWindow");
			return f.GetProperty("w").GetDouble() < before.W - 20 && Math.Abs(f.GetProperty("w").GetDouble() - r.GetProperty("w").GetDouble()) <= 3 && Math.Abs(f.GetProperty("h").GetDouble() - r.GetProperty("h").GetDouble()) <= 3
				&& Math.Abs(gw.GetProperty("w").GetDouble() - f.GetProperty("w").GetDouble()) <= 4;
		}, TimeSpan.FromSeconds(15));
		Assert.True(resizeOk, "The overlay did not follow the host window when it was resized (frame and game window should track the element): " + followed + "\n" + status);
		await Task.Delay(1200);
		status = await ide.InvokeAsync("od.stride.scene-status");
		var finalFrame = status.GetProperty("frame");
		var movedRect = new Rect((int)finalFrame.GetProperty("x").GetDouble(), (int)finalFrame.GetProperty("y").GetDouble(), (int)finalFrame.GetProperty("w").GetDouble(), (int)finalFrame.GetProperty("h").GetDouble());
		string shot2 = Path.Combine(shotDirectory, "viewport-after-move-resize.png");
		var (distinct2, share2) = await CapturePixelsAsync(movedRect, shot2);
		var redrawDeadline = DateTime.UtcNow.AddSeconds(60);
		while (!(distinct2 >= 100 && share2 < 0.90) && DateTime.UtcNow < redrawDeadline)
		{
			await Task.Delay(2000);
			(distinct2, share2) = await CapturePixelsAsync(movedRect, shot2);
		}
		Assert.True(distinct2 >= 100 && share2 < 0.90,
			$"After moving and resizing the host window the viewport looks empty or flat: {distinct2} distinct colours, the most common covers {share2:P0}. Screenshot: {shot2}\n{status}\n{ide.Output}");
	}

	record struct Rect(int X, int Y, int W, int H);

	static JsonElement Input(JsonElement status) => status.GetProperty("input");

	static async Task<(string Out, int Exit)> RunAsync(string file, string args, bool binary = false)
	{
		var start = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start " + file);
		string output = await process.StandardOutput.ReadToEndAsync();
		string error = await process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		Assert.True(process.ExitCode == 0, $"{file} {args} failed ({process.ExitCode}): {error}");
		return (output, process.ExitCode);
	}

	static Task XdotoolAsync(string args) => RunAsync("xdotool", args);

	static async Task<(int X, int Y, int W, int H)> GeometryAsync(long window)
	{
		var (text, _) = await RunAsync("xdotool", $"getwindowgeometry --shell {window}");
		int Get(string key) => int.Parse(text.Split('\n').First(l => l.StartsWith(key + "=")).Split('=')[1].Trim(), CultureInfo.InvariantCulture);
		return (Get("X"), Get("Y"), Get("WIDTH"), Get("HEIGHT"));
	}

	/// <summary>Copies a screen rectangle to a PNG and counts distinct colours (sampled on a grid) and the share of the most common one.</summary>
	static async Task<(int Distinct, double DominantShare)> CapturePixelsAsync(Rect rect, string path)
	{
		string geometry = $"-window root -crop {rect.W}x{rect.H}+{rect.X}+{rect.Y} +repage";
		await RunAsync("import", $"{geometry} png:\"{path}\"");

		// The same capture as raw 8-bit RGB, to count colours without an image decoder.
		var start = new ProcessStartInfo("import", $"{geometry} -depth 8 rgb:-") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start import");
		using var ms = new MemoryStream();
		await process.StandardOutput.BaseStream.CopyToAsync(ms);
		string error = await process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		Assert.True(process.ExitCode == 0, "import failed: " + error);
		var bytes = ms.ToArray();
		Assert.True(bytes.Length >= rect.W * rect.H * 3, $"import returned {bytes.Length} bytes for a {rect.W}x{rect.H} capture");

		var counts = new Dictionary<int, int>();
		int total = 0;
		for (int y = 0; y < rect.H; y += 4)
			for (int x = 0; x < rect.W; x += 4)
			{
				int i = (y * rect.W + x) * 3;
				// Quantise to 5 bits per channel so noise and dithering do not count as distinct colours.
				int c = ((bytes[i] & 0xF8) << 16) | ((bytes[i + 1] & 0xF8) << 8) | (bytes[i + 2] & 0xF8);
				counts[c] = counts.GetValueOrDefault(c) + 1;
				total++;
			}
		return (counts.Count, total == 0 ? 1.0 : counts.Values.Max() / (double)total);
	}
}
