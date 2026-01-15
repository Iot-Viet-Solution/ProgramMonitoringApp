using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Bson;
using System.Runtime.InteropServices;
using System.Text;
using System.Net.Http;

namespace ProgramMonitoringApp
{
    class Program
    {
        // Windows API declarations
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);
        
        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr hWnd);
        
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        static int logLine = 1;
        static int? idleWaitingMillis;
        static int? loopIntervalSec;
        static List<TargetProcess>? targetProcesses;
        static Dictionary<string, Process?> trackedProcesses = new Dictionary<string, Process?>(); // Theo doi Process object
        static Dictionary<string, string> processStatus = new Dictionary<string, string>(); // Theo doi trang thai process
        static Dictionary<string, DateTime> lastRestartTimes = new Dictionary<string, DateTime>(); // Theo doi thoi gian restart gan nhat
        static Dictionary<string, MongoClient> mongoClients = new Dictionary<string, MongoClient>(); // Cache MongoClient
        static AppConfig? appConfig;
        static readonly HttpClient httpClient = new HttpClient();

        static async Task Main()
        {
            try
            {
                // Đọc cấu hình từ appsettings.json và config.json
                var builder = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .AddJsonFile("config.json", optional: false, reloadOnChange: true);

                var config = builder.Build();

                appConfig = config.Get<AppConfig>();
                
                idleWaitingMillis = appConfig?.Settings?.IdleWaitingMillis ?? 1000;
                loopIntervalSec = appConfig?.Settings?.LoopIntervalSec ?? 5;
                targetProcesses = appConfig?.TargetProcesses ?? new List<TargetProcess>();
                
                // Khoi tao Grace Period cho tat ca process ngay khi monitor bat dau
                foreach (var p in targetProcesses)
                {
                    if (!string.IsNullOrEmpty(p.Name))
                    {
                        lastRestartTimes[p.Name] = DateTime.Now;
                    }
                }

                Log($"Da load {targetProcesses.Count} process tu config. Grace period 2 phut da duoc kich hoat.");
                await SendNotification($":rocket: **ProgramMonitoringApp** đã khởi động tại **{appConfig?.Settings?.SiteName ?? "Unknown site"}**.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LOI: Khong the doc cau hinh: {ex.Message}");
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

                        // Kiem tra Grace Period sau khi restart
                        if (lastRestartTimes.ContainsKey(process.Name))
                        {
                            var timeSinceRestart = DateTime.Now - lastRestartTimes[process.Name];
                            if (timeSinceRestart < TimeSpan.FromMinutes(2))
                            {
                                Log($"{process.Name} đang trong thời gian chờ khởi động ({timeSinceRestart.TotalSeconds:F0}s), bỏ qua kiểm tra.");
                                continue;
                            }
                        }

                        // 1. Kiem tra trang thai Process
                        bool anyRunning = false;
                        bool anyNotResponding = false;
                        int currentPID = 0;
                        Process? currentProcess = null;
                        
                        // Kiem tra bang Window Title (neu co cau hinh)
                        if (!string.IsNullOrEmpty(process.WindowTitle))
                        {
                            IntPtr hWnd = FindWindow(null, process.WindowTitle);
                            if (hWnd != IntPtr.Zero && IsWindow(hWnd))
                            {
                                uint pid;
                                GetWindowThreadProcessId(hWnd, out pid);
                                try
                                {
                                    currentProcess = Process.GetProcessById((int)pid);
                                    anyRunning = true;
                                    currentPID = (int)pid;
                                    anyNotResponding = !IsProcessResponding(currentProcess);
                                }
                                catch { anyRunning = false; }
                            }
                        }

                        // Fallback: Kiem tra process da track truoc hoac GetProcessesByName neu WindowTitle fail
                        if (!anyRunning && trackedProcesses.ContainsKey(process.Name) && trackedProcesses[process.Name] != null)
                        {
                            var tracked = trackedProcesses[process.Name];
                            try
                            {
                                if (!tracked!.HasExited)
                                {
                                    anyRunning = true;
                                    currentPID = tracked.Id;
                                    currentProcess = tracked;
                                    anyNotResponding = !IsProcessResponding(tracked);
                                }
                            }
                            catch { }
                        }

                        // Update tracked process and its status
                        if (anyRunning)
                        {
                            trackedProcesses[process.Name] = currentProcess;
                        }
                        else
                        {
                            if (trackedProcesses.ContainsKey(process.Name)) trackedProcesses[process.Name]?.Dispose();
                            trackedProcesses[process.Name] = null;
                        }

                        // 2. Kiem tra tin hieu tu MongoDB (neu co cau hinh)
                        bool mongoSignalLost = false;
                        if (anyRunning && !anyNotResponding && process.EnableMongoMonitor == true)
                        {
                            mongoSignalLost = !(await IsMongoSignalOk(process));
                        }

                        // 3. Quyet dinh Restart
                        if (anyNotResponding || mongoSignalLost || !anyRunning)
                        {
                            string reason = anyNotResponding ? "khong phan hoi" : (mongoSignalLost ? "mat tin hieu MongoDB" : "khong chay");
                            
                            if (!processStatus.ContainsKey(process.Name) || processStatus[process.Name] != "restarting_" + reason)
                            {
                                Log($"{process.Name} {reason}, dang kill va khoi dong lai...");
                                processStatus[process.Name] = "restarting_" + reason;

                                // Gui notification khi restart
                                await SendNotification($":rotating_light: **{process.Name}** ({process.SignalType}) - {reason}\n" +
                                                      $"Hành động: Đã kill và đang khởi động lại.");
                            }

                            // Kill process cu (neu co)
                            if (currentProcess != null && !currentProcess.HasExited)
                            {
                                try
                                {
                                    int pid = currentProcess.Id;
                                    currentProcess.Kill();
                                    currentProcess.WaitForExit(3000);
                                    Log($"Da kill process PID: {pid}");
                                }
                                catch (Exception ex)
                                {
                                    Log($"Loi khi kill process: {ex.Message}");
                                }
                                finally
                                {
                                    currentProcess?.Dispose();
                                }
                            }

                            // Clear tracked process
                            if (trackedProcesses.ContainsKey(process.Name))
                            {
                                trackedProcesses[process.Name] = null;
                            }

                            // Start process moi
                            if (!string.IsNullOrWhiteSpace(process.Path))
                            {
                                StartProcess(process.Path, process.Name);
                                processStatus[process.Name] = "restarted";
                                lastRestartTimes[process.Name] = DateTime.Now;
                            }
                            else
                            {
                                Log("Process.Path bi null hoac rong, khong the khoi dong.");
                            }
                        }
                        else
                        {
                            // Process dang chay binh thuong
                            if (processStatus.ContainsKey(process.Name) && (processStatus[process.Name] == "restarted" || processStatus[process.Name].StartsWith("restarting_")))
                            {
                                Log($"{process.Name} đã hoạt động bình thường trở lại (PID: {currentPID}).");
                                processStatus[process.Name] = "running";
                                await SendNotification($":white_check_mark: **{process.Name}** ({process.SignalType}) đã hoạt động bình thường trở lại (PID: {currentPID}).");
                            }
                            else if (!processStatus.ContainsKey(process.Name) || processStatus[process.Name] != "running")
                            {
                                Log($"{process.Name} dang chay voi PID: {currentPID}");
                                processStatus[process.Name] = "running";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Loi xu ly process {process.Name}: {ex.Message}");
                    }
                }
                Thread.Sleep(TimeSpan.FromSeconds(loopIntervalSec ?? 5));
            }
        }

        static async Task<bool> IsMongoSignalOk(TargetProcess process)
        {
            if (process.EnableMongoMonitor != true || string.IsNullOrEmpty(process.MongoUri))
                return true;

            try
            {
                if (!mongoClients.ContainsKey(process.MongoUri))
                {
                    mongoClients[process.MongoUri] = new MongoClient(process.MongoUri);
                }
                var client = mongoClients[process.MongoUri];
                var database = client.GetDatabase(process.DbName);
                var collection = database.GetCollection<BsonDocument>(process.CollectionName);

                var filter = !string.IsNullOrEmpty(process.MongoFilterJson)
                    ? MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonDocument>(process.MongoFilterJson)
                    : new BsonDocument();

                var sort = Builders<BsonDocument>.Sort.Descending(process.LastSignalField ?? "LastSignal");
                var latestDoc = await collection.Find(filter).Sort(sort).Limit(1).FirstOrDefaultAsync();

                if (latestDoc == null)
                {
                    Log($"[MONGO][{process.Name}] Không tìm thấy dữ liệu với bộ lọc: {process.MongoFilterJson}");
                    return false;
                }

                if (latestDoc.TryGetValue(process.LastSignalField ?? "LastSignal", out var lastValue))
                {
                    DateTime lastDt;
                    if (lastValue.IsBsonDateTime) 
                    {
                        lastDt = lastValue.ToUniversalTime();
                    }
                    else if (lastValue.IsString) 
                    {
                        if (DateTime.TryParse(lastValue.AsString, out var parsed))
                            lastDt = parsed.ToUniversalTime();
                        else
                        {
                            Log($"[MONGO][{process.Name}] Không thể parse chuỗi thời gian: {lastValue.AsString}");
                            return false;
                        }
                    }
                    else if (lastValue.IsInt64 || lastValue.IsInt32 || lastValue.IsDouble)
                    {
                        // Truong hop la timestamp (seconds hoac milliseconds)
                        long timestamp = lastValue.IsDouble ? (long)lastValue.AsDouble : (lastValue.IsInt64 ? lastValue.AsInt64 : lastValue.AsInt32);
                        
                        // Neu timestamp > 10^12 thi la milliseconds (VD: 1768285144000)
                        // Neu timestamp < 10^11 thi la seconds (VD: 1768285144)
                        if (timestamp > 99999999999) 
                        {
                            lastDt = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime;
                        }
                        else 
                        {
                            lastDt = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
                        }
                    }
                    else 
                    {
                        Log($"[MONGO][{process.Name}] Kiểu dữ liệu không hợp lệ cho {process.LastSignalField}: {lastValue.BsonType}");
                        return false;
                    }

                    var nowUtc = DateTime.UtcNow;
                    var age = nowUtc - lastDt;
                    
                    if (age.TotalMinutes > (process.ThresholdMinutes ?? 5))
                    {
                        Log($"[MONGO][{process.Name}] MẤT TÍN HIỆU!");
                        Log($"  + Timestamp gốc: {lastValue.ToString()}");
                        Log($"  + Thời gian hiện tại (Local): {DateTime.Now:HH:mm:ss}");
                        Log($"  + Tín hiệu cuối (Local): {lastDt.ToLocalTime():HH:mm:ss}");
                        Log($"  + Độ trễ: {age.TotalMinutes:F1} phút (Ngưỡng: {process.ThresholdMinutes} phút)");
                        return false;
                    }
                    
                    // Log thanh cong (optional, co the tat neu qua nhieu log)
                    // Log($"[MONGO][{process.Name}] Tín hiệu OK. Độ trễ: {age.TotalMinutes:F1} phút.");
                    return true;
                }
                
                Log($"[MONGO][{process.Name}] Document không chứa trường {process.LastSignalField}");
                return false;
            }
            catch (Exception ex)
            {
                Log($"[MONGO][{process.Name}] Lỗi kết nối/truy vấn: {ex.Message}");
                return true; // Coi nhu OK de tranh restart khi mat mang
            }
        }

        static List<Process> GetRunningProcesses(string processName)
        {
            var normalizedName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) 
                ? processName.Substring(0, processName.Length - 4) : processName;
            
            var list = new List<Process>();
            try
            {
                var processes = Process.GetProcessesByName(normalizedName);
                foreach (var proc in processes)
                {
                    try { if (!proc.HasExited) list.Add(proc); else proc.Dispose(); }
                    catch { proc?.Dispose(); }
                }
            }
            catch (Exception ex) { Log($"Loi khi lay process {normalizedName}: {ex.Message}"); }
            return list;
        }

        static bool IsProcessResponding(Process proc)
        {
            try
            {
                if (proc.HasExited) return false;
                try { return proc.Responding; }
                catch (InvalidOperationException) { return !proc.HasExited; }
            }
            catch { return false; }
        }

        static void KillProcess(string processName)
        {
            var normalizedName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) 
                ? processName.Substring(0, processName.Length - 4) : processName;
            
            var processes = Process.GetProcessesByName(normalizedName);
            foreach (var process in processes)
            {
                try
                {
                    var pid = process.Id;
                    process.Kill();
                    process.WaitForExit(5000);
                    Log($"Da dung process: {normalizedName} (PID: {pid})");
                    process.Dispose();
                }
                catch (Exception ex) { Log($"Khong the dung process: {ex.Message}"); }
            }
            Thread.Sleep(1000);
        }

        static void StartProcess(string processPath, string processName)
        {
            try
            {
                if (!File.Exists(processPath)) { Log($"LOI: File khong ton tai: {processPath}"); return; }
                var proc = Process.Start(processPath);
                if (proc != null)
                {
                    Log($"Da khoi dong process: {processPath} (PID: {proc.Id})");
                    trackedProcesses[processName] = proc;
                    if (idleWaitingMillis.HasValue && idleWaitingMillis.Value > 0)
                    {
                        Log($"Dang doi {idleWaitingMillis.Value}ms...");
                        Thread.Sleep(idleWaitingMillis.Value);
                    }
                }
                else Log($"LOI: Khong the khoi dong process: {processPath}");
            }
            catch (Exception ex) { Log($"Khong the khoi dong tai {processPath}: {ex.Message}"); }
        }

        static void Log(string message)
        {
            string cleanMessage = RemoveAccents(message);
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {cleanMessage}");
            logLine++;
        }

        static string RemoveAccents(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder();

            foreach (var c in normalizedString)
            {
                var unicodeCategory = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder.ToString().Normalize(NormalizationForm.FormC).Replace("đ", "d").Replace("Đ", "D");
        }

        static async Task SendNotification(string content)
        {
            if (appConfig?.Notify?.Discord?.Enabled == true && !string.IsNullOrEmpty(appConfig.Notify.Discord.Webhook))
            {
                try
                {
                    var payload = new { content = content };
                    var json = JsonSerializer.Serialize(payload);
                    var response = await httpClient.PostAsync(appConfig.Notify.Discord.Webhook, 
                        new StringContent(json, Encoding.UTF8, "application/json"));
                    
                    if (response.IsSuccessStatusCode)
                        Log("[Discord] Da gui thong bao.");
                    else
                        Log($"[Discord] Gui thong bao that bai: {response.StatusCode}");
                }
                catch (Exception ex)
                {
                    Log($"[Discord] Loi khi gui thong bao: {ex.Message}");
                }
            }

            if (appConfig?.Notify?.Telegram?.Enabled == true && !string.IsNullOrEmpty(appConfig.Notify.Telegram.BotToken) && !string.IsNullOrEmpty(appConfig.Notify.Telegram.ChatId))
            {
                try
                {
                    var url = $"https://api.telegram.org/bot{appConfig.Notify.Telegram.BotToken}/sendMessage";
                    var payload = new { chat_id = appConfig.Notify.Telegram.ChatId, text = content };
                    var json = JsonSerializer.Serialize(payload);
                    var response = await httpClient.PostAsync(url, 
                        new StringContent(json, Encoding.UTF8, "application/json"));

                    if (response.IsSuccessStatusCode)
                        Log("[Telegram] Da gui thong bao.");
                    else
                        Log($"[Telegram] Gui thong bao that bai: {response.StatusCode}");
                }
                catch (Exception ex)
                {
                    Log($"[Telegram] Loi khi gui thong bao: {ex.Message}");
                }
            }
        }
    }

    class AppConfig
    {
        public GlobalSettings? Settings { get; set; }
        public NotifyConfig? Notify { get; set; }
        public List<TargetProcess>? TargetProcesses { get; set; }
    }

    class GlobalSettings
    {
        public int IdleWaitingMillis { get; set; }
        public int LoopIntervalSec { get; set; }
        public string? SiteName { get; set; }
    }

    class NotifyConfig
    {
        public DiscordConfig? Discord { get; set; }
        public TelegramConfig? Telegram { get; set; }
    }

    class DiscordConfig
    {
        public bool Enabled { get; set; }
        public string? Webhook { get; set; }
    }

    class TelegramConfig
    {
        public bool Enabled { get; set; }
        public string? BotToken { get; set; }
        public string? ChatId { get; set; }
    }

    class TargetProcess
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
        public string? WindowTitle { get; set; }  // Ten cua so de kiem tra
        
        // Mongo Monitor
        public bool? EnableMongoMonitor { get; set; }
        public string? SignalType { get; set; }  // Loai tin hieu (Nhiet/San xuat)
        public string? MongoUri { get; set; }
        public string? DbName { get; set; }
        public string? CollectionName { get; set; }
        public string? LastSignalField { get; set; }
        public string? MongoFilterJson { get; set; }
        public int? ThresholdMinutes { get; set; }
    }
}
