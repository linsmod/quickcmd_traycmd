// ============================================================================
// quickcmd — SkillResource.cs: 配套 agent skill 文档（SKILL.md）的读取与安装。
//   构建时以嵌入资源（LogicalName=skill.md）打进 quickcmd.shared.dll，
//   quickcmd / qchost / qcApp 三个客户端共享同一份。
// ============================================================================

using System.Reflection;
using System.Text;

namespace QuickCmd;

internal static class SkillResource
{
    private const string ResName = "skill.md";

    // 目标安装目录：~/.agents/skills/traycmd/
    public static string TargetDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agents", "skills", "traycmd");

    public static string TargetFile => Path.Combine(TargetDir, "SKILL.md");

    public static bool Exists() => File.Exists(TargetFile);

    // 读取嵌入资源文本；缺失返回 null
    public static string LoadText()
    {
        var asm = typeof(SkillResource).Assembly;
        using var s = asm.GetManifestResourceStream(ResName);
        if (s == null) return null;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static bool Available() =>
        typeof(SkillResource).Assembly.GetManifestResourceInfo(ResName) != null;

    // 把 skill.md 写到 ~/.agents/skills/traycmd/SKILL.md。
    // overwrite=false 且已存在 → 返回 false（不覆盖，调用方决定 -y 重试）。
    // 成功返回 true；ioError 非空表示出错原因。
    public static bool Install(bool overwrite, out string ioError)
    {
        ioError = null;
        var text = LoadText();
        if (text == null) { ioError = "可执行文件内未嵌入 skill.md"; return false; }
        try
        {
            var file = TargetFile;
            if (File.Exists(file) && !overwrite) return false;
            Directory.CreateDirectory(TargetDir);
            File.WriteAllText(file, text, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) { ioError = ex.Message; return false; }
    }
}
