using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Xml.Linq;
using Microsoft.Win32;

namespace XinDong.Services;

/// <summary>
/// 开机自启动服务：优先写当前用户 HKCU Run 项；如果注册表被策略 / 权限拦截，
/// 自动退回到「启动」文件夹快捷方式。两种方式都不需要管理员权限。
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "XinDong";
    private const string TaskName = "XinDong_AutoStart";

    /// <summary>开启或关闭当前用户的开机自启动。</summary>
    /// <param name="enabled">true 写入启动项；false 删除启动项。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>是否成功。</returns>
    public static bool TrySet(bool enabled, out string error)
    {
        error = string.Empty;

        if (!enabled)
        {
            var registryRemoved = TryRemoveRegistry(out var registryRemoveError);
            var shortcutRemoved = TryRemoveShortcut(out var shortcutRemoveError);
            var taskRemoved = TryRemoveScheduledTask(out var taskRemoveError);

            if (registryRemoved && shortcutRemoved && taskRemoved)
            {
                return true;
            }

            error = $"注册表：{registryRemoveError}；启动文件夹：{shortcutRemoveError}；计划任务：{taskRemoveError}";
            return false;
        }

        var exePath = ResolveExePath();
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            error = "找不到当前薪动 exe 的路径。";
            return false;
        }

        // 1) 首选 HKCU Run：最标准，不产生额外文件
        if (TryWriteRegistry(exePath, out var regError))
        {
            TryRemoveShortcut(out _);
            TryRemoveScheduledTask(out _);
            return true;
        }

        // 2) 注册表被拦截时，退回启动文件夹快捷方式
        if (TryWriteShortcut(exePath, out var shortcutWriteError))
        {
            TryRemoveScheduledTask(out _);
            error = string.Empty;
            return true;
        }

        // 3) 启动文件夹也被权限 / 策略拦截时，使用当前用户登录计划任务
        if (TryWriteScheduledTask(exePath, out var taskError))
        {
            TryRemoveShortcut(out _);
            error = string.Empty;
            return true;
        }

        error = $"注册表写入失败（{regError}）；启动文件夹快捷方式创建失败（{shortcutWriteError}）；计划任务创建失败（{taskError}）";
        return false;
    }

    /// <summary>当前程序中实际运行的 exe 路径。</summary>
    public static string ResolveExePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            !string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        try
        {
            var main = Process.GetCurrentProcess().MainModule;
            if (!string.IsNullOrWhiteSpace(main?.FileName) &&
                !string.Equals(Path.GetFileName(main.FileName), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                return main.FileName;
            }
        }
        catch
        {
            // 取不到进程路径时回退到 exe 同级目录
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "XinDong.exe");
    }

    /// <summary>读取系统中的实际启动项，兼容手动导入注册表后的状态同步。</summary>
    public static bool TryGetEnabled(out bool enabled, out string error)
    {
        enabled = false;
        error = string.Empty;
        var exePath = ResolveExePath();
        var registryRead = TryReadRegistry(out var command, out var registryError);
        if (registryRead && TargetsExecutable(command, exePath))
        {
            enabled = true;
            return true;
        }

        var shortcutRead = TryReadShortcut(out var target, out var shortcutError);
        if (shortcutRead && TargetsExecutable(target, exePath))
        {
            enabled = true;
            return true;
        }

        var taskRead = TryReadScheduledTask(out var taskXml, out var taskError);
        if (taskRead && taskXml is not null)
        {
            try
            {
                var document = XDocument.Parse(taskXml);
                XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                enabled = document.Root?.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value != "false"
                          && document.Descendants(ns + "Command")
                              .Any(element => TargetsExecutable(element.Value, exePath));
                if (enabled) return true;
            }
            catch (Exception ex)
            {
                taskRead = false;
                taskError = ex.Message;
            }
        }

        if (registryRead && shortcutRead && taskRead) return true;
        error = $"注册表：{registryError}；启动文件夹：{shortcutError}；计划任务：{taskError}";
        return false;
    }

    private static bool TargetsExecutable(string? command, string exePath) =>
        command is not null && string.Equals(
            Environment.ExpandEnvironmentVariables(command.Trim()).Trim('"'),
            exePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>刷新便携版路径，并以系统实际状态同步配置。</summary>
    public static bool RefreshPathIfEnabled(bool enabled)
    {
        if (enabled) TrySet(true, out _);
        return TryGetEnabled(out var actualEnabled, out _) ? actualEnabled : enabled;
    }

    /// <summary>
    /// 当前进程被系统权限彻底拦截时，生成可手动双击导入的 .reg 文件。
    /// 文件写在 exe 同级目录，用户双击启用 / 关闭即可。
    /// </summary>
    public static bool CreateManualRegistrationScripts(
        out string enablePath, out string disablePath, out string error)
    {
        enablePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "启用开机自启动.reg");
        disablePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "关闭开机自启动.reg");
        error = string.Empty;

        try
        {
            var exePath = ResolveExePath();
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                error = "找不到当前薪动 exe 的路径。";
                return false;
            }

            var escaped = exePath.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var enable = "Windows Registry Editor Version 5.00\r\n\r\n" +
                         "[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]\r\n" +
                         $"\"XinDong\"=\"\\\"{escaped}\\\"\"\r\n";
            var disable = "Windows Registry Editor Version 5.00\r\n\r\n" +
                          "[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]\r\n" +
                          "\"XinDong\"=-\r\n";

            File.WriteAllText(enablePath, enable, System.Text.Encoding.Unicode);
            File.WriteAllText(disablePath, disable, System.Text.Encoding.Unicode);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>当前进程被权限 / 沙盒拦截时，以管理员权限重新运行自身来写入启动项。</summary>
    public static bool TrySetElevated(bool enabled, out string error)
    {
        error = string.Empty;
        try
        {
            var exePath = ResolveExePath();
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                error = "找不到当前薪动 exe 的路径。";
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--set-startup=" + (enabled ? "on" : "off"),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                error = "无法启动管理员授权进程。";
                return false;
            }

            if (!process.WaitForExit(30000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                error = "管理员授权超时。";
                return false;
            }

            if (process.ExitCode == 0) return true;

            error = $"启动项设置进程返回错误（{process.ExitCode}），请检查当前用户的写入权限。";
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "已取消管理员授权。";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    #region 注册表

    private static bool TryWriteRegistry(string exePath, out string error)
        => TrySetRegistryValue("\"" + exePath + "\"", out error);

    private static bool TryRemoveRegistry(out string error)
        => TrySetRegistryValue(null, out error);

    private static bool TryReadRegistry(out string? command, out string error)
    {
        command = null;
        error = string.Empty;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            command = key?.GetValue(ValueName) as string;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TrySetRegistryValue(string? command, out string error)
    {
        error = string.Empty;
        // 已经处于目标状态时，不再申请写权限，避免把成功的手动导入误报为失败。
        if (TryReadRegistry(out var current, out _) &&
            string.Equals(current, command, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                                RunKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree,
                                RegistryRights.SetValue)
                            ?? (command is null ? null : Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true));

            if (command is null)
            {
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                if (key is null) throw new IOException("无法打开当前用户启动项注册表。");
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }

            if (TryReadRegistry(out current, out _) &&
                string.Equals(current, command, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            error = "操作后启动项与预期不一致。";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        return false;
    }

    #endregion

    #region 启动文件夹快捷方式

    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "XinDong.lnk");

    private static bool TryWriteShortcut(string exePath, out string error)
    {
        error = string.Empty;
        object? shell = null;
        object? shortcut = null;

        try
        {
            var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (string.IsNullOrWhiteSpace(startup))
            {
                error = "找不到当前用户的启动文件夹。";
                return false;
            }

            Directory.CreateDirectory(startup);

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                error = "系统不支持创建快捷方式（缺少 WScript.Shell）。";
                return false;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                error = "无法创建快捷方式宿主。";
                return false;
            }

            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(ShortcutPath);
            dynamic link = shortcut;

            link.TargetPath = exePath;
            link.WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
            link.WindowStyle = 1;
            link.Description = "薪动 XinDong";
            link.IconLocation = exePath + ",0";
            link.Save();

            return File.Exists(ShortcutPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (shortcut is not null)
            {
                try { Marshal.FinalReleaseComObject(shortcut); } catch { /* 忽略 COM 释放异常 */ }
            }

            if (shell is not null)
            {
                try { Marshal.FinalReleaseComObject(shell); } catch { /* 忽略 COM 释放异常 */ }
            }
        }
    }

    private static bool TryRemoveShortcut(out string error)
    {
        error = string.Empty;
        try
        {
            var path = ShortcutPath;
            File.Delete(path);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryReadShortcut(out string? targetPath, out string error)
    {
        targetPath = null;
        error = string.Empty;
        object? shell = null;
        object? shortcut = null;
        try
        {
            // GetAttributes 会保留“拒绝访问”错误，避免 File.Exists 把它当成不存在。
            File.GetAttributes(ShortcutPath);
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                            ?? throw new IOException("系统不支持读取启动快捷方式。");
            shell = Activator.CreateInstance(shellType);
            if (shell is null) throw new IOException("无法创建快捷方式宿主。");
            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(ShortcutPath);
            dynamic link = shortcut;
            targetPath = link.TargetPath;
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    #endregion

    #region 登录计划任务

    private static bool TryWriteScheduledTask(string exePath, out string error)
    {
        // 任务里必须给 exe 路径加引号，兼容带空格的目录
        var args = new[]
        {
            "/Create",
            "/TN", TaskName,
            "/TR", "\"" + exePath + "\"",
            "/SC", "ONLOGON",
            "/F"
        };

        return TryRunSchtasks(args, out error);
    }

    private static bool TryRemoveScheduledTask(out string error)
    {
        if (!TryReadScheduledTask(out var taskXml, out error)) return false;
        if (taskXml is null) return true;

        var deleteArgs = new[] { "/Delete", "/TN", TaskName, "/F" };
        if (!TryRunSchtasks(deleteArgs, out error)) return false;
        if (!TryReadScheduledTask(out taskXml, out error)) return false;
        if (taskXml is null) return true;

        error = "删除后计划任务仍然存在。";
        return false;
    }

    private static bool TryReadScheduledTask(out string? taskXml, out string error)
    {
        taskXml = null;
        error = string.Empty;
        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service")
                              ?? throw new IOException("系统不支持读取登录计划任务。");
            service = Activator.CreateInstance(serviceType);
            if (service is null) throw new IOException("无法连接任务计划程序。");
            dynamic scheduler = service;
            scheduler.Connect();
            folder = scheduler.GetFolder("\\");
            dynamic root = folder;
            task = root.GetTask(TaskName);
            dynamic registeredTask = task;
            taskXml = registeredTask.Xml;
            return true;
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002))
        {
            // 只有 ERROR_FILE_NOT_FOUND 才表示任务不存在，拒绝访问不能当作关闭成功。
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null) return;
        try { Marshal.FinalReleaseComObject(value); } catch { /* 忽略 COM 释放异常 */ }
    }

    /// <summary>调用系统 schtasks，避免依赖任何 NuGet 包。</summary>
    private static bool TryRunSchtasks(string[] args, out string error)
    {
        error = string.Empty;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                error = "无法启动 schtasks.exe。";
                return false;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                error = "schtasks.exe 执行超时。";
                return false;
            }

            var output = outputTask.GetAwaiter().GetResult();
            var errorOutput = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode == 0) return true;

            error = string.IsNullOrWhiteSpace(errorOutput) ? output.Trim() : errorOutput.Trim();
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    #endregion
}

