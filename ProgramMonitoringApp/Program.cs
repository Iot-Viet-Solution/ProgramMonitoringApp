using System;
using System.Diagnostics;

namespace ProgramMonitoringApp
{
    class Program
    {
        #region User variables
        static int count = 1;
        static int idleWaitingMillis = 6000;
        static int loopIntervalSec = 1;
        static string targetProcessName = "OpcDataCollectionApp"; // Thay tên chương trình cần theo dõi vào đây
        //static string targetProcessPath = @"C:\Users\OPC-MES\Downloads\win-x86\OpcDataCollectionApp.exe"; // Thay đường dẫn đến chương trình cần theo dõi vào đây
        static string targetProcessPath = @"D:\Offline-Cloud\VIOT\Projects\Takako\OpcDataCollectionApp\OpcDataCollectionApp\bin\x86\Debug\net6.0-windows\OpcDataCollectionApp.exe"; // Thay đường dẫn đến chương trình cần theo dõi vào đây
        #endregion

        static void Main()
        {
            while (true)
            {
                try
                {
                    // Di chuyển con trỏ ghi đến vị trí (0, 0) trên màn hình
                    Console.SetCursorPosition(0, 0);
                    Console.Write("Thoi Gian Hien Tai: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                    bool isNotResponding = IsProcessNotResponding(targetProcessName);

                    if (!isNotResponding)
                    {
                        bool isRunning = IsProcessRunning(targetProcessName);

						if (!isRunning)
						{
							count++;
							Console.SetCursorPosition(0, count);
							Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {targetProcessName} khong chay, khoi dong lai...");

                            // Kill chương trình đích nếu nó đang chạy
                            KillProcess(targetProcessName);

                            StartProcess(targetProcessPath);
                        }
                    }
                    else
                    {
                        count++;
                        Console.SetCursorPosition(0, count);
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {targetProcessName} khong phan hoi, khoi dong lai...");

                        // Kill chương trình đích nếu nó không phản hồi
                        KillProcess(targetProcessName);

                        StartProcess(targetProcessPath);
                    }

                }
                catch (Exception ex)
                {
                    count++;
                    Console.SetCursorPosition(0, count);
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - loi: {ex.Message}");
                }

                Thread.Sleep(TimeSpan.FromSeconds(loopIntervalSec)); // Chờ 5 giây (có thể điều chỉnh)
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
                    // Chờ tiến trình có trạng thái không phản hồi trong một khoảng thời gian nhất định
                    if (processes[0].WaitForInputIdle(idleWaitingMillis))
                    {
                        return false; // Tiến trình phản hồi
                    }
                }
                catch (Exception)
                {
                    // Lỗi xảy ra khi kiểm tra trạng thái không phản hồi
                }
            }
            return true; // Tiến trình không phản hồi hoặc không chạy
        }

        static void KillProcess(string processName)
        {
            Process[] processes = Process.GetProcessesByName(processName);
            foreach (Process process in processes)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(); // Chờ quá trình kết thúc trước khi tiếp tục
                }
                catch (Exception ex)
                {
                    count++;
                    Console.SetCursorPosition(0, count);
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Khong the dung chuong trinh: {ex.Message}");
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
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Khong the khoi dong chuong trinh: {ex.Message}");
            }
        }
    }

}

