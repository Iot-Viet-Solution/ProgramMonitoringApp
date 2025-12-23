using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Threading;
using System.Linq;

namespace ProgramMonitoringApp
{
    class Program
    {
        static int logLine = 1;
        static int? idleWaitingMillis;
        static int? loopIntervalSec;
        static List<TargetProcess>? targetProcesses;
        static Dictionary<string, string> processStatus = new Dictionary<string, string>(); // Theo doi trang thai process

        static void Main()
        {
            try
            {
                // Đọc cấu hình từ appsettings.json
                var config = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();

                var idleWaitingMillisStr = config["Settings:IdleWaitingMillis"];
                var loopIntervalSecStr = config["Settings:LoopIntervalSec"];
                idleWaitingMillis = int.TryParse(idleWaitingMillisStr, out var idleVal) ? idleVal : 1000;
                loopIntervalSec = int.TryParse(loopIntervalSecStr, out var loopVal) ? loopVal : 5;
                targetProcesses = config.GetSection("TargetProcesses").Get<List<TargetProcess>>() ?? new List<TargetProcess>();
                
                Log($"Da load {targetProcesses.Count} process tu config.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LOI: Khong the doc appsettings.json: {ex.Message}");
                Console.WriteLine("Nhan phim bat ky de thoat...");
                Console.ReadKey();
                return;
            }

            while (true)
            {
                Console.WriteLine($"Thoi Gian Hien Tai: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                foreach (var process in targetProcesses ?? new List<TargetProcess>())
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(process.Name))
                        {
                            Log("Process.Name bi null hoac rong, bo qua.");
                            continue;
                        }
                        var runningInstances = GetRunningProcesses(process.Name);
                        bool anyNotResponding = false;
                        bool anyRunning = runningInstances.Count > 0;
                        
                        try
                        {
                            foreach (var proc in runningInstances)
                            {
                                if (!IsProcessResponding(proc))
                                {
                                    anyNotResponding = true;
                                    break;
                                }
                            }
                        }
                        finally
                        {
                            // Dispose tat ca process objects de tranh memory leak
                            foreach (var proc in runningInstances)
                            {
                                proc?.Dispose();
                            }
                        }

                        if (anyNotResponding)
                        {
                            // Chi log khi trang thai thay doi
                            if (!processStatus.ContainsKey(process.Name) || processStatus[process.Name] != "not_responding")
                            {
                                Log($"{process.Name} khong phan hoi, dang kill va khoi dong lai...");
                                processStatus[process.Name] = "not_responding";
                            }
                            KillProcess(process.Name);
                            if (!string.IsNullOrWhiteSpace(process.Path))
                            {
                                StartProcess(process.Path);
                                processStatus[process.Name] = "restarted";
                            }
                            else
                                Log("Process.Path bi null hoac rong, khong the khoi dong.");
                        }
                        else if (!anyRunning)
                        {
                            // Chi log khi trang thai thay doi
                            if (!processStatus.ContainsKey(process.Name) || processStatus[process.Name] != "stopped")
                            {
                                Log($"{process.Name} khong chay, dang khoi dong lai...");
                                processStatus[process.Name] = "stopped";
                            }
                            // Khong can kill vi process da khong chay roi
                            if (!string.IsNullOrWhiteSpace(process.Path))
                            {
                                StartProcess(process.Path);
                                processStatus[process.Name] = "restarted";
                            }
                            else
                                Log("Process.Path bi null hoac rong, khong the khoi dong.");
                        }
                        else
                        {
                            // Process dang chay binh thuong
                            if (!processStatus.ContainsKey(process.Name) || processStatus[process.Name] != "running")
                            {
                                if (processStatus.ContainsKey(process.Name) && processStatus[process.Name] == "restarted")
                                {
                                    Log($"{process.Name} da khoi dong lai thanh cong va dang chay binh thuong.");
                                }
                                processStatus[process.Name] = "running";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Loi: {ex.Message}");
                    }
                }
                Thread.Sleep(TimeSpan.FromSeconds(loopIntervalSec ?? 5));
            }
        }

        static List<Process> GetRunningProcesses(string processName)
        {
            // Loai bo .exe extension neu co
            var normalizedName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) 
                ? processName.Substring(0, processName.Length - 4) 
                : processName;
            
            var list = new List<Process>();
            try
            {
                var processes = Process.GetProcessesByName(normalizedName);
                foreach (var proc in processes)
                {
                    try
                    {
                        if (!proc.HasExited)
                        {
                            list.Add(proc);
                        }
                        else
                        {
                            proc.Dispose();
                        }
                    }
                    catch
                    {
                        // Process co the da exit trong luc check
                        proc?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Loi khi lay danh sach process {normalizedName}: {ex.Message}");
            }
            return list;
        }

        static bool IsProcessResponding(Process proc)
        {
            try
            {
                // Kiem tra xem process co con ton tai khong
                if (proc.HasExited)
                    return false;
                
                // Neu la process GUI, kiem tra Responding
                // Neu la console app hoac service, chi can check HasExited
                try
                {
                    return proc.Responding;
                }
                catch (InvalidOperationException)
                {
                    // Process khong co main window (console app/service)
                    // Coi nhu dang responding neu chua exit
                    return !proc.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }

        static void KillProcess(string processName)
        {
            // Loai bo .exe extension neu co
            var normalizedName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) 
                ? processName.Substring(0, processName.Length - 4) 
                : processName;
            
            var processes = Process.GetProcessesByName(normalizedName);
            foreach (var process in processes)
            {
                try
                {
                    var pid = process.Id;
                    process.Kill();
                    process.WaitForExit(5000); // Doi toi da 5 giay
                    Log($"Da dung process: {normalizedName} (PID: {pid})");
                    process.Dispose();
                }
                catch (Exception ex)
                {
                    Log($"Khong the dung chuong trinh: {ex.Message}");
                }
            }
            
            // Doi them mot chut de dam bao process da cleanup hoan toan
            Thread.Sleep(1000);
        }

        static void StartProcess(string processPath)
        {
            try
            {
                // Kiem tra file co ton tai khong
                if (!File.Exists(processPath))
                {
                    Log($"LOI: File khong ton tai: {processPath}");
                    return;
                }
                
                var proc = Process.Start(processPath);
                if (proc != null)
                {
                    Log($"Da khoi dong process moi: {processPath} (PID: {proc.Id})");
                    
                    // Doi mot chut de process khoi dong hoan toan
                    if (idleWaitingMillis.HasValue && idleWaitingMillis.Value > 0)
                    {
                        Log($"Dang doi {idleWaitingMillis.Value}ms de process khoi dong hoan toan...");
                        Thread.Sleep(idleWaitingMillis.Value);
                    }
                }
                else
                {
                    Log($"LOI: Khong the khoi dong process: {processPath}");
                }
            }
            catch (Exception ex)
            {
                Log($"Khong the khoi dong chuong trinh tai {processPath}: {ex.Message}");
            }
        }
        static void Log(string message)
        {
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}");
            logLine++;
        }
    }

    class TargetProcess
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
    }
}