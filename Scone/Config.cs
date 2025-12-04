namespace Scone;

public struct Config
{
	public string? fgfsPath = null;
	public string? fgelevPath = null;
	public string? OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
	public int DarkMode = 2;

	public Config() { }
}