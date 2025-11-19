namespace Scone;

public struct Config
{
	public string? OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
	public int DarkMode = 2;
	public bool Gltf = false;

    public Config()
    {
    }
}