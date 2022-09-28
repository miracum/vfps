namespace Vfps.Config;

public class CacheConfig
{
    public bool IsEnabled { get; set; }
    public TimeSpan AbsoluteExpiration { get; set; } = TimeSpan.FromMinutes(60);
    public int SizeLimit { get; set; } = 32;
}
