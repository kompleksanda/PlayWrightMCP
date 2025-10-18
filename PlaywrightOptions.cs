public sealed class PlaywrightOptions
{
    // Directory to use as Playwright user data dir (profile). If null, Playwright will use its default.
    public string? UserDataDir { get; set; }

    // Whether to launch browsers in headless mode.
    public bool Headless { get; set; } = false;
}
