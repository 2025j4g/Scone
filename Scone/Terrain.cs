namespace Scone;

using System.Diagnostics;
using System.Text.RegularExpressions;

public class FGElev : IDisposable
{
	private Process? _fgelevProcess;
	private StreamWriter _stdin;
	private StreamReader _stdout;
	private int _recordNumber = 0;

	public FGElev()
	{
		_fgelevProcess = Process.Start(new ProcessStartInfo
		{
			FileName = App.AppConfig.fgelevPath,
			Arguments = $"--expire 1000000 --fg-scenery \"{App.TempPath}\" --fg-root \"{App.AppConfig.fgdataPath}\"",
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		});

		_stdin = _fgelevProcess!.StandardInput;
		_stdout = _fgelevProcess.StandardOutput;
	}

	public float Probe(float latitude, float longitude)
	{
		_recordNumber++;
		string query = $"{_recordNumber} {longitude:F10} {latitude:F10}";
		_stdin.WriteLine(query);
		_stdin.Flush();

		// Read until we get a valid result
		while (true)
		{
			string line = _stdout.ReadLine()!;
			if (string.IsNullOrWhiteSpace(line))
				continue;

			string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

			// Skip debug messages
			if (parts[0].StartsWith("now", StringComparison.OrdinalIgnoreCase) ||
				parts[0].StartsWith("osg::registry", StringComparison.OrdinalIgnoreCase) ||
				parts[0].StartsWith("loaded", StringComparison.OrdinalIgnoreCase))
				continue;

			// Parse elevation (second element)
			if (parts.Length > 1 && float.TryParse(parts[1], out float elevation))
				return elevation;
		}
	}

	public void Dispose()
	{
		_stdin?.Close();
		_stdout?.Close();
		_fgelevProcess?.Kill();
		_fgelevProcess?.Dispose();
		GC.SuppressFinalize(this);
	}
}

public class Terrain
{
	private static readonly double[,] LatitudeIndex = { { 89, 12 }, { 86, 4 }, { 83, 2 }, { 76, 1 }, { 62, 0.5 }, { 22, 0.25 }, { 0, 0.125 } };
	private static readonly HttpClientHandler handler = new() { ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true };
	private static readonly HttpClient client = new(handler);
	private static readonly FGElev? fgelev = App.AppConfig.fgelevPath != null ? new FGElev() : null;
	public static double GetElevation(double latitude, double longitude)
	{
		int index = GetTileIndex(latitude, longitude);
		string lonHemi = longitude >= 0 ? "e" : "w";
		string latHemi = latitude >= 0 ? "n" : "s";
		string terrainDir = $"Terrain/{lonHemi}{Math.Abs(Math.Floor(longitude / 10)) * 10:000}{latHemi}{Math.Abs(Math.Floor(latitude / 10)) * 10:00}/{lonHemi}{Math.Abs(Math.Floor(longitude)):000}{latHemi}{Math.Abs(Math.Floor(latitude)):00}";
		string urlTopLevel = $"https://terramaster.flightgear.org/terrasync/ws3/{terrainDir}";
		try
		{
			if (!Directory.Exists(Path.Combine(App.TempPath, terrainDir)))
			{
				Directory.CreateDirectory(Path.Combine(App.TempPath, terrainDir));
			}
			byte[] stgData = [];
			if (!File.Exists(Path.Combine(App.TempPath, terrainDir, $"{index}.stg")))
			{
				stgData = client.GetByteArrayAsync($"{urlTopLevel}/{index}.stg").Result;
				File.WriteAllBytes(Path.Combine(App.TempPath, terrainDir, $"{index}.stg"), stgData);
				MatchCollection matches = new Regex(@"OBJECT (.+\.btg)", RegexOptions.Multiline).Matches(System.Text.Encoding.UTF8.GetString(stgData));
				Console.WriteLine($"Found {matches.Count} BTG files in index {index}");
				foreach (Match match in matches)
				{
					File.WriteAllBytes(Path.Combine(App.TempPath, terrainDir, match.Groups[1].Value), client.GetByteArrayAsync($"{urlTopLevel}/{match.Groups[1].Value}.gz").Result);
				}
			}
		}
		catch (AggregateException e)
		{
			Console.WriteLine($"Error fetching BTG data from {$"{urlTopLevel}/{index}.stg"}: {e.Message}");
		}
		float elevation = fgelev!.Probe((float)latitude, (float)longitude);
		Console.WriteLine($"Elevation: {elevation} meters");
		return elevation;
	}

	public static int GetTileIndex(double lat, double lon)
	{
		if (Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
		{
			Console.WriteLine("Latitude or longitude out of range");
			return 0;
		}
		else
		{
			double lookup = Math.Abs(lat);
			double tileWidth = 0;
			for (int i = 0; i < LatitudeIndex.Length; i++)
			{
				if (lookup >= LatitudeIndex[i, 0])
				{
					tileWidth = LatitudeIndex[i, 1];
					break;
				}
			}
			int baseX = (int)Math.Floor(Math.Floor(lon / tileWidth) * tileWidth);
			int x = (int)Math.Floor((lon - baseX) / tileWidth);
			int baseY = (int)Math.Floor(lat);
			int y = (int)Math.Truncate((lat - baseY) * 8);
			return ((baseX + 180) << 14) + ((baseY + 90) << 6) + (y << 3) + x;
		}
	}

	public static (double lat, double lon) GetLatLon(int tileIndex)
	{
		// Extract x, y, baseY, baseX from the tile index (reverse of GetTileIndex bit packing)
		// GetTileIndex packs as: ((baseX + 180) << 14) + ((baseY + 90) << 6) + (y << 3) + x
		int x = tileIndex & 0b111; // last 3 bits
		int y = (tileIndex >> 3) & 0b111; // next 3 bits (not 6!)
		int baseY = ((tileIndex >> 6) & 0b11111111) - 90; // next 8 bits, then subtract 90
		int baseX = (tileIndex >> 14) - 180; // remaining bits, then subtract 180

		// Determine the tileWidth for this latitude band
		double lookup = Math.Abs(baseY);
		double tileWidth = 0;
		for (int i = 0; i < LatitudeIndex.Length; i++)
		{
			if (lookup >= LatitudeIndex[i, 0])
			{
				tileWidth = LatitudeIndex[i, 1];
				break;
			}
		}

		// Reconstruct the coordinates (reverse of GetTileIndex coordinate calculation)
		double lat = baseY + y / 8.0;
		double lon = baseX + x * tileWidth;

		return (lat, lon);
	}
}