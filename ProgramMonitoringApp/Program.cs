using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;

namespace ProgramMonitoringApp
{
    class Program
    {
        static int logLine = 1;
        static int? idleWaitingMillis;
        static int? loopIntervalSec;
        static List<TargetProcess>? targetProcesses;

        static void Main()
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
                        foreach (var proc in runningInstances)
                        {
                            if (!IsProcessResponding(proc))
                            {
                                anyNotResponding = true;
                                break;
                            }
                        }

                        if (anyNotResponding)
                        {
                            Log($"{process.Name} khong phan hoi, khoi dong lai...");
                            KillProcess(process.Name);
                            if (!string.IsNullOrWhiteSpace(process.Path))
                                StartProcess(process.Path);
                            else
                                Log("Process.Path bi null hoac rong, khong the khoi dong.");
                        }
                        else if (!anyRunning)
                        {
                            Log($"{process.Name} khong chay, khoi dong lai...");
                            KillProcess(process.Name);
                            if (!string.IsNullOrWhiteSpace(process.Path))
                                StartProcess(process.Path);
                            else
                                Log("Process.Path bi null hoac rong, khong the khoi dong.");
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
            var list = new List<Process>();
            foreach (var proc in Process.GetProcessesByName(processName))
            {
                if (!proc.HasExited)
                {
                    list.Add(proc);
                }
            }
            return list;
        }

        static bool IsProcessResponding(Process proc)
        {
            try
            {
                // Nếu là process GUI, kiểm tra Responding
                return proc.Responding;
            }
            catch
            {
                return false;
            }
        }

        static void KillProcess(string processName)
        {
            var processes = Process.GetProcessesByName(processName);
            foreach (var process in processes)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit();
                    Log($"Da dung process: {processName} (PID: {process.Id})");
                }
                catch (Exception ex)
                {
                    Log($"Khong the dung chuong trinh: {ex.Message}");
                }
            }
        }

        static void StartProcess(string processPath)
        {
            try
            {
                var proc = Process.Start(processPath);
                Log($"Da khoi dong process moi: {processPath} (PID: {proc?.Id})");
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