using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// End-to-end Stride Game Studio integration: open a real Stride game project, see its .sdpkg
/// Assets subtree in the Projects pad, edit a game script, inspect the tree/editor, and run the
/// game. Driven entirely through DevFlow (see <stride>/sources/tools/Stride.OpenDevelop.AddIn/stride-game-studio.md "Integration
/// test design" for the full case and per-phase blockers).
///
/// Uses the Stride First-Person-Shooter template game IN PLACE (the local port clone, the same
/// checkout the addin's $(StrideRoot) resolves to) rather than a copied fixture, because the
/// game's <c>ProjectReference</c>s point at shared Packs two levels above it; a temp copy would
/// break those paths.
///
/// The addin-generated "<Game>.Desktop" launcher project can lose a one-shot restore/build race:
/// the OTHER OpenDevelop process under test (which has the solution open) evaluates the same
/// freshly generated project in-process at roughly the same moment <see cref="BuildProjectAsync"/>
/// spawns an external "dotnet build" for it, and the loser can write (or read) a partial
/// obj/project.assets.json - observed as a same-run-only CS0246 on "using Stride.Engine;" (the
/// ProjectReference to the sibling .Game project not resolving its transitive Stride.* packages)
/// that a plain retry never reproduces in isolation (confirmed: the generated template restores
/// and builds clean standalone, every time). <see cref="BuildProjectAsync"/> retries with a real
/// obj/bin wipe rather than a bare retry, since a bare retry can just re-read the same corrupted
/// assets file (MSBuild's up-to-date check skips restore when the timestamp looks fresh).
///
/// <see cref="StrideGame_OpenEditInspectRun"/> therefore cleans up after itself instead: it deletes
/// the generated "<Game>.Desktop" project, its generated ".sln", and any bin/obj the run produced,
/// so running the suite does not leave untracked/staged git noise in the developer's real Stride
/// checkout.
///
/// The addin itself is built in the Stride checkout. It is never copied into the installed
/// OpenDevelop application: the Addin SDK starts an isolated child with the build output passed
/// through <c>-addindir:</c>. This keeps the installed host free of Stride-specific payload while
/// exercising the development workflow a Stride addin author actually uses.
/// </summary>
[Collection(Stride.OpenDevelop.AddIn.Tests.OpenDevelopInstalledAppCollection.Name)]
public sealed class StrideGameStudioIntegrationTests : IAsyncDisposable
{
	readonly OpenDevelopAppFixture app;
	readonly string gameProjectPath;
	readonly string gameScriptPath;
	readonly string addinProjectPath;
	readonly string gameDir;
	const int AddinDevFlowPort = 9300;

