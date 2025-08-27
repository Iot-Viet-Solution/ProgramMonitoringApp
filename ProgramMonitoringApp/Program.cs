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
        static int count = 1;
        static int idleWaitingMillis;
        static int loopIntervalSec;
        static List<TargetProcess> targetProcesses;

        static void Main()
        {
            // Đọc cấu hình từ appsettings.json
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            idleWaitingMillis = int.Parse(config["Settings:IdleWaitingMillis"]);
            loopIntervalSec = int.Parse(config["Settings:LoopIntervalSec"]);
            targetProcesses = config.GetSection("TargetProcesses").Get<List<TargetProcess>>();

            while (true)
            {
                foreach (var process in targetProcesses)
                {
                    try
                    {
                        Console.SetCursorPosition(0, 0);
                        Console.Write("Thoi Gian Hien Tai: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                        bool isNotResponding = IsProcessNotResponding(process.Name);

                        if (!isNotResponding)
                        {
                            bool isRunning = IsProcessRunning(process.Name);

                            if (!isRunning)
                            {
                                // count++;
                                // Console.SetCursorPosition(0, count);
                                //Console.WriteLine( $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {process.Name} khong chay, khoi dong lai...");

                                KillProcess(process.Name);
                                StartProcess(process.Path);
                            }
                        }
                        else
                        {
                            // count++;
                            // Console.SetCursorPosition(0, count);
                            // Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {process.Name} khong phan hoi, khoi dong lai...");

                            KillProcess(process.Name);
                            StartProcess(process.Path);
                        }
                    }
                    catch (Exception ex)
                    {
                        count++;
                        Console.SetCursorPosition(0, count);
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - loi: {ex.Message}");
                    }
                }

                Thread.Sleep(TimeSpan.FromSeconds(loopIntervalSec));
            }
        }

        static bool IsProcessRunning(string processName)
        {
            Process[] processes = Process.GetProcessesByName(processName);
            return processes.Length > 0 && !processes[0].HasExited && processes[0].Responding;
        }

        static bool IsProcessNotResponding(string processName)
        {
            Process[] processes = Process.GetProcessesByName(processName);
            if (processes.Length > 0)
            {
                try
                {
                    if (processes[0].WaitForInputIdle(idleWaitingMillis))
                    {
                        return false;
                    }
                }
                catch (Exception)
                {
                }
            }

            return true;
        }

        static void KillProcess(string processName)
        {
            Process[] processes = Process.GetProcessesByName(processName);
            foreach (Process process in processes)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit();
                }
                catch (Exception ex)
                {
                    count++;
                    Console.SetCursorPosition(0, count);
                    Console.WriteLine(
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Khong the dung chuong trinh: {ex.Message}");
                }
            }
        }

        static void StartProcess(string processPath)
        {
            try
            {
                Process.Start(processPath);
            }
            catch (Exception ex)
            {
                count++;
                Console.SetCursorPosition(0, count);
                Console.WriteLine(
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Khong the khoi dong chuong trinh tai {processPath}: {ex.Message}");
            }
        }
    }

    class TargetProcess
    {
        public string Name { get; set; }
        public string Path { get; set; }
    }
}