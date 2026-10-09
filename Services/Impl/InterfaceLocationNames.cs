namespace MacExplorer.Services.Impl;

internal static class InterfaceLocationNames
{
    public static string? Get(string path, string home)
    {
        var name = path.TrimEnd('/') switch
        {
            "/Applications" => "应用程序",
            "/Users" => "用户",
            _ when path == System.IO.Path.Combine(home, "Desktop") => "桌面",
            _ when path == System.IO.Path.Combine(home, "Documents") => "文稿",
            _ when path == System.IO.Path.Combine(home, "Downloads") => "下载",
            _ when path == System.IO.Path.Combine(home, "Pictures") => "图片",
            _ when path == System.IO.Path.Combine(home, "Music") => "音乐",
            _ when path == System.IO.Path.Combine(home, ".Trash") => "废纸篓",
            _ => null
        };
        return name == null ? null : LocalizationText.Get(name);
    }
}