	// Match the addin's StrideCheckoutRoot default (uno-tools/stride). Adjust if the clone lives
	// elsewhere: pass via env var STRIDE_CHECKOUT_ROOT.
	static string StrideRoot =>
		Environment.GetEnvironmentVariable("STRIDE_CHECKOUT_ROOT")
		?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "uno-tools", "stride");

	public StrideGameStudioIntegrationTests(OpenDevelopAppFixture app)
	{
		this.app = app;
		gameDir = Path.Combine(StrideRoot, "samples", "Templates", "FirstPersonShooter", "FirstPersonShooter", "FirstPersonShooter.Game");
		gameProjectPath = Path.Combine(gameDir, "FirstPersonShooter.Game.csproj");
		gameScriptPath = Path.Combine(gameDir, "Player", "PlayerController.cs");
		addinProjectPath = Path.Combine(StrideRoot, "sources", "tools", "Stride.OpenDevelop.AddIn",
			"ICSharpCode.StrideGameStudio.csproj");
	}

	/// <summary>
	/// Starts the external addin through the same SDK-generated start configuration a developer
	/// gets from F5. The child receives its own DevFlow port, so game-host assertions never require
	/// copying the addin into the installed application's AddIns directory.
	/// </summary>
	async Task<AddinHostClient> StartAddinHostAsync()
	{
		var opened = await app.ReopenSolutionAsync(addinProjectPath);
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
		var properties = await app.InvokeAsync("od.project.properties", "ICSharpCode.StrideGameStudio",
			"StartAction,StartArguments");
		Assert.True(properties.GetProperty("success").GetBoolean(), properties.ToString());
		var arguments = properties.GetProperty("properties").GetProperty("StartArguments").GetString() ?? string.Empty;
		Assert.Contains("-addindir:", arguments);
		Assert.Contains("-devflow:" + AddinDevFlowPort, arguments);

		var started = await app.InvokeAsync("od.run-project");
		Assert.True(started.GetProperty("success").GetBoolean(), started.ToString());
		var child = new AddinHostClient(AddinDevFlowPort);
		var ready = await OpenDevelopAppFixture.PollUntilAsync(async () => await child.IsReadyAsync(), TimeSpan.FromSeconds(120));
		Assert.True(ready, "The SDK-started addin host never exposed its isolated DevFlow endpoint.");
		return child;
	}

	async Task StopAddinHostAsync(AddinHostClient child)
	{
		child.Dispose();
		await app.InvokeAsync("od.stop-project");
	}

	/// <summary>
	/// Removes everything either <see cref="StrideGame_OpenEditInspectRun"/> or
	/// <see cref="StrideAddInProject_IsStartable_AndDebuggingBreaksInsideTheAddIn"/> can have
	/// generated in the real checkout: the addin-generated "<Game>.Desktop" launcher project, the
	/// ".sln" the IDE writes next to whatever csproj it opens (the game project and/or the addin
	/// project itself), and any bin/obj this run produced under the FirstPersonShooter or Packs
	/// sample trees.
	/// </summary>
	void CleanUpGeneratedFixtureArtifacts()
	{
		if (!Directory.Exists(StrideRoot))
			return;
		var firstPersonShooterRoot = Path.Combine(StrideRoot, "samples", "Templates", "FirstPersonShooter");
		var desktopLauncherDir = Path.Combine(Path.GetDirectoryName(gameDir)!, "FirstPersonShooter.Desktop");
		if (Directory.Exists(desktopLauncherDir))
			Directory.Delete(desktopLauncherDir, recursive: true);
		var generatedSolution = Path.Combine(gameDir, "FirstPersonShooter.Game.sln");
		if (File.Exists(generatedSolution))
			File.Delete(generatedSolution);
		var binDir = Path.Combine(Path.GetDirectoryName(gameDir)!, "Bin");
		if (Directory.Exists(binDir))
			Directory.Delete(binDir, recursive: true);
		foreach (var buildOutputDir in Directory.EnumerateDirectories(firstPersonShooterRoot, "*", SearchOption.AllDirectories)
			.Where(d => Path.GetFileName(d) is "bin" or "obj"))
		{
			try { Directory.Delete(buildOutputDir, recursive: true); }
			catch (IOException) { /* best-effort: a locked build output doesn't fail the test */ }
		}
		// od.open-solution on the addin project itself (not a copied fixture - the addin IS the
		// checkout) writes ICSharpCode.StrideGameStudio.sln next to its csproj the same way it
		// does for the game project above.
		var addinGeneratedSolution = Path.Combine(Path.GetDirectoryName(addinProjectPath)!,
			Path.GetFileNameWithoutExtension(addinProjectPath) + ".sln");
		if (File.Exists(addinGeneratedSolution))
			File.Delete(addinGeneratedSolution);
	}

	[Fact]
	public async Task StrideGame_OpenEditInspectRun()
	{
		if (!Directory.Exists(StrideRoot))
			Assert.Skip($"No Stride checkout at {StrideRoot}; set STRIDE_CHECKOUT_ROOT to run the Stride suite.");
		var child = await StartAddinHostAsync();
		try
		{
			var app = child;

		// ── 1. OPEN — load the Stride game project ────────────────────────────────────────────
		var opened = await app.ReopenSolutionAsync(gameProjectPath);
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

		// The .sdpkg-backed Assets subtree must appear under the game project in the Projects pad.
		var browser = await app.InvokeAsync("od.project-browser-state", "FirstPersonShooter.Game");
		Assert.True(browser.GetProperty("success").GetBoolean(), browser.ToString());
		var projectNode = browser.GetProperty("project");
		var assets = FindNode(projectNode, n => n.GetProperty("name").GetString() == "Assets");
		Assert.NotNull(assets);
		var assetsNode = assets!.Value;
		Assert.Equal("Folder", assetsNode.GetProperty("kind").GetString());

		// The virtual asset tree is derived from the .sdpkg AssetFolders (../Assets, Effects), so
		// the Assets subtree must contain at least one folder and one file somewhere (nesting is
		// package-dependent: the root "Assets" node typically wraps a folder of the same name that
		// holds the actual asset files). Assert recursively rather than on direct children.
		var allNodes = Flatten(assetsNode).ToList();
		Assert.Contains(allNodes, n => n.GetProperty("kind").GetString() == "Folder");
		Assert.Contains(allNodes, n => n.GetProperty("kind").GetString() == "File");
		Assert.True(allNodes.Any(n => n.GetProperty("kind").GetString() == "File"
			&& n.GetProperty("name").GetString()!.EndsWith(".sdscene", StringComparison.OrdinalIgnoreCase)),
			"The .sdpkg scene asset should surface as a File node: " + assetsNode);

		// ── 2. EDIT — open a game script for editing ─────────────────────────────────────────
		var script = await app.InvokeAsync("od.open-file", gameScriptPath);
		Assert.True(script.GetProperty("opened").GetBoolean(), script.ToString());
		var active = await WaitForActiveEditorAsync(child);
		Assert.True(active.GetProperty("active").GetBoolean(), active.ToString());
		Assert.True(active.GetProperty("isAvalonEdit").GetBoolean(),
			"The game script should open in the AvalonEdit text editor: " + active);
		Assert.True(active.GetProperty("textLength").GetInt32() > 0, "Script should have content: " + active);

		// ── 3. INSPECT — the tree/editor hold after the edit ─────────────────────────────────
		// Tree still intact (project + Assets subtree), and the script is the active view.
		var browserAgain = await app.InvokeAsync("od.project-browser-state", "FirstPersonShooter.Game");
		Assert.True(browserAgain.GetProperty("success").GetBoolean(), browserAgain.ToString());
		var assetsAgain = FindNode(browserAgain.GetProperty("project"), n => n.GetProperty("name").GetString() == "Assets");
		Assert.NotNull(assetsAgain);
		_ = assetsAgain!.Value;
		Assert.Equal("FirstPersonShooter.Game", browserAgain.GetProperty("project").GetProperty("name").GetString());

		var activeAgain = await app.InvokeAsync("od.active-view");
		Assert.Equal(gameScriptPath, activeAgain.GetProperty("fileName").GetString());

		// ── 4. RUN — build the game, then launch it ──────────────────────────────────────────
		// Building runs the Stride SDK asset pipeline (the kept CLI). od.build-solution is NOT
		// usable here yet: it builds only the current (single-project) solution and does not
		// transitively build the game's ProjectReference packs (mannequinModel, VFXPackage, ...),
		// so CSC fails on their missing ref assemblies - that is an open build-integration item
		// (see technote). Build the game csproj directly instead, which builds the packs
		// transitively and exercises the real Stride asset-pipeline build.
		var buildOutput = await BuildProjectAsync(gameProjectPath);
		Assert.True(buildOutput.exitCode == 0, "Stride game build failed:\n" + buildOutput.output);

		// The game project is a class library and cannot be started; the addin's launcher service
		// resolves or generates the startable "<Game>.Desktop" entry project, adds it to the
		// solution, and makes it the startup project. That happens automatically on solution open;
		// calling it explicitly makes the assertion independent of that hook's timing.
		var launcher = await app.InvokeAsync("od.stride.ensure-launcher");
		Assert.True(launcher.GetProperty("success").GetBoolean(), launcher.ToString());
		var launcherPath = launcher.GetProperty("launcherProjectPath").GetString();
		Assert.False(string.IsNullOrEmpty(launcherPath));
		Assert.True(File.Exists(launcherPath), "Launcher project was not created at " + launcherPath);
		Assert.True(launcher.GetProperty("setAsStartupProject").GetBoolean(),
			"The launcher was not made the startup project, so Run/Debug still has nothing to start.");

		// Building the launcher builds the game and its packs transitively and runs the Stride
		// asset pipeline for the launcher's own platform.
		var launcherBuild = await BuildProjectAsync(launcherPath!);
		Assert.True(launcherBuild.exitCode == 0, "Stride launcher build failed:\n" + launcherBuild.output);

		// Run through the IDE's own launch surface: od.run-project reuses IProject.CreateStartInfo
		// (the behavior chain the Debug > Start Without Debugging command uses) but keeps the
		// launched Process, so the smoke check can poll it and stop it again.
		var run = await app.InvokeAsync("od.run-project");
		Assert.True(run.GetProperty("success").GetBoolean(), run.ToString());
		Assert.True(run.GetProperty("processId").GetInt32() > 0, run.ToString());
		try
		{
			// The generated launcher sets GraphicsDeviceManager.SkipBackBufferClampToWindow, which
			// is what keeps this alive on macOS: without it the Vulkan presenter lets the Retina
			// CAMetalLayer extent feed back into the backbuffer size until a texture descriptor
			// exceeds Metal's 16384 cap and the game aborts within seconds (see technote). Staying
			// alive here is therefore a real regression guard on that line, not just a liveness poll.
			await Task.Delay(TimeSpan.FromSeconds(10));
			var status = await app.InvokeAsync("od.run-status");
			Assert.True(status.GetProperty("running").GetBoolean(),
				"The launched game exited instead of running: " + status);
		}
		finally
		{
			await app.InvokeAsync("od.stop-project");
		}
		}
		finally
		{
			await StopAddinHostAsync(child);
		}
	}

	/// <summary>
	/// The scenario the other two Stride tests don't cover: a GAME developer (not an addin
	/// developer) presses F5 on their game and expects the real debugger to launch it.
	/// <see cref="StrideAddInProject_IsStartable_AndDebuggingBreaksInsideTheAddIn"/> only covers
	/// addin projects, which reach the debugger through a different behavior branch
	/// (StartAction=Program + an explicit StartProgram, emitted by the Addin SDK). A game launcher
	/// is an ordinary Exe with neither, so it goes through DotNetStartBehavior's default
	/// StartAction.Project branch and Project.OutputAssemblyFullPath instead - a path no test
	/// exercised, and one that was completely broken on macOS/Linux:
	///
	/// WindowsDebugger.ResolveManagedAssemblyPath has to map the native apphost the project system
	/// hands it to the sibling managed .dll, because the DAP adapter launches the debuggee as
	/// `dotnet &lt;program&gt;`. It used Path.GetFileNameWithoutExtension, which is right for the
	/// Windows "&lt;AssemblyName&gt;.exe" shape but wrong for the EXTENSIONLESS Unix apphost whenever
	/// AssemblyName itself contains a dot: "FirstPersonShooter.Desktop" had ".Desktop" stripped as
	/// if it were an extension, so it probed for a nonexistent "FirstPersonShooter.dll", fell back
	/// to the apphost, and the dotnet muxer rejected the path as a command name ("Could not execute
	/// because the specified command or file was not found ... dotnet-&lt;path&gt; does not exist").
	/// Dotted assembly names are utterly ordinary, so F5 was broken for a large class of projects.
	///
	/// Asserting on the Debug output channel's "&gt; Debugging: &lt;file&gt;" line is what pins the
	/// regression precisely: it prints Path.GetFileName(targetPath) AFTER that mapping, so the
	/// ".dll" suffix is the direct evidence the apphost was resolved rather than passed through.
	/// (Reading that channel back at all depends on the matching CompilerMessageViewViewModel
	/// .GetOrCreateCategory fix - it previously matched categories on DisplayCategory only, so a
	/// lookup of "Debug" never found the debugger's channel and silently created a second, empty
	/// one, which is exactly what hid this failure from tooling in the first place.)
	///
	/// The breakpoint half guards a second, independent bug, in od.debug.start itself: it used to
	/// call OpenSolutionOrProject unconditionally, and on a bare .csproj that builds an ad-hoc
	/// single-project solution and CLOSES the current one, discarding its breakpoint bookmarks.
	/// Since the generated launcher lives in the GAME's solution (ensure-launcher adds it there and
	/// makes it the startup project), debugging it by path threw away the breakpoints just set in
	/// the game's scripts and the session ran straight through - which looked exactly like "the
	/// debugger can't bind breakpoints in Stride game code" and was nothing of the sort. The
	/// launcher path is passed here deliberately, to keep that reopen-suppression covered.
	/// </summary>
	[Fact]
	public async Task StrideGame_ScriptDebuggingBreaksInsidePlayerScript()
	{
		if (!Directory.Exists(StrideRoot))
			Assert.Skip($"No Stride checkout at {StrideRoot}; set STRIDE_CHECKOUT_ROOT to run the Stride suite.");
		var child = await StartAddinHostAsync();
		try
		{
			var app = child;

		var opened = await app.ReopenSolutionAsync(gameProjectPath);
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

		var buildOutput = await BuildProjectAsync(gameProjectPath);
		Assert.True(buildOutput.exitCode == 0, "Stride game build failed:\n" + buildOutput.output);

		var launcher = await app.InvokeAsync("od.stride.ensure-launcher");
		Assert.True(launcher.GetProperty("success").GetBoolean(), launcher.ToString());
		var launcherPath = launcher.GetProperty("launcherProjectPath").GetString();
		Assert.False(string.IsNullOrEmpty(launcherPath));
		Assert.True(launcher.GetProperty("setAsStartupProject").GetBoolean(),
			"The launcher was not made the startup project, so F5 still has nothing to debug.");

		var launcherBuild = await BuildProjectAsync(launcherPath!);
		Assert.True(launcherBuild.exitCode == 0, "Stride launcher build failed:\n" + launcherBuild.output);

		// Move()'s body runs unconditionally every frame (no input needed), so this is reached
		// without simulating keyboard/mouse into the running game.
		var breakpointLine = FindLine(gameScriptPath, "character.SetVelocity(moveDirection");

		await app.InvokeAsync("od.open-file", gameScriptPath);
		await app.InvokeAsync("od.debug.clear-breakpoints");
		var breakpoint = await app.InvokeAsync("od.debug.set-breakpoint", gameScriptPath, breakpointLine);
		Assert.True(breakpoint.GetProperty("success").GetBoolean(), breakpoint.ToString());

		try
		{
			// Booting a real Stride game (Vulkan/MoltenVK init, asset load) before the first frame
			// runs takes a while, hence the generous timeout.
			var start = await app.InvokeAsync("od.debug.start", launcherPath, true, 180);
			Assert.True(start.GetProperty("stopped").GetBoolean(),
				"Debugging the game never stopped at the breakpoint in the player script: " + start);
			Assert.EndsWith("PlayerController.cs",
				(start.GetProperty("currentFile").GetString() ?? string.Empty).Replace('\\', '/'));
			Assert.Equal(breakpointLine, start.GetProperty("currentLine").GetInt32());

			// The frame really is the player script's own Move(), reached through Stride's
			// ScriptSystem - not a same-named method elsewhere.
			var stack = await app.InvokeAsync("od.debug.call-stack");
			var frames = stack.EnumerateArray().Select(f => f.GetProperty("Name").GetString() ?? string.Empty).ToList();
			Assert.Contains(frames, n => n.Contains("PlayerController.Move()", StringComparison.Ordinal));
			Assert.Contains(frames, n => n.Contains("ScriptSystem", StringComparison.Ordinal));

			// The breakpoint hitting at all already proves the apphost was mapped to the managed
			// .dll, but assert the mapping explicitly so a regression there is reported as such
			// rather than as a mysterious missed breakpoint.
			var debugText = await WaitForDebugOutputAsync(child, "> Debugging: ");
			Assert.Contains("FirstPersonShooter.Desktop.dll", debugText);
			Assert.DoesNotContain("Could not execute because the specified command or file was not found", debugText);

			// The reopen-suppression fix: debugging a project that already belongs to the open
			// solution must not swap it for an ad-hoc single-project one.
			var solution = await app.InvokeAsync("od.solution.status");
			Assert.EndsWith("FirstPersonShooter.Game.sln",
				(solution.GetProperty("path").GetString() ?? string.Empty).Replace('\\', '/'));
		}
		finally
		{
			await app.InvokeAsync("od.debug.stop");
			await app.InvokeAsync("od.debug.clear-breakpoints");
		}
		}
		finally
		{
			await StopAddinHostAsync(child);
		}
	}

	/// <summary>
	/// The game developer's actual inner loop, which no other test covers end to end: change a
	/// script IN THE IDE, save, rebuild, run - and have the new code actually execute. The existing
	/// tests only ever open a script read-only, so nothing proved an edit reaches the running game;
	/// a stale-output or wrong-OutputPath regression would leave them all green while every real
	/// code change silently did nothing.
	///
	/// The edit goes through od.file.edit-text + od.file.save, i.e. the same AvalonEdit
	/// Document.Insert and Ctrl+S path a keystroke takes, rather than writing the file behind the
	/// IDE's back - so the IDE's own edit/save//file-watcher behaviour is part of what is verified.
	///
	/// od.file.edit-text can only append at end-of-document, so the probe is a self-contained
	/// namespace block appended after the last brace, using [ModuleInitializer] - it runs when the
	/// game loads FirstPersonShooter.Game.dll, with no entity/scene wiring needed and no dependence
	/// on input or on which scripts the scene happens to attach.
	/// </summary>
	[Fact]
	public async Task StrideGame_EditedScriptIsRebuiltAndRuns()
	{
		if (!Directory.Exists(StrideRoot))
			Assert.Skip($"No Stride checkout at {StrideRoot}; set STRIDE_CHECKOUT_ROOT to run the Stride suite.");
		var child = await StartAddinHostAsync();
		try
		{
			var app = child;

		const string marker = "OD_STRIDE_EDIT_MARKER";
		var originalScript = await File.ReadAllTextAsync(gameScriptPath);
		try
		{
			var opened = await app.ReopenSolutionAsync(gameProjectPath);
			Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

			var openFile = await app.InvokeAsync("od.open-file", gameScriptPath);
			Assert.True(openFile.GetProperty("opened").GetBoolean(), openFile.ToString());

			var probe = "\n\nnamespace FirstPersonShooter.OdProbe\n{\n"
				+ "\tinternal static class EditMarker\n\t{\n"
				+ "\t\t[System.Runtime.CompilerServices.ModuleInitializer]\n"
				+ "\t\tinternal static void Init() => System.Console.WriteLine(\"" + marker + "\");\n"
				+ "\t}\n}\n";
			var edit = await app.InvokeAsync("od.file.edit-text", gameScriptPath, probe);
			Assert.True(edit.GetProperty("success").GetBoolean(), edit.ToString());
			Assert.True(edit.GetProperty("isDirty").GetBoolean(),
				"Inserting into the editor document did not dirty the file: " + edit);

			var saved = await app.InvokeAsync("od.file.save", gameScriptPath);
			Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
			Assert.False(saved.GetProperty("isDirty").GetBoolean(), "File still dirty after save: " + saved);
			Assert.Contains(marker, await File.ReadAllTextAsync(gameScriptPath));

			var launcher = await app.InvokeAsync("od.stride.ensure-launcher");
			Assert.True(launcher.GetProperty("success").GetBoolean(), launcher.ToString());
			var launcherPath = launcher.GetProperty("launcherProjectPath").GetString();
			Assert.False(string.IsNullOrEmpty(launcherPath));

			var launcherBuild = await BuildProjectAsync(launcherPath!);
			Assert.True(launcherBuild.exitCode == 0, "Stride launcher build failed:\n" + launcherBuild.output);

			var run = await app.InvokeAsync("od.run-project");
			Assert.True(run.GetProperty("success").GetBoolean(), run.ToString());
			try
			{
				var output = await WaitForRunOutputAsync(child, marker);
				Assert.Contains(marker, output);
			}
			finally
			{
				await app.InvokeAsync("od.stop-project");
			}
		}
		finally
		{
			// Restore the checkout's own source before anything else - this test edits a real file
			// in the developer's Stride clone, so a failed assertion must not leave it modified.
			await File.WriteAllTextAsync(gameScriptPath, originalScript);
			await app.InvokeAsync("od.file.revert-all-dirty");
		}
		}
		finally
		{
			await StopAddinHostAsync(child);
		}
	}

	/// <summary>
	/// Polls the console output captured from od.run-project until it contains
	/// <paramref name="marker"/>, returning whatever it holds if the marker never arrives so the
	/// caller's assertion shows the real output instead of a bare timeout.
	/// </summary>
	async Task<string> WaitForRunOutputAsync(string marker)
	{
		string text = string.Empty;
		for (var i = 0; i < 60; i++)
		{
			var output = await app.InvokeAsync("od.run-output");
			text = output.GetProperty("text").GetString() ?? string.Empty;
			if (text.Contains(marker, StringComparison.Ordinal))
				return text;
			await Task.Delay(TimeSpan.FromSeconds(1));
		}
		return text;
	}

	static async Task<string> WaitForRunOutputAsync(AddinHostClient app, string marker)
	{
		string text = string.Empty;
		for (var i = 0; i < 60; i++)
		{
			var output = await app.InvokeAsync("od.run-output");
			text = output.GetProperty("text").GetString() ?? string.Empty;
			if (text.Contains(marker, StringComparison.Ordinal))
				return text;
			await Task.Delay(TimeSpan.FromSeconds(1));
		}
		return text;
	}

	static async Task<(int exitCode, string output)> BuildProjectAsync(string projectPath)
	{
		(int exitCode, string output) result = (-1, string.Empty);
		for (var attempt = 0; attempt < 2; attempt++)
		{
			if (attempt > 0)
			{
				// A bare retry can just re-read the same corrupted obj/project.assets.json from the
				// lost race (MSBuild's up-to-date check skips restore when its timestamp looks
				// fresh) - wipe obj/bin so this attempt is a genuinely fresh restore + build.
				var projectDir = Path.GetDirectoryName(projectPath)!;
				foreach (var name in new[] { "obj", "bin" })
				{
					var dir = Path.Combine(projectDir, name);
					if (Directory.Exists(dir))
						Directory.Delete(dir, recursive: true);
				}
			}
			var psi = new ProcessStartInfo("dotnet", "build \"" + projectPath + "\" -c Debug")
			{
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			using var process = Process.Start(psi);
			var stdout = await process!.StandardOutput.ReadToEndAsync();
			var stderr = await process.StandardError.ReadToEndAsync();
			await process.WaitForExitAsync();
			result = (process.ExitCode, stdout + stderr);
			if (result.exitCode == 0)
				return result;
		}
		return result;
	}

	/// <summary>
	/// Polls the Output pad's "Debug" channel until it contains <paramref name="marker"/>, then
	/// returns its full text (returning whatever it holds if the marker never appears, so the
	/// caller's assertion reports the real content rather than a bare timeout).
	/// </summary>
	async Task<string> WaitForDebugOutputAsync(string marker)
	{
		string text = string.Empty;
		for (var i = 0; i < 60; i++)
		{
			var output = await app.InvokeAsync("od.output-text", "Debug");
			text = output.GetProperty("text").GetString() ?? string.Empty;
			if (text.Contains(marker, StringComparison.Ordinal))
				return text;
			await Task.Delay(TimeSpan.FromSeconds(1));
		}
		return text;
	}

	static async Task<string> WaitForDebugOutputAsync(AddinHostClient app, string marker)
	{
		string text = string.Empty;
		for (var i = 0; i < 60; i++)
		{
			var output = await app.InvokeAsync("od.output-text", "Debug");
			text = output.GetProperty("text").GetString() ?? string.Empty;
			if (text.Contains(marker, StringComparison.Ordinal))
				return text;
			await Task.Delay(TimeSpan.FromSeconds(1));
		}
		return text;
	}

	async Task<JsonElement> WaitForActiveEditorAsync()
	{
		for (var i = 0; i < 20; i++)
		{
			var active = await app.InvokeAsync("od.active-view");
			if (active.GetProperty("active").GetBoolean() && active.GetProperty("isAvalonEdit").GetBoolean())
				return active;
			await Task.Delay(TimeSpan.FromMilliseconds(500));
		}
		return await app.InvokeAsync("od.active-view");
	}

	static async Task<JsonElement> WaitForActiveEditorAsync(AddinHostClient app)
	{
		for (var i = 0; i < 20; i++)
		{
			var active = await app.InvokeAsync("od.active-view");
			if (active.GetProperty("active").GetBoolean() && active.GetProperty("isAvalonEdit").GetBoolean())
				return active;
			await Task.Delay(TimeSpan.FromMilliseconds(500));
		}
		return await app.InvokeAsync("od.active-view");
	}

	static JsonElement? FindNode(JsonElement root, Func<JsonElement, bool> predicate)
	{
		if (predicate(root))
			return root;
		if (root.TryGetProperty("children", out var children))
		{
			foreach (var child in children.EnumerateArray())
			{
				var match = FindNode(child, predicate);
				if (match.HasValue)
					return match;
			}
		}
		return null;
	}

	static IEnumerable<JsonElement> Flatten(JsonElement root)
	{
		yield return root;
		if (root.TryGetProperty("children", out var children))
		{
			foreach (var child in children.EnumerateArray())
			{
				foreach (var descendant in Flatten(child))
					yield return descendant;
			}
		}
	}

	public ValueTask DisposeAsync()
	{
		CleanUpGeneratedFixtureArtifacts();
		return ValueTask.CompletedTask;
	}

	/// <summary>
	/// The Addin SDK's develop/run loop, exercised against the real Stride addin project now that
	/// it lives in the Stride repo: opening the ADDIN project (not the game) must produce a
	/// startable project, and starting it must bring up a second OpenDevelop that loads the addin.
	///
	/// This is the loop that makes an out-of-repo addin developable at all - without it the
	/// relocated addin would have no way to be run from the IDE that hosts it.
	///
	/// Runs under the real debugger and breaks inside the addin's own source. That is the strongest
	/// available evidence: the breakpoint is set before launch, in a module the debuggee has not
	/// loaded yet and which lives outside the debuggee's own directory, so hitting it proves the
	/// SDK-emitted start configuration launched the right host, the host loaded the addin from
	/// -addindir:, and the debugger rebound a pending breakpoint when that module arrived.
	/// </summary>
	[Fact]
	public async Task StrideAddInProject_IsStartable_AndDebuggingBreaksInsideTheAddIn()
	{
		if (!File.Exists(addinProjectPath))
			Assert.Skip($"No Stride addin project at {addinProjectPath}; set STRIDE_CHECKOUT_ROOT to run the Stride suite.");

		var opened = await app.ReopenSolutionAsync(addinProjectPath);
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

		// ── Addin SDK: the start configuration it emits ──────────────────────────────────────
		// StartAction=Program is what makes IsStartable ignore OutputType, so an addin (a class
		// library) becomes startable at all; the rest is what the child instance is told to do.
		var props = await app.InvokeAsync("od.project.properties", "ICSharpCode.StrideGameStudio",
			"StartAction,StartProgram,StartArguments,OpenDevelopAddin,OpenDevelopAddinKind");
		Assert.True(props.GetProperty("success").GetBoolean(), props.ToString());
		var values = props.GetProperty("properties");
		Assert.Equal("true", values.GetProperty("OpenDevelopAddin").GetString());
		Assert.Equal("InProcess", values.GetProperty("OpenDevelopAddinKind").GetString());
		Assert.Equal("Program", values.GetProperty("StartAction").GetString());

		var startProgram = values.GetProperty("StartProgram").GetString();
		Assert.False(string.IsNullOrEmpty(startProgram), "The Addin SDK emitted no StartProgram, so F5 would do nothing.");
		Assert.True(File.Exists(startProgram), $"StartProgram does not exist: {startProgram}");

		var startArguments = values.GetProperty("StartArguments").GetString() ?? string.Empty;
		Assert.Contains("-addindir:", startArguments);
		// Instance isolation: the child must not write into the developer's own settings/layout,
		// and must use either the SDK's normal DevFlow-off setting or this suite's dedicated child port.
		Assert.Contains("-configdir:", startArguments);
		var expectedDevFlowArgument = Environment.GetEnvironmentVariable("OPENDEVELOP_ADDIN_TEST_DEVFLOW_PORT") is { Length: > 0 } port
			? "-devflow:" + port
			: "-devflow:" + AddinDevFlowPort;
		Assert.Contains(expectedDevFlowArgument, startArguments);
		var configDir = ExtractQuotedArgument(startArguments, "-configdir:");
		Assert.False(string.IsNullOrEmpty(configDir), "No -configdir: value: the child would share the developer's profile.");

		// ── Debug the second instance, breaking inside the addin ────────────────────────────
		// The addin's autostart command runs while the child workbench initializes, so the
		// breakpoint is reached without driving the child's UI (which would be impossible anyway:
		// it uses a dedicated DevFlow endpoint in the suite, avoiding a collision with the parent).
		var addinSource = Path.Combine(Path.GetDirectoryName(addinProjectPath)!,
			"RegisterStrideProjectTreeContributorCommand.cs");
		Assert.True(File.Exists(addinSource), $"Missing addin source {addinSource}");
		var breakpointLine = FindLine(addinSource, "ProjectTreeContributorRegistry.Register(");

		await app.InvokeAsync("od.open-file", addinSource);
		await app.InvokeAsync("od.debug.clear-breakpoints");
		var breakpoint = await app.InvokeAsync("od.debug.set-breakpoint", addinSource, breakpointLine);
		Assert.True(breakpoint.GetProperty("success").GetBoolean(), breakpoint.ToString());

		try
		{
			// A second full IDE start under a suspended-attach debug session is slow.
			var start = await app.InvokeAsync("od.debug.start", addinProjectPath, true, 180);
			Assert.True(start.GetProperty("stopped").GetBoolean(),
				"Debugging the addin project never stopped at the breakpoint: " + start);
			Assert.EndsWith("RegisterStrideProjectTreeContributorCommand.cs",
				(start.GetProperty("currentFile").GetString() ?? string.Empty).Replace('\\', '/'));
			Assert.Equal(breakpointLine, start.GetProperty("currentLine").GetInt32());

			// The frame really is the addin's autostart command, not a same-named file elsewhere.
			var stack = await app.InvokeAsync("od.debug.call-stack");
			Assert.Contains(stack.EnumerateArray(),
				f => (f.GetProperty("Name").GetString() ?? string.Empty).Contains("Run"));

			// Instance isolation actually took effect on disk.
			Assert.True(Directory.Exists(configDir),
				$"The child did not use its isolated config directory {configDir}.");
		}
		finally
		{
			await app.InvokeAsync("od.debug.stop");
			await app.InvokeAsync("od.debug.clear-breakpoints");
		}
	}

	/// <summary>1-based line number of the first line containing <paramref name="marker"/>.</summary>
	static int FindLine(string file, string marker)
	{
		var lines = File.ReadAllLines(file);
		for (var i = 0; i < lines.Length; i++)
		{
			if (lines[i].Contains(marker, StringComparison.Ordinal))
				return i + 1;
		}
		throw new InvalidOperationException($"No line containing '{marker}' in {file}");
	}

	/// <summary>Value of a <c>-name:"value"</c> style start argument.</summary>
	static string ExtractQuotedArgument(string arguments, string name)
	{
		var index = arguments.IndexOf(name, StringComparison.Ordinal);
		if (index < 0)
			return string.Empty;
		var rest = arguments.Substring(index + name.Length);
		if (rest.StartsWith("\"", StringComparison.Ordinal))
		{
			var end = rest.IndexOf('"', 1);
			return end > 0 ? rest.Substring(1, end - 1) : string.Empty;
		}
		var space = rest.IndexOf(' ');
		return space > 0 ? rest.Substring(0, space) : rest;
	}

	sealed class AddinHostClient : IDisposable
	{
		readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(240) };
		readonly string baseUrl;

		public AddinHostClient(int port) => baseUrl = $"http://localhost:{port}";

		public async Task<bool> IsReadyAsync()
		{
			try {
				using var response = await http.GetAsync(baseUrl + "/api/v1/agent/status");
				return response.IsSuccessStatusCode;
			} catch (HttpRequestException) {
				return false;
			}
		}

		public async Task<JsonElement> InvokeAsync(string action, params object[] args)
		{
			using var content = new StringContent(JsonSerializer.Serialize(new { args }), System.Text.Encoding.UTF8, "application/json");
			using var response = await http.PostAsync($"{baseUrl}/api/v1/invoke/actions/{action}", content);
			var body = await response.Content.ReadAsStringAsync();
			if (!response.IsSuccessStatusCode)
				throw new InvalidOperationException($"Action '{action}' failed ({(int)response.StatusCode}): {body}");
			using var envelope = JsonDocument.Parse(body);
			var raw = envelope.RootElement.GetProperty("returnValue").GetString();
			if (string.IsNullOrEmpty(raw))
				throw new InvalidOperationException($"Action '{action}' returned no value: {body}");
			using var value = JsonDocument.Parse(raw);
			return value.RootElement.Clone();
		}

		public Task<JsonElement> ReopenSolutionAsync(string path) => InvokeAsync("od.open-solution", path);

		public void Dispose() => http.Dispose();
	}
}
