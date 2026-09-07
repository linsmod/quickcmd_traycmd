// ============================================================================
// quickcmd — StartupManager.cs: Windows 启动项管理
// ============================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QuickCmd;

// ---------------------------------------------------------------------------
// Windows 启动项管理
// ---------------------------------------------------------------------------
internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "quickcmd";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key == null) return false;
            var value = key.GetValue(AppName);
            return value != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled, Action<string, string> notifyCallback = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return false;

            if (enabled)
            {
                var exePath = Application.ExecutablePath;
                key.SetValue(AppName, "\"" + exePath + "\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
            return true;
        }
        catch (Exception ex)
        {
            notifyCallback?.Invoke("quickcmd", "设置启动项失败: " + ex.Message);
            return false;
        }
    }
}

