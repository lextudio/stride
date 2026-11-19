namespace Stride.OpenDevelop.AddIn.Tests;

/// <summary>
/// Judges a screen capture of the scene viewport. Counting colours is not enough: a scene that renders with the right geometry but has lost its
/// materials' colours (seen with Vulkan under Lavapipe: the template's blue and yellow walls came out olive green, or grey) still has plenty of
/// shades. The FirstPersonShooter template's walls are blue and yellow, so a correct render has saturated pixels in at least two hue sectors that
/// are not neighbours; a single tint (green, grey) does not.
/// </summary>
internal static class PixelStats
{
	public readonly record struct Result(int Distinct, double DominantShare, string Hues, bool HasDistinctHues);

	/// <summary>Analyses pixels given as 0xRRGGBB, sampled by the caller.</summary>
	public static Result Analyse(IReadOnlyList<int> rgb)
	{
		var counts = new Dictionary<int, int>();
		// Six 60-degree hue sectors centred on red, yellow, green, cyan, blue, magenta.
		var sectors = new int[6];
		foreach (int c in rgb)
		{
			// Quantise to 5 bits per channel so noise and dithering do not count as distinct colours.
			int q = c & 0xF8F8F8;
			counts[q] = counts.GetValueOrDefault(q) + 1;

			int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
			int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
			if (max < 50 || (max - min) < 0.12 * max)
				continue; // dark or nearly grey: no reliable hue
			double h = max == r ? 60.0 * (((g - b) / (double)(max - min) + 6) % 6)
				: max == g ? 60.0 * ((b - r) / (double)(max - min) + 2)
				: 60.0 * ((r - g) / (double)(max - min) + 4);
			sectors[(int)(((h + 30) % 360) / 60)]++;
		}
		int total = Math.Max(1, rgb.Count);
		// A sector counts when at least 2% of all sampled pixels fall in it. Calibrated on captures: the correct Direct3D11 render has blue ~43%, yellow ~6%, red 3-5%;
		// the Vulkan/Lavapipe renders 85-90% in one sector (olive) or none (grey); the empty editor grid 82% in one.
		var populated = Enumerable.Range(0, 6).Where(i => sectors[i] >= 0.02 * total).ToArray();
		bool distinctHues = populated.Any(i => populated.Any(j => Math.Min(Math.Abs(i - j), 6 - Math.Abs(i - j)) >= 2));
		string[] names = { "red", "yellow", "green", "cyan", "blue", "magenta" };
		string hues = string.Join(", ", Enumerable.Range(0, 6).Select(i => $"{names[i]} {sectors[i] * 100.0 / total:F0}%"));
		return new Result(counts.Count, counts.Count == 0 ? 1.0 : counts.Values.Max() / (double)total, hues, distinctHues);
	}
}
