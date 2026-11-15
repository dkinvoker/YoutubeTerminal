using System.Reflection;
using YoutubeTerminal.Storage;

namespace YoutubeTerminal.Tests;

public class UninstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ytt-uninst-" + Guid.NewGuid().ToString("N"));
    private static string Legacy => OperatingSystem.IsWindows() ? "odinstaluj.cmd" : "odinstaluj.sh";

    public UninstallerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Name_IsUninstallRegardlessOfLanguage()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? "uninstall.cmd" : "uninstall.sh", Installer.UninstallerName);
    }

    [Fact]
    public void EmbeddedScript_ExistsUnderNewName()
    {
        var asm = typeof(Installer).Assembly;
        Assert.NotNull(asm.GetManifestResourceInfo(Installer.UninstallerName));
        Assert.Null(asm.GetManifestResourceInfo(Legacy));
    }

    [Fact]
    public void LegacyUninstaller_OursIsRemoved()
    {
        string old = Path.Combine(_dir, Legacy);
        File.WriteAllText(old, "@echo off\r\ntitle Deinstalator YouTube Terminal\r\nset \"DATA=%LOCALAPPDATA%\\YoutubeTerminal\"\r\n");
        Installer.RemoveLegacyUninstaller(_dir);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void LegacyUninstaller_ForeignFileIsKept()
    {
        string old = Path.Combine(_dir, Legacy);
        File.WriteAllText(old, "echo something else\n");
        Installer.RemoveLegacyUninstaller(_dir);
        Assert.True(File.Exists(old));
    }

    [Fact]
    public void EmbeddedScript_GetsActualExeName()
    {
        using var res = typeof(Installer).Assembly.GetManifestResourceStream(Installer.UninstallerName)!;
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        var text = System.Text.Encoding.UTF8.GetString(Installer.WithExeName(ms.ToArray(), "YoutubeTerminal-win-x64.exe"));
        Assert.Contains("YoutubeTerminal-win-x64.exe", text);
        Assert.DoesNotContain("__EXE_NAME__", text);
        Assert.True(Installer.IsOurUninstaller(text));
    }
}
