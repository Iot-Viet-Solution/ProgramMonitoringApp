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

namespace ProgramMonitoringApp
{
    class Program
    {
        static int logLine = 1;
        static int? idleWaitingMillis;
        static int? loopIntervalSec;
        static List<TargetProcess>? targetProcesses;
        static Dictionary<string, int> trackedPIDs = new Dictionary<string, int>(); // Theo doi PID cua process
        static Dictionary<string, string> processStatus = new Dictionary<string, string>(); // Theo doi trang thai process

        static async Task Main()
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

                        // 1. Kiem tra trang thai Process trong OS
                        var runningInstances = GetRunningProcesses(process.Name);
                        int instanceCount = runningInstances.Count;
                        bool anyNotResponding = false;
                        bool anyRunning = instanceCount > 0;
                        int currentPID = 0;
                        List<int> allPIDs = new List<int>();
                        
                        try
                        {
                            foreach (var proc in runningInstances)
                            {
                                try { allPIDs.Add(proc.Id); } catch { }
                            }
                            if (allPIDs.Count > 0) currentPID = allPIDs[0];
                        }
                        catch (Exception ex) { Log($"Loi khi lay PID: {ex.Message}"); }
                        
                        if (instanceCount > 1)
                        {
                            if (!processStatus.ContainsKey(process.Name) || processStatus[process.Name] != "multiple_instances")
                            {
                                processStatus[process.Name] = "multiple_instances";
                            }
                        }
                        
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
                            foreach (var proc in runningInstances) proc?.Dispose();
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
                            }

                            if (anyRunning)
                            {
                                KillProcess(process.Name);
                            }
                            
                            trackedPIDs.Remove(process.Name);
                            if (!string.IsNullOrWhiteSpace(process.Path))
                            {
                                StartProcess(process.Path, process.Name);
                                processStatus[process.Name] = "restarted";
                            }
                            else
                                Log("Process.Path bi null hoac rong, khong the khoi dong.");
                        }
                        else
                        {
                            // Process dang chay binh thuong
                            if (!trackedPIDs.ContainsKey(process.Name))
                            {
                                trackedPIDs[process.Name] = currentPID;
                                Log($"{process.Name} dang chay voi PID: {currentPID} (Signal OK)");
                                processStatus[process.Name] = "running";
                            }
                            else if (trackedPIDs[process.Name] != currentPID)
                            {
                                Log($"{process.Name} da KHOI DONG LAI: PID cu: {trackedPIDs[process.Name]} -> PID moi: {currentPID}");
                                trackedPIDs[process.Name] = currentPID;
                                processStatus[process.Name] = "running";
                            }
                            else
                            {
                                if (processStatus.ContainsKey(process.Name) && processStatus[process.Name] == "restarted")
                                {
                                    Log($"{process.Name} da khoi dong lai thanh cong va dang chay binh thuong (PID: {currentPID}).");
                                    processStatus[process.Name] = "running";
                                }
                                else
                                {
                                    processStatus[process.Name] = "running";
                                }
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
                var client = new MongoClient(process.MongoUri);
                var database = client.GetDatabase(process.DbName);
                var collection = database.GetCollection<BsonDocument>(process.CollectionName);

                var filter = !string.IsNullOrEmpty(process.MongoFilterJson)
                    ? MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonDocument>(process.MongoFilterJson)
                    : new BsonDocument();

                var sort = Builders<BsonDocument>.Sort.Descending(process.LastSignalField);
                var latestDoc = await collection.Find(filter).Sort(sort).Limit(1).FirstOrDefaultAsync();

                if (latestDoc == null)
                {
                    Log($"[MONGO][{process.Name}] Khong tim thay document.");
                    return false;
                }

                if (latestDoc.TryGetValue(process.LastSignalField, out var lastValue))
                {
                    DateTime lastDt;
                    if (lastValue.IsBsonDateTime) lastDt = lastValue.ToUniversalTime();
                    else if (lastValue.IsString) lastDt = DateTime.Parse(lastValue.AsString).ToUniversalTime();
                    else return false;

                    var age = DateTime.UtcNow - lastDt;
                    if (age.TotalMinutes > (process.ThresholdMinutes ?? 5))
                    {
                        Log($"[MONGO][{process.Name}] Mat tin hieu! Lan cuoi: {lastDt.ToLocalTime()} ({age.TotalMinutes:F1} phut truoc)");
                        return false;
                    }
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log($"[MONGO][{process.Name}] Loi: {ex.Message}");
                return true; // Tam thoi coi nhu OK de tranh restart lien tuc khi loi mang
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
                    trackedPIDs[processName] = proc.Id;
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
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}");
            logLine++;
        }
    }

    class TargetProcess
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
        
        // Mongo Monitor
        public bool? EnableMongoMonitor { get; set; }
        public string? MongoUri { get; set; }
        public string? DbName { get; set; }
        public string? CollectionName { get; set; }
        public string? LastSignalField { get; set; }
        public string? MongoFilterJson { get; set; }
        public int? ThresholdMinutes { get; set; }
    }
}
