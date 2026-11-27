static class BundledFiles
{
    public static string TemplateDirectory { get; private set; } = "";

    public static void Prepare()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "Templates");
        if (File.Exists(Path.Combine(beside, "repair-panel.png")))
        {
            TemplateDirectory = beside;
            return;
        }

        string cache = Path.Combine(Path.GetTempPath(), "BonghwangWall", "Templates");
        Directory.CreateDirectory(cache);
        var assembly = typeof(BundledFiles).Assembly;
        int extracted = 0;
        foreach (string name in assembly.GetManifestResourceNames())
        {
            const string prefix = "templates/";
            string normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                continue;

            string relative = normalized[prefix.Length..];
            string destination = Path.Combine(cache, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using Stream? source = assembly.GetManifestResourceStream(name);
            if (source is null)
                continue;
            using var target = File.Create(destination);
            source.CopyTo(target);
            extracted++;
        }

        if (extracted == 0 || !File.Exists(Path.Combine(cache, "repair-panel.png")))
            throw new InvalidDataException("실행 파일 안에 화면 템플릿이 없습니다.");

        TemplateDirectory = cache;
    }
}
