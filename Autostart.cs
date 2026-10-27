using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace DropWheel;

// Start at sign-in through a Task Scheduler task of the current user. The
// Run registry key isn't enough: newer Windows 11 builds skip fresh Run
// entries without saying so. A per-user logon task needs no admin rights.
internal static class Autostart
{
    const string Name = "DropWheel";
    // A few seconds after sign-in, once the taskbar is up.
    const string Delay = "PT5S";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static string ExePath => Environment.ProcessPath!;

    public static bool IsEnabled => Schtasks("/Query", "/TN", Name).exit == 0;

    public static void Set(bool enabled)
    {
        try
        {
            RemoveRunValue();
            if (enabled)
            {
                CreateTask();
            }
            else
            {
                Schtasks("/Delete", "/TN", Name, "/F");
            }
        }
        catch (Exception ex)
        {
            Log.Write("Autostart change failed: " + ex.Message);
        }
    }

    // At every start: an old Run entry becomes the task, and a task that
    // points at another copy of the program is pointed here.
    public static void Repair()
    {
        try
        {
            bool hadRunValue = RemoveRunValue();
            var (exit, xml) = Schtasks("/Query", "/TN", Name, "/XML");
            bool taskHere = exit == 0 && xml.Contains(SecurityElement.Escape(ExePath), StringComparison.OrdinalIgnoreCase);
            if (hadRunValue || (exit == 0 && !taskHere))
            {
                CreateTask();
                Log.Write("Autostart task set to " + ExePath);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Repairing autostart failed: " + ex.Message);
        }
    }

    static void CreateTask()
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>{Name}: запуск при входе в Windows</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>{Delay}</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(ExePath)}</Command></Exec></Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), $"{Name}-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            var (exit, output) = Schtasks("/Create", "/TN", Name, "/XML", file, "/F");
            if (exit != 0)
            {
                throw new InvalidOperationException(output.Trim());
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    static bool RemoveRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(Name) == null)
        {
            return false;
        }
        key.DeleteValue(Name, false);
        return true;
    }

    static (int exit, string output) Schtasks(params string[] args)
    {
        var info = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);
        return (process.ExitCode, output);
    }
}
