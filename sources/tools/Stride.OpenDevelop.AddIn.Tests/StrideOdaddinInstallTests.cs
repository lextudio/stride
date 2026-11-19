using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenDevelop.IntegrationTests;
using Xunit;

namespace Stride.OpenDevelop.AddIn.Tests;

/// <summary>
/// The release package, end to end: the built <c>ICSharpCode.StrideGameStudio.odaddin</c> is served by a fake GitHub, an
/// isolated OpenDevelop trusts the repository and installs it from the AddIn Manager, is restarted, and the installed
/// add-in must then load and surface a Stride game's .sdpkg assets in the Projects pad.
/// Needs STRIDE_ODADDIN (the package built with PackOdAddin), OD_IT_IDE_DLL (OpenDevelop.dll of the host under test) and
/// a Stride checkout (STRIDE_CHECKOUT_ROOT) for the sample game; skipped otherwise.
/// </summary>
public sealed class StrideOdaddinInstallTests
{
	const string Repository = "lextudio/stride";
	const string Identity = "ICSharpCode.StrideGameStudio";
	const string AssetName = "ICSharpCode.StrideGameStudio.odaddin";
	const string Tag = "v0.1.0";

	[Fact]
	public async Task GitHubReleasePackage_InstallsRestartsLoadsAndShowsTheAssetsOfAStrideGame()
	{
		string? package = Environment.GetEnvironmentVariable("STRIDE_ODADDIN");
		string strideRoot = Environment.GetEnvironmentVariable("STRIDE_CHECKOUT_ROOT") ?? "";
		string game = Path.Combine(strideRoot, "samples", "Templates", "FirstPersonShooter", "FirstPersonShooter", "FirstPersonShooter.Game", "FirstPersonShooter.Game.csproj");
		Assert.SkipUnless(!string.IsNullOrEmpty(package) && File.Exists(package) && File.Exists(game) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OD_IT_IDE_DLL")),
			"Set STRIDE_ODADDIN (built .odaddin), STRIDE_CHECKOUT_ROOT (with the FirstPersonShooter template) and OD_IT_IDE_DLL (host OpenDevelop.dll).");

		// Opening a game project generates a solution file and a "<Game>.Desktop" launcher project beside it; this test leaves
		// the checkout as it found it, like StrideGameStudioIntegrationTests does.
		string gameDir = Path.GetDirectoryName(game)!;
		string generatedSolution = Path.Combine(gameDir, "FirstPersonShooter.Game.sln");
		string generatedLauncher = Path.Combine(Path.GetDirectoryName(gameDir)!, "FirstPersonShooter.Desktop");
		bool hadSolution = File.Exists(generatedSolution), hadLauncher = Directory.Exists(generatedLauncher);

		string work = Path.Combine(Path.GetTempPath(), "od-it-stride-" + Guid.NewGuid().ToString("N"));
		string config = Directory.CreateDirectory(Path.Combine(work, "config")).FullName;
		using var github = new FakeGitHub(package!);
		try
		{
			var environment = new Dictionary<string, string?>
			{
				["OD_TEST_USER_ADDINS"] = "1",
				["OD_ADDIN_GITHUB_API"] = github.Base.ToString(),
				["OD_ADDIN_GITHUB_DOWNLOAD"] = github.Base.ToString(),
			};

			// First run: trust the repository and install the release from the AddIn Manager.
			using (var first = await IsolatedIde.StartAsync(config, environment))
			{
				Assert.True((await first.InvokeAsync("od.addin-manager.show")).GetProperty("success").GetBoolean());
				await first.InvokeAsync("od.test.queue-dialog-answer", "question", "[\"yes\"]");
				Assert.True((await first.InvokeAsync("od.addin-manager.trust", Repository)).GetProperty("success").GetBoolean());
				await first.InvokeAsync("od.addin-manager.refresh");
				var state = await WaitAsync(first, s => Entries(s, "AvailableAddInsViewModel").Any(), "Stride Game Studio to be listed");
				string name = Entries(state, "AvailableAddInsViewModel").Single().GetProperty("name").GetString()!;
				Assert.True((await first.InvokeAsync("od.addin-manager.install", "Available", name)).GetProperty("success").GetBoolean());
				await WaitAsync(first, s => Entries(s, "InstalledAddInsViewModel").Any(e => e.GetProperty("id").GetString() == Identity), "the add-in to be installed");
			}

			// Second run, same configuration: the add-in is part of the tree and works.
			using (var second = await IsolatedIde.StartAsync(config, environment))
			{
				var addIns = await second.InvokeAsync("od.addins");
				var loaded = addIns.GetProperty("addins").EnumerateArray()
					.FirstOrDefault(a => a.GetProperty("fileName").GetString()?.Contains("StrideGameStudio", StringComparison.OrdinalIgnoreCase) == true);
				Assert.True(loaded.ValueKind == JsonValueKind.Object, "The installed add-in is not in the add-in tree: " + addIns);
				Assert.True(loaded.GetProperty("enabled").GetBoolean(), loaded.ToString());

				// The add-in's project-tree contributor turns the game's .sdpkg into an Assets subtree.
				var opened = await second.InvokeAsync("od.open-solution", game);
				Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
				JsonElement assets = default;
				bool shown = await OpenDevelopAppFixture.PollUntilAsync(async () =>
				{
					var browser = await second.InvokeAsync("od.project-browser-state", "FirstPersonShooter.Game");
					if (!browser.GetProperty("success").GetBoolean())
						return false;
					var found = Flatten(browser.GetProperty("project")).Where(n => n.GetProperty("name").GetString() == "Assets").ToList();
					if (found.Count == 0)
						return false;
					assets = found[0];
					return Flatten(assets).Any(n => n.GetProperty("name").GetString()!.EndsWith(".sdscene", StringComparison.OrdinalIgnoreCase));
				}, TimeSpan.FromSeconds(90));
				Assert.True(shown, "The installed add-in did not surface the game's .sdscene under Assets: " + assets);

				// Opt-in: opening the scene must bring the viewport up. macOS shows it through a Cocoa overlay window; Windows through Stride's own
				// HWND host, which only the Windows (WinForms editor) variant of the package carries, so the cross-platform package fails this on Windows.
				if (Environment.GetEnvironmentVariable("STRIDE_CHECK_VIEWPORT") == "1")
				{
					Assert.True(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), "STRIDE_CHECK_VIEWPORT needs macOS or Windows: there is no scene viewport on Linux yet.");
					string scene = Path.Combine(gameDir, "..", "Assets", "MainScene.sdscene");
					Assert.True(File.Exists(scene), "The template's scene is missing: " + scene);
					var openedScene = await second.InvokeAsync("od.open-file", Path.GetFullPath(scene));
					Assert.True(openedScene.GetProperty("opened").GetBoolean(), openedScene.ToString());
					JsonElement sceneStatus = default;
					bool running = await OpenDevelopAppFixture.PollUntilAsync(async () =>
					{
						sceneStatus = await second.InvokeAsync("od.stride.scene-status");
						return sceneStatus.TryGetProperty("running", out var r) && r.ValueKind == JsonValueKind.True;
					}, TimeSpan.FromSeconds(OperatingSystem.IsWindows() ? 240 : 120));
					Assert.True(running, "The scene viewport did not start in the installed add-in: " + sceneStatus + "\n" + second.Output);
				}
			}
		}
		finally
		{
			try { Directory.Delete(work, true); } catch (IOException) { }
			try
			{
				if (!hadSolution && File.Exists(generatedSolution))
					File.Delete(generatedSolution);
				if (!hadLauncher && Directory.Exists(generatedLauncher))
					Directory.Delete(generatedLauncher, true);
			}
			catch (IOException) { }
		}
	}

	static IEnumerable<JsonElement> Flatten(JsonElement node)
	{
		yield return node;
		if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
			foreach (var child in children.EnumerateArray())
				foreach (var descendant in Flatten(child))
					yield return descendant;
	}

	static IEnumerable<JsonElement> Entries(JsonElement state, string listType) =>
		state.GetProperty("lists").EnumerateArray()
			.Single(l => l.GetProperty("type").GetString() == listType)
			.GetProperty("entries").EnumerateArray();

	static async Task<JsonElement> WaitAsync(IsolatedIde ide, Func<JsonElement, bool> condition, string what)
	{
		JsonElement last = default;
		bool ok = await OpenDevelopAppFixture.PollUntilAsync(async () =>
		{
			last = await ide.InvokeAsync("od.addin-manager.state");
			return condition(last);
		}, TimeSpan.FromSeconds(120));
		Assert.True(ok, $"Timed out waiting for {what}: {last}\n{ide.Output}");
		return last;
	}

	/// <summary>api.github.com and the asset host in one loopback server, serving one release of the real package.</summary>
	sealed class FakeGitHub : IDisposable
	{
		readonly HttpListener listener = new();
		readonly string packagePath;
		readonly long length;
		readonly string digest;
		public Uri Base { get; }

		public FakeGitHub(string packagePath)
		{
			this.packagePath = packagePath;
			length = new FileInfo(packagePath).Length;
			using (var stream = File.OpenRead(packagePath))
				digest = "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
			int port;
			using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
			{
				probe.Start();
				port = ((IPEndPoint)probe.LocalEndpoint).Port;
			}
			Base = new Uri($"http://127.0.0.1:{port}/");
			listener.Prefixes.Add(Base.ToString());
			listener.Start();
			_ = Task.Run(Serve);
		}

		async Task Serve()
		{
			string assetPath = $"/{Repository}/releases/download/{Tag}/{AssetName}";
			while (listener.IsListening)
			{
				HttpListenerContext context;
				try { context = await listener.GetContextAsync(); }
				catch (Exception) { return; }
				try
				{
					string path = context.Request.Url!.AbsolutePath;
					if (path == $"/repos/{Repository}/releases")
					{
						context.Response.ContentType = "application/json";
						context.Response.OutputStream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]
						{
							new Dictionary<string, object?>
							{
								["tag_name"] = Tag, ["prerelease"] = false, ["draft"] = false,
								["body"] = "test release", ["published_at"] = "2026-11-17T10:00:00Z",
								["assets"] = new[]
								{
									new Dictionary<string, object?>
									{
										["name"] = AssetName, ["state"] = "uploaded", ["size"] = length, ["digest"] = digest,
										["browser_download_url"] = new Uri(Base, assetPath.TrimStart('/')).ToString(),
									},
								},
							},
						})));
					}
					else if (path == assetPath)
					{
						context.Response.ContentLength64 = length;
						using var stream = File.OpenRead(packagePath);
						stream.CopyTo(context.Response.OutputStream);
					}
					else
						context.Response.StatusCode = 404;
				}
				catch (Exception) { context.Response.StatusCode = 500; }
				finally { context.Response.Close(); }
			}
		}

		public void Dispose() => listener.Close();
	}
}
