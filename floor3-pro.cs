 using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ProfControl.PublicAPI;
using ProfControl.Model;
using ProfControl.Model.Interface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using System.Text;
using System.IO;
using System.Windows;
using System.Collections.Generic;
using ProfControl.PublicAPI.WebHttp;
using ProfControl.Items;
using ProfControl.Viewer.View;
using System.Collections;
using ProfControl.Modules.Property;
using ProfControl.Modules.ScriptEditor;
using System.Collections.Concurrent; // 引入并发集合

namespace ProfControl
{
    public partial class floor3pro : IAppScript
    {          
        
/// ==============================================================================<summary>货物产生+机器加工5.0=================================================================================
/// ===================================================================三层（粗加工层）：300-306产生货物1-4，307-334粗加工→货物5-8
/// ===================================================================二层（精加工层）：201-240精加工→货物9-12
/// ===================================================================一层（组装+输出）：105-114组装成品13(1x9+1x10)，115-124组装成品14(1x11+1x12)
/// ===================================================================                  100-104输出站，接收成品13/14
/// ===================================================================改进：【5.0】完整四层流水线：产生→粗加工→精加工→组装→输出
/// ==============================================================================</summary>==========================================================================================
        // 1. 定义站点与设备集合
        private int[] _sourceStations = { 300, 301, 302, 303, 304, 305, 306 }; // 产生器
        private int[] _processStations = { 201, 202, 203, 204, 205, 206, 207, 208, 209, 210, 211, 212, 213, 214, 215, 216, 217, 218, 219, 220, 221, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 234, 235, 236, 237, 238, 239, 240, 307, 308, 309, 310, 311, 312, 313, 314, 315, 316, 317, 318, 319, 320, 321, 322, 323, 324, 325, 326, 327 };    // 加工站
        private string[] _agvNames = { "Agv-0", "Agv-3", "Agv-4", "Agv-2", "Agv-5",  "Agv-6",  "Agv-1", "Agv-7", "Agv-8" };           // AGV 集合
        // 粗加工站（三楼 307-334）：接收来自产生器的未加工货物
        private int[] _roughProcessStations = { 307, 308, 309, 310, 311, 312, 313, 314, 315, 316, 317, 318, 319, 320, 321, 322, 323, 324, 325, 326, 327, 328, 329, 330, 331, 332, 333, 334 };
        // 精加工站（二楼 201-240）：仅接收已完成粗加工的货物
        private int[] _fineProcessStations = { 201, 202, 203, 204, 205, 206, 207, 208, 209, 210, 211, 212, 213, 214, 215, 216, 217, 218, 219, 220, 221, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 234, 235, 236, 237, 238, 239, 240 };
        // 组装站（一层 105-124）：左区接收精加工货物9-12，右区存放成品13/14
        private int[] _assemblyStations13 = { 105, 106, 107, 108, 109, 110, 111, 112, 113, 114 }; // 组装成品13 (1x9+1x10)
        private int[] _assemblyStations14 = { 115, 116, 117, 118, 119, 120, 121, 122, 123, 124 }; // 组装成品14 (1x11+1x12)
        private int[] _allAssemblyStations = { 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124 };
        // 输出站（一层 100-104）：接收成品13/14
        private int[] _outputStations = { 100, 101, 102, 103, 104 };
        // AGV当前任务类型: "粗加工"/"精加工"/"组装"/"输出"
        private Dictionary<string, string> _agvMissionType = new Dictionary<string, string>();

        // ========== 5.0 货物体系：12种货物 + 2种成品 + 加工转换链 ==========
        // 产生站 → 货物1-4; 粗加工: 1→5,2→6,3→7,4→8; 精加工: 5→9,6→10,7→11,8→12
        private string[] _allSourceCargos = { "货物1", "货物2", "货物3", "货物4" };
        private string[] _allRoughCargos = { "货物5", "货物6", "货物7", "货物8" };
        private string[] _allFineCargos = { "货物9", "货物10", "货物11", "货物12" };
        private string[] _allProductCargos = { "成品13", "成品14" };
        private static readonly Dictionary<string, string> _roughTransform = new Dictionary<string, string>
        {
            {"货物1", "货物5"}, {"货物2", "货物6"}, {"货物3", "货物7"}, {"货物4", "货物8"}
        };
        private static readonly Dictionary<string, string> _fineTransform = new Dictionary<string, string>
        {
            {"货物5", "货物9"}, {"货物6", "货物10"}, {"货物7", "货物11"}, {"货物8", "货物12"}
        };
        // 组装转换: 成品13←(货物9+货物10), 成品14←(货物11+货物12)
        private static readonly Dictionary<string, (string cargoA, string cargoB)> _assemblyRecipe = new Dictionary<string, (string, string)>
        {
            {"成品13", ("货物9", "货物10")},
            {"成品14", ("货物11", "货物12")}
        };

        // 2. 指数分布参数
        private double _lambdaArrival = 0.05; // 产生间隔
        private double _lambdaRoughProcess = 0.01; // 粗加工 (期望~100s, 钳位30-300s)
        private double _lambdaFineProcess = 0.005; // 精加工 (期望~200s, 钳位60-500s)
        private double _lambdaAssembly = 0.005; // 组装 (期望~200s, 钳位60-600s)
        private double _lambdaArrival4 = 0.05; // 货物4产生间隔 (期望~20s, 钳位10-30s)
        private Random _rand = new Random();

        // 3. 存储各站点的独立计时器与状态
        private Dictionary<int, double> _arrivalTimers = new Dictionary<int, double>();
        private Dictionary<int, double> _processTimers = new Dictionary<int, double>();
        private Dictionary<int, bool> _isProcessingMap = new Dictionary<int, bool>();
        private Dictionary<int, double> _assemblyTimers = new Dictionary<int, double>();
        private Dictionary<int, bool> _isAssemblyMap = new Dictionary<int, bool>();
        private Dictionary<string, string> _agvStatus = new Dictionary<string, string>();
        
        // 在类成员变量区增加：
        private Dictionary<string, string> _agvCurrentSource = new Dictionary<string, string>();
        private Dictionary<string, string> _agvCurrentProc = new Dictionary<string, string>();
        // 配送计划队列：每个AGV维护一个待配送站点队列
        private Dictionary<string, Queue<(int stationId, int[] counts)>> _agvDeliveryPlan = new Dictionary<string, Queue<(int stationId, int[] counts)>>();

        // ========== 手动控制模式变量 ==========
        private enum ManualTaskState { Idle, GoingToSource, GoingToTarget }
        private bool _manualMode = false;
        private ConcurrentQueue<string> _manualCmdQueue = new ConcurrentQueue<string>();
        private string _manualStep = "idle";
        private string _manualAgvName = "";
        private string _manualSrcStation = "";
        private string _manualTgtStation = "";
        private string _manualMissionType = "";
        private string _manualElevatorSide = "";
        private ManualTaskState _manualTaskState = ManualTaskState.Idle;
        public void Main()
        {
            API.StartVirtualSim();

            // ========== 手动控制模式：HTTP 命令端点 + 控制面板 ==========
            try
            {
                // HTML 控制面板页面路径
                string htmlPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "script", "floor3-pro", "manual-control.html");
                string htmlContent = "<html><body><h1>控制面板文件未找到</h1></body></html>";
                try { htmlContent = File.ReadAllText(htmlPath); }
                catch { Tools.trace($"[手动模式] 未找到HTML面板文件: {htmlPath}"); }

                API.Http("http://localhost:58080", app =>
                {
                    // 首页 → HTML 控制面板
                    app.MapGet("/", async ctx =>
                    {
                        ctx.Response.ContentType = "text/html; charset=utf-8";
                        await ctx.Response.WriteAsync(htmlContent);
                    });

                    // 命令端点
                    app.MapGet("/manual-cmd", async ctx =>
                    {
                        try
                        {
                            string cmd = System.Net.WebUtility.UrlDecode(ctx.Request.Query["cmd"].ToString().Trim());
                            if (!string.IsNullOrWhiteSpace(cmd))
                            {
                                // 模式切换命令：同步执行，保证 refreshStatus 能立即看到结果
                                if (cmd.ToLower() == "manual on" || cmd.ToLower() == "manual off")
                                {
                                    ProcessManualCommand(cmd);
                                    await ctx.Response.WriteAsync("OK");
                                }
                                else
                                {
                                    _manualCmdQueue.Enqueue(cmd);
                                    await ctx.Response.WriteAsync("OK");
                                }
                            }
                            else
                            {
                                await ctx.Response.WriteAsync("MISSING_CMD");
                            }
                        }
                        catch (Exception ex)
                        {
                            await ctx.Response.WriteAsync($"ERROR: {ex.Message}");
                        }
                    });

                    // 状态查询端点 (供面板轮询)
                    app.MapGet("/status", async ctx =>
                    {
                        try
                        {
                            var agvInfos = new JArray();
                            foreach (var name in _agvNames)
                            {
                                string position = "?";
                                try
                                {
                                    var agv = API.GetAgv(name);
                                    position = agv != null ? (agv.API.On ?? "?") : "?";
                                }
                                catch { /* 单个AGV查询失败不影响整体 */ }
                                string status = _agvStatus.ContainsKey(name) ? _agvStatus[name] : "未知";
                                agvInfos.Add(new JObject
                                {
                                    ["name"] = name,
                                    ["status"] = status,
                                    ["position"] = position
                                });
                            }
                            var result = new JObject
                            {
                                ["mode"] = _manualMode ? "manual" : "auto",
                                ["agvs"] = agvInfos
                            };
                            ctx.Response.ContentType = "application/json; charset=utf-8";
                            await ctx.Response.WriteAsync(result.ToString());
                        }
                        catch (Exception ex)
                        {
                            await ctx.Response.WriteAsync($"{{\"error\":\"{ex.Message}\"}}");
                        }
                    });
                    
                    // ========== 统一仪表盘端点：返回 AGV + 全楼层 + 电梯 ==========
                    app.MapGet("/dashboard", async ctx =>
                    {
                        try
                        {
                            // 1) AGV状态
                            var agvInfos = new JArray();
                            foreach (var name in _agvNames)
                            {
                                string position = "?";
                                try
                                {
                                    var agv = API.GetAgv(name);
                                    position = agv != null ? (agv.API.On ?? "?") : "?";
                                }
                                catch { }
                                string status = _agvStatus.ContainsKey(name) ? _agvStatus[name] : "未知";
                                agvInfos.Add(new JObject
                                {
                                    ["name"] = name,
                                    ["status"] = status,
                                    ["position"] = position
                                });
                            }
                            
                            // 2) 全楼层站点状态
                            var floors = new JObject();
                            
                            // 3F: 产生站 + 粗加工站
                            var floor3Stations = new JArray();
                            foreach (var id in _sourceStations.Concat(_roughProcessStations))
                            {
                                var sta = API.GetStation(id.ToString());
                                if (sta == null) continue;
                                string leftAreaName = _sourceStations.Contains(id) ? "输入机器" : "加工机器左区";
                                var leftArea = sta.CargoAreas.FirstOrDefault(a => a.Name == leftAreaName);
                                var rightArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                                floor3Stations.Add(new JObject
                                {
                                    ["mark"] = id.ToString(),
                                    ["type"] = GetStationType(id),
                                    ["leftQty"] = leftArea?.Quanlity ?? 0,
                                    ["rightQty"] = rightArea?.Quanlity ?? 0
                                });
                            }
                            floors["3"] = floor3Stations;
                            
                            // 2F: 精加工站
                            var floor2Stations = new JArray();
                            foreach (var id in _fineProcessStations)
                            {
                                var sta = API.GetStation(id.ToString());
                                if (sta == null) continue;
                                var leftArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器左区");
                                var rightArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                                floor2Stations.Add(new JObject
                                {
                                    ["mark"] = id.ToString(),
                                    ["type"] = "精加工站",
                                    ["leftQty"] = leftArea?.Quanlity ?? 0,
                                    ["rightQty"] = rightArea?.Quanlity ?? 0
                                });
                            }
                            floors["2"] = floor2Stations;
                            
                            // 1F: 组装站 + 输出站
                            var floor1Stations = new JArray();
                            foreach (var id in _allAssemblyStations.Concat(_outputStations))
                            {
                                var sta = API.GetStation(id.ToString());
                                if (sta == null) continue;
                                string leftAreaName = _outputStations.Contains(id) ? "输出机器" : "加工机器左区";
                                var leftArea = sta.CargoAreas.FirstOrDefault(a => a.Name == leftAreaName);
                                var rightArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                                floor1Stations.Add(new JObject
                                {
                                    ["mark"] = id.ToString(),
                                    ["type"] = GetStationType(id),
                                    ["leftQty"] = leftArea?.Quanlity ?? 0,
                                    ["rightQty"] = rightArea?.Quanlity ?? 0
                                });
                            }
                            floors["1"] = floor1Stations;
                            
                            // 3) 电梯状态
                            var leftElev = new JArray();
                            var rightElev = new JArray();
                            foreach (var mark in new[] { "398", "298", "198" })
                            {
                                var sta = API.GetStation(mark);
                                if (sta == null) continue;
                                leftElev.Add(new JObject
                                {
                                    ["floor"] = GetStationFloor(mark),
                                    ["mark"] = mark,
                                    ["hasAgv"] = sta.AgvOns.Any(),
                                    ["agvNames"] = string.Join(",", sta.AgvOns.Select(a => a.Name))
                                });
                            }
                            foreach (var mark in new[] { "399", "299", "199" })
                            {
                                var sta = API.GetStation(mark);
                                if (sta == null) continue;
                                rightElev.Add(new JObject
                                {
                                    ["floor"] = GetStationFloor(mark),
                                    ["mark"] = mark,
                                    ["hasAgv"] = sta.AgvOns.Any(),
                                    ["agvNames"] = string.Join(",", sta.AgvOns.Select(a => a.Name))
                                });
                            }
                            
                            var result = new JObject
                            {
                                ["mode"] = _manualMode ? "manual" : "auto",
                                ["agvs"] = agvInfos,
                                ["floors"] = floors,
                                ["elevator"] = new JObject
                                {
                                    ["left"] = leftElev,
                                    ["right"] = rightElev
                                }
                            };
                            ctx.Response.ContentType = "application/json; charset=utf-8";
                            await ctx.Response.WriteAsync(result.ToString());
                        }
                        catch (Exception ex)
                        {
                            await ctx.Response.WriteAsync($"{{\"error\":\"{ex.Message}\"}}");
                        }
                    });
                    
                });  // close API.Http
                
                Tools.trace("[手动模式] HTTP端点已注册:");
                Tools.trace("  控制面板: http://localhost:58080/");
                Tools.trace("  命令API:  http://localhost:58080/manual-cmd?cmd=...");
                Tools.trace("  仪表盘API: http://localhost:58080/dashboard");
            }
            catch (Exception ex)
            {
                Tools.trace($"[手动模式] HTTP端点注册失败: {ex.Message}");
            }

            Tools.trace("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Tools.trace("  手动控制模式已就绪");
            Tools.trace("  输入 'manual on'   → 切换到手动模式");
            Tools.trace("  输入 'manual off'  → 切换到自动模式");
            Tools.trace("  手动模式下会自动弹出任务提示");
            Tools.trace("  输入格式: [agv编号] [目标站地标]");
            Tools.trace("  例如: 0 315  (让Agv-0去粗加工站315)");
            Tools.trace("  跨楼层: 0 220 left  (走左梯)");
            Tools.trace("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

            // 初始化所有站点的计时器

            foreach (var id in _sourceStations) _arrivalTimers[id] = _rand.Next(1, 10);
            foreach (var id in _processStations)
            {
                _processTimers[id] = 0;
                _isProcessingMap[id] = false;
            }
            foreach (var id in _allAssemblyStations)
            {
                _assemblyTimers[id] = 0;
                _isAssemblyMap[id] = false;
            }
            foreach (var name in _agvNames) _agvStatus[name] = "等待任务";

//            // 为所有加工站设置区域数量限制为5（每个区域最多5个货物）
//            foreach (var id in _processStations)
//            {
//                var sta = API.GetStation(id.ToString());
//                if (sta == null) continue;
//                var leftArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器左区");
//                if (leftArea != null) { leftArea.Info.EnableLimitedQuanlity = true; leftArea.Info.LimitedQuanlity = 5; }
//                var rightArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
//                if (rightArea != null) { rightArea.Info.EnableLimitedQuanlity = true; rightArea.Info.LimitedQuanlity = 5; }
//            }

            // 为组装站设置区域数量限制为5
//            foreach (var id in _allAssemblyStations)
//            {
//                var sta = API.GetStation(id.ToString());
//                if (sta == null) continue;
//                var leftArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器左区");
//                if (leftArea != null) { leftArea.Info.EnableLimitedQuanlity = true; leftArea.Info.LimitedQuanlity = 5; }
//                var rightArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
//                if (rightArea != null) { rightArea.Info.EnableLimitedQuanlity = true; rightArea.Info.LimitedQuanlity = 5; }
//            }
//
//            // 为输出站设置输出机器区域容量限制为10
//            foreach (var id in _outputStations)
//            {
//                var sta = API.GetStation(id.ToString());
//                if (sta == null) continue;
//                var outArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "输出机器");
//                if (outArea != null) { outArea.Info.EnableLimitedQuanlity = true; outArea.Info.LimitedQuanlity = 10; }
//            }

            // 为所有AGV的agv缓冲区设置容量限制为5
            foreach (var name in _agvNames)
            {
                var agv = API.GetAgv(name);
                if (agv == null) continue;
                var bufArea = agv.CargoAreas.FirstOrDefault(a => a.Name == "agv缓冲区");
                if (bufArea != null) { bufArea.Info.EnableLimitedQuanlity = true; bufArea.Info.LimitedQuanlity = 5; }
            }

            foreach (var name in _agvNames) {
                _agvCurrentSource[name] = "";
                _agvCurrentProc[name] = "";
                _agvDeliveryPlan[name] = new Queue<(int, int[])>();
                _agvMissionType[name] = "";
            }
            // ==========================================
            // 逻辑 E：手动交通管制 - 楼层桥接点控制
            // 左右电梯分别独立管制，防止互锁
            // 左电梯组: 198(1F) ↔ 298(2F) ↔ 398(3F)
            // 右电梯组: 199(1F) ↔ 299(2F) ↔ 399(3F)
            // ==========================================
            // --- 左电梯管制 ---
            var leftElevatorMarks = new List<string> { "198", "298", "398" };
            var leftElevatorStations = leftElevatorMarks
                .Select(m => API.GetStation(m))
                .Where(s => s != null)
                .Cast<IStation>()
                .ToList();
            Tools.trace($"左电梯桥接交管站点数: {leftElevatorStations.Count}");

            var leftChannelMarks = new HashSet<string> { "198", "298", "398" };  // 左电梯通道所有站点
            API.ManualTrafficControl(leftElevatorStations, (route, sta) =>
            {
                var agv = route.Agv;
                var staMark = sta.Mark;

                // === 入口/出口站点（398为3F左梯，198为1F左梯）：通道有车则拦截 ===
                // 注意：398既是下行入口又是上行出口，所以不能把车堵在398上，
                // 否则上行到达398的AGV出不来！
                if (staMark == "398" || staMark == "198")
                {
                    // 检查通道内其他站点是否有其他AGV（排除自己）
                    bool channelOccupied = leftChannelMarks
                        .Where(m => m != staMark)
                        .Select(m => API.GetStation(m))
                        .Where(s => s != null)
                        .Any(s => s.AgvOns.Any(a => a != agv));

                    if (channelOccupied)
                    {
                        // ★ 关键：不退到398，而是退到AGV来时的前一站等待（如303）
                        // 这样不占用398出口，上行AGV可以从298正常到达398并离开
                        var prevSta = route?.GetControlPointToTargetSta(sta, -1);
                        if (prevSta != null)
                            return (true, prevSta);  // 退回到前一站等待
                        return (true, sta);           // 无前一站时停在原地（兜底）
                    }
                }
                // === 中间站点（298）：已在通道内，直接放行，避免死锁 ===

                return (false, null);
            });

            // --- 右电梯管制 ---
            var rightElevatorMarks = new List<string> { "199", "299", "399" };
            var rightElevatorStations = rightElevatorMarks
                .Select(m => API.GetStation(m))
                .Where(s => s != null)
                .Cast<IStation>()
                .ToList();
            Tools.trace($"右电梯桥接交管站点数: {rightElevatorStations.Count}");

            var rightChannelMarks = new HashSet<string> { "199", "299", "399" };  // 右电梯通道所有站点
            API.ManualTrafficControl(rightElevatorStations, (route, sta) =>
            {
                var agv = route.Agv;
                var staMark = sta.Mark;

                // === 入口/出口站点（399为3F右梯，199为1F右梯）：通道有车则退回到前一站 ===
                if (staMark == "399" || staMark == "199")
                {
                    bool channelOccupied = rightChannelMarks
                        .Where(m => m != staMark)
                        .Select(m => API.GetStation(m))
                        .Where(s => s != null)
                        .Any(s => s.AgvOns.Any(a => a != agv));

                    if (channelOccupied)
                    {
                        // 退回到来时的前一站等待，不堵出口
                        var prevSta = route?.GetControlPointToTargetSta(sta, -1);
                        if (prevSta != null)
                            return (true, prevSta);
                        return (true, sta);
                    }
                }

                return (false, null);
            });

            // ==========================================
            // 逻辑 A：多站点独立货物产生器
            // ==========================================
            API.SetInterval(tc =>
            {
                foreach (var id in _sourceStations)
                {
                    _arrivalTimers[id] -= 1;
                    if (_arrivalTimers[id] <= 0)
                    {
                        int idx = _rand.Next(0, 4);
                        var sta = API.GetStation(id.ToString());
                        if (sta != null)
                        {
                            string cargoType = _allSourceCargos[idx]; // 货物1-4
                            sta.CargoPlace("输入机器", cargoType, 1);
                        }
                        // 计算下一次到达 (指数分布) - 货物4使用独立参数
                        double u = _rand.NextDouble();
                        if (idx == 3) // 货物4
                        {
                            _arrivalTimers[id] = -Math.Log(1 - u) / _lambdaArrival4;
                            _arrivalTimers[id] = Math.Clamp(_arrivalTimers[id], 10, 30);
                        }
                        else
                        {
                            _arrivalTimers[id] = -Math.Log(1 - u) / _lambdaArrival;
                            _arrivalTimers[id] = Math.Clamp(_arrivalTimers[id], 5, 60);
                        }
                    }
                }
            }, 1000);

            // ==========================================
            // 逻辑 B：多 AGV 任务调度 (5.0: 四层优先级)
            // ==========================================
            // 优先级: 输出 > 组装 > 精加工 > 粗加工
            API.SetInterval(tc =>
            {
                if (_manualMode) return; // ★ 手动模式下跳过自动调度
                foreach (var agvName in _agvNames)
                {
                    var agv = API.GetAgv(agvName);
                    if (agv == null) continue;

                    string currentStatus = _agvStatus[agvName];
                    

                    switch (currentStatus)
                    {
                        case "等待任务":
                            var agvBufCheck = agv.CargoAreas.FirstOrDefault(a => a.Name == "agv缓冲区");
                            int currentLoad = agvBufCheck != null ? agvBufCheck.Quanlity : 0;
                            if (currentLoad > 0)
                            {
                                string[] cargoNamesChk = GetCargoNamesForStageByMission(_agvMissionType[agvName]);
                                int[] bufCountsChk = GetBufferCargoCounts(agvBufCheck, cargoNamesChk);
                                int[] targetStationsChk = GetTargetStationsForMission(_agvMissionType[agvName]);
                                _agvDeliveryPlan[agvName] = _agvMissionType[agvName] == "输出"
                                    ? BuildOutputDeliveryPlan(bufCountsChk, targetStationsChk)
                                    : BuildDeliveryPlanForTarget(bufCountsChk, targetStationsChk);
                                if (_agvDeliveryPlan[agvName].Count > 0)
                                {
                                    var plan = _agvDeliveryPlan[agvName].Peek();
                                    _agvCurrentProc[agvName] = plan.stationId.ToString();
                                    _agvStatus[agvName] = "前往送货";
                                    Tools.trace($"{agvName} 缓冲区残留{currentLoad}件货物，重新分配配送任务: 首站{plan.stationId}");
                                }
                                else
                                {
                                    _agvStatus[agvName] = "等待卸货";
                                    Tools.trace($"{agvName} 缓冲区残留{currentLoad}件货物，但无可用的目标站，进入等待卸货");
                                }
                                break;
                            }
                            // ========== 5.0 四优先级调度 ==========
                            // Priority 1: 组装站右区有成品 → 送输出站
                            var availableAssemblyRight = _allAssemblyStations
                                .Where(s => {
                                    var sta = API.GetStation(s.ToString());
                                    if (sta == null) return false;
                                    var rightArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                                    return rightArea != null && rightArea.CargoChildren.Any();
                                })
                                .ToList();

                            if (availableAssemblyRight.Count > 0 && _rand.Next(0, 100) < 60)
                            {
                                int randomIndex = _rand.Next(availableAssemblyRight.Count);
                                int targetSource = availableAssemblyRight[randomIndex];
                                _agvCurrentSource[agvName] = targetSource.ToString();
                                _agvMissionType[agvName] = "输出";
                                _agvStatus[agvName] = "前往取货";
                                Tools.trace($"{agvName} [输出任务] 从组装站{targetSource}右区取成品, 送输出站");
                                break;
                            }

                            // Priority 2: 精加工站右区有货物9-12 → 送组装站
                            var availableFineRight = _fineProcessStations
                                .Where(s => {
                                    var sta = API.GetStation(s.ToString());
                                    if (sta == null) return false;
                                    var rightArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                                    return rightArea != null && rightArea.CargoChildren.Any();
                                })
                                .ToList();

                            if (availableFineRight.Count > 0 && _rand.Next(0, 100) < 60)
                            {
                                int randomIndex = _rand.Next(availableFineRight.Count);
                                int targetSource = availableFineRight[randomIndex];
                                _agvCurrentSource[agvName] = targetSource.ToString();
                                _agvMissionType[agvName] = "组装";
                                _agvStatus[agvName] = "前往取货";
                                Tools.trace($"{agvName} [组装任务] 从精加工站{targetSource}右区取货, 送组装站");
                                break;
                            }

                            // Priority 3: 粗加工站右区有货物5-8 → 送精加工站
                            var availableRoughRight = _roughProcessStations
                                .Where(s => {
                                    var sta = API.GetStation(s.ToString());
                                    if (sta == null) return false;
                                    var rightArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                                    return rightArea != null && rightArea.CargoChildren.Any();
                                })
                                .ToList();

                            if (availableRoughRight.Count > 0)
                            {
                                int randomIndex = _rand.Next(availableRoughRight.Count);
                                int targetSource = availableRoughRight[randomIndex];
                                _agvCurrentSource[agvName] = targetSource.ToString();
                                _agvMissionType[agvName] = "精加工";
                                _agvStatus[agvName] = "前往取货";
                                Tools.trace($"{agvName} [精加工任务] 从粗加工站{targetSource}右区取货, 送精加工站");
                                break;
                            }

                            // Priority 4: 产生站有货物 → 送粗加工站
                            var availableSources = _sourceStations
                                .Where(s => !API.GetStation(s.ToString()).IsEmpty())
                                .ToList();

                            if (availableSources.Count > 0)
                            {
                                int randomIndex2 = _rand.Next(availableSources.Count);
                                int targetSource2 = availableSources[randomIndex2];
                                _agvCurrentSource[agvName] = targetSource2.ToString();
                                _agvMissionType[agvName] = "粗加工";
                                _agvStatus[agvName] = "前往取货";
                                Tools.trace($"{agvName} [粗加工任务] 从产生站{targetSource2}取货, 送粗加工站");
                            }
                            break;

                        case "前往取货":
                            string srcId = _agvCurrentSource[agvName]; 
                            string missionType = _agvMissionType[agvName];
                            
                            if (string.IsNullOrEmpty(srcId)) {
                                _agvStatus[agvName] = "等待任务";
                                break;
                            }
                            // ★★★ 防重复导航：仅在AGV空闲且不在目标点时下发GoTo，避免每秒重算 ★★★
                            if (agv.Tasks.Count < 1 && agv.API.On != srcId)
                                agv.API.GoTo(srcId, null);
                            Tools.trace($"{agvName} 正在前往 {srcId}, 当前位置: {agv.API.On}");

                            if (agv.API.On == srcId) 
                            {
                                bool sourceHasCargo = false;
                                if (missionType == "输出")
                                {
                                    var srcSta = API.GetStation(srcId);
                                    var rightArea = srcSta?.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                                    sourceHasCargo = rightArea != null && rightArea.CargoChildren.Any();
                                }
                                else if (missionType == "组装")
                                {
                                    var srcSta = API.GetStation(srcId);
                                    var rightArea = srcSta?.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                                    sourceHasCargo = rightArea != null && rightArea.CargoChildren.Any();
                                }
                                else if (missionType == "精加工")
                                {
                                    var srcSta = API.GetStation(srcId);
                                    var rightArea = srcSta?.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                                    sourceHasCargo = rightArea != null && rightArea.CargoChildren.Any();
                                }
                                else
                                {
                                    sourceHasCargo = !API.GetStation(srcId).IsEmpty();
                                }

                                if (sourceHasCargo)
                                    _agvStatus[agvName] = "取货";
                                else
                                {
                                    Tools.trace($"[防错] {agvName} 已到达 {srcId} 但货物已被取空，重新找任务");
                                    _agvStatus[agvName] = "等待任务";
                                }
                            }
                            break;

                        case "取货":
                            {
                                string sId = _agvCurrentSource[agvName];
                                string mType = _agvMissionType[agvName];
                                var sidSta = API.GetStation(sId);
                                
                                int[] takenCounts = new int[4];

                                if (mType == "输出")
                                {
                                    // 从组装站右区取成品13/14 → 送输出站
                                    var rightArea = sidSta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                                    if (rightArea == null)
                                    {
                                        Tools.trace($"[警告] 组装站{sId}未找到'加工机器右区'区域，AGV{agvName}重新找任务");
                                        _agvStatus[agvName] = "等待任务";
                                        break;
                                    }
                                    // 成品13和成品14使用独立的2元素数组
                                    int[] productTaken = new int[2];
                                    productTaken[0] = (int)(rightArea?.Contains("成品13")?.Quanlity ?? 0);
                                    productTaken[1] = (int)(rightArea?.Contains("成品14")?.Quanlity ?? 0);
                                    int totalProdAvailable = productTaken.Sum();
                                    if (totalProdAvailable <= 0)
                                    {
                                        Tools.trace($"[防错] 组装站{sId}右区无成品，AGV{agvName}重新寻找任务");
                                        _agvStatus[agvName] = "等待任务";
                                        break;
                                    }
                                    var agvBuf = agv.CargoAreas.FirstOrDefault(a => a.Name == "agv缓冲区");
                                    int agvCurrentCargo = agvBuf != null ? agvBuf.Quanlity : 0;
                                    int availableSlots = Math.Max(0, 5 - agvCurrentCargo);
                                    if (availableSlots <= 0)
                                    {
                                        Tools.trace($"[防错] AGV{agvName}缓冲区已满({agvCurrentCargo})，无法取货，重新寻找任务");
                                        _agvStatus[agvName] = "等待任务";
                                        break;
                                    }
                                    int totalTake = Math.Min(totalProdAvailable, availableSlots);
                                    int rSlots = totalTake;
                                    for (int i = 0; i < 2 && rSlots > 0; i++)
                                    {
                                        int take = Math.Min(productTaken[i], rSlots);
                                        productTaken[i] = take;
                                        rSlots -= take;
                                    }
                                    for (int i = 0; i < 2; i++)
                                    {
                                        if (productTaken[i] > 0) { sidSta.CargoTake("加工机器右区", _allProductCargos[i], productTaken[i]); agv.CargoPlace("agv缓冲区", _allProductCargos[i], productTaken[i]); }
                                    }
                                    Tools.trace($"{agvName} [输出取货] 从组装站{sId}右区取 ({string.Join(",", Enumerable.Range(0,2).Select(i=>$"{productTaken[i]}个{_allProductCargos[i]}"))})");
                                    _agvDeliveryPlan[agvName] = BuildOutputDeliveryPlan(productTaken, _outputStations);
                                }
                                else if (mType == "组装")
                                {
                                    // 从精加工站右区取货物9-12 → 送组装站
                                    var rightArea = sidSta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                                    if (rightArea == null)
                                    {
                                        Tools.trace($"[警告] 精加工站{sId}未找到'加工机器右区'区域，AGV{agvName}重新找任务");
                                        _agvStatus[agvName] = "等待任务";
                                        break;
                                    }
                                    int totalAvailable = 0;
                                    for (int i = 0; i < 4; i++)
                                    {
                                        string cn = _allFineCargos[i]; // 货物9-12
                                        takenCounts[i] = (int)(rightArea?.Contains(cn)?.Quanlity ?? 0);
                                        totalAvailable += takenCounts[i];
                                    }
                                    if (totalAvailable <= 0)
                                    {
                                        Tools.trace($"[防错] 精加工站{sId}右区已被取空，AGV{agvName}重新寻找任务");
                                        _agvStatus[agvName] = "等待任务";
                                        break;
                                    }
                                    var agvBuf2 = agv.CargoAreas.FirstOrDefault(a => a.Name == "agv缓冲区");
                                    int agvCurrentCargo2 = agvBuf2 != null ? agvBuf2.Quanlity : 0;
                                    int availableSlots2 = Math.Max(0, 5 - agvCurrentCargo2);
                                    if (availableSlots2 <= 0)
                                    {
                                        Tools.trace($"[防错] AGV{agvName}缓冲区已满({agvCurrentCargo2})，无法取货，重新寻找任务");
                                        _agvStatus[agvName] = "等待任务";
                                        break;
                                    }
                                    int totalTake2 = Math.Min(totalAvailable, availableSlots2);
                                    int rSlots2 = totalTake2;
                                    for (int i = 0; i < 4 && rSlots2 > 0; i++)
                                    {
                                        int take = Math.Min(takenCounts[i], rSlots2);
                                        takenCounts[i] = take;
                                        rSlots2 -= take;
                                    }
                                    for (int i = 0; i < 4; i++)
                                    {
                                        if (takenCounts[i] > 0) { sidSta.CargoTake("加工机器右区", _allFineCargos[i], takenCounts[i]); agv.CargoPlace("agv缓冲区", _allFineCargos[i], takenCounts[i]); }
                                    }
                                    Tools.trace($"{agvName} [组装取货] 从精加工站{sId}右区取 ({string.Join(",", Enumerable.Range(0,4).Select(i=>$"{takenCounts[i]}个{_allFineCargos[i]}"))})");
                                    _agvDeliveryPlan[agvName] = BuildDeliveryPlanForTarget(takenCounts, _allAssemblyStations);
                                }
                                else if (mType == "精加工")
                                {
                                    // 从粗加工站右区取货物5-8 → 送精加工站
                                    var rightArea = sidSta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                                    if (rightArea == null)
                                    {
                                        Tools.trace($"[警告] 粗加工站{sId}未找到'加工机器右区'区域，AGV{agvName}重新找任务");
                                        _agvStatus[agvName] = "等待任务";
                                        break;
                                    }
                                    int totalAvailable = 0;
                                    for (int i = 0; i < 4; i++)
                                    {
                                        string cn = _allRoughCargos[i]; // 货物5-8
                                        takenCounts[i] = (int)(rightArea?.Contains(cn)?.Quanlity ?? 0);
                                        totalAvailable += takenCounts[i];
                                    }
                                    if (totalAvailable <= 0)
                                    {
                                        Tools.trace($"[防错] 粗加工站{sId}右区已被取空，AGV{agvName}重新寻找任务");
                                        _agvStatus[agvName] = "等待任务";
                                        break;
                                    }
                                    var agvBuf3 = agv.CargoAreas.FirstOrDefault(a => a.Name == "agv缓冲区");
                                    int agvCurrentCargo3 = agvBuf3 != null ? agvBuf3.Quanlity : 0;
                                    int availableSlots3 = Math.Max(0, 5 - agvCurrentCargo3);
                                    if (availableSlots3 <= 0)
                                    {
                                        Tools.trace($"[防错] AGV{agvName}缓冲区已满({agvCurrentCargo3})，无法取货，重新寻找任务");
                                        _agvStatus[agvName] = "等待任务";
                                        break;
                                    }
                                    int totalTake3 = Math.Min(totalAvailable, availableSlots3);
                                    for (int i = 0; i < 4; i++)
                                    {
                                        if (takenCounts[i] > 0) { sidSta.CargoTake("加工机器右区", _allRoughCargos[i], takenCounts[i]); agv.CargoPlace("agv缓冲区", _allRoughCargos[i], takenCounts[i]); }
                                    }
                                    Tools.trace($"{agvName} [精加工取货] 从粗加工站{sId}右区取 ({string.Join(",", Enumerable.Range(0,4).Select(i=>$"{takenCounts[i]}个{_allRoughCargos[i]}"))})");
                                    _agvDeliveryPlan[agvName] = BuildDeliveryPlanForTarget(takenCounts, _fineProcessStations);
                                }
                                else // 粗加工
                                {
                                    var inputArea = sidSta.CargoAreas.FirstOrDefault(a => a.Name == "输入机器");
                                    if (inputArea == null)
                                    {
                                        Tools.trace($"[警告] 站点{sId}未找到'输入机器'区域，AGV{agvName}重新找任务");
                                        _agvStatus[agvName] = "等待任务";
                                        break;
                                    }
                                    int totalAvailable = 0;
                                    for (int i = 0; i < 4; i++)
                                    {
                                        string cn = _allSourceCargos[i]; // 货物1-4
                                        takenCounts[i] = (int)(inputArea?.Contains(cn)?.Quanlity ?? 0);
                                        totalAvailable += takenCounts[i];
                                    }
                                    if (totalAvailable <= 0)
                                    {
                                        Tools.trace($"[防错] 站点{sId}货物已被取空，AGV{agvName}重新寻找任务");
                                        _agvStatus[agvName] = "等待任务";
                                        break;
                                    }
                                    var agvBuf4 = agv.CargoAreas.FirstOrDefault(a => a.Name == "agv缓冲区");
                                    int agvCurrentCargo4 = agvBuf4 != null ? agvBuf4.Quanlity : 0;
                                    int availableSlots4 = Math.Max(0, 5 - agvCurrentCargo4);
                                    if (availableSlots4 <= 0)
                                    {
                                        Tools.trace($"[防错] AGV{agvName}缓冲区已满({agvCurrentCargo4})，无法取货，重新寻找任务");
                                        _agvStatus[agvName] = "等待任务";
                                        break;
                                    }
                                    int totalTake4 = Math.Min(totalAvailable, availableSlots4);
                                    int rSlots4 = totalTake4;
                                    for (int i = 0; i < 4 && rSlots4 > 0; i++)
                                    {
                                        int take = Math.Min(takenCounts[i], rSlots4);
                                        takenCounts[i] = take;
                                        rSlots4 -= take;
                                    }
                                    for (int i = 0; i < 4; i++)
                                    {
                                        if (takenCounts[i] > 0) { sidSta.CargoTake("输入机器", _allSourceCargos[i], takenCounts[i]); agv.CargoPlace("agv缓冲区", _allSourceCargos[i], takenCounts[i]); }
                                    }
                                    Tools.trace($"{agvName} [粗加工取货] 从产生站{sId}取 ({string.Join(",", Enumerable.Range(0,4).Select(i=>$"{takenCounts[i]}个{_allSourceCargos[i]}"))})");
                                    _agvDeliveryPlan[agvName] = BuildDeliveryPlanForTarget(takenCounts, _roughProcessStations);
                                }

                                if (_agvDeliveryPlan[agvName].Count > 0)
                                {
                                    var firstPlan = _agvDeliveryPlan[agvName].Peek();
                                    _agvCurrentProc[agvName] = firstPlan.stationId.ToString();
                                    _agvStatus[agvName] = "前往送货";
                                    Tools.trace($"{agvName} 配送计划: 共{_agvDeliveryPlan[agvName].Count}站, 首站{firstPlan.stationId}");
                                }
                                else
                                {
                                    _agvStatus[agvName] = "等待卸货";
                                    Tools.trace($"{agvName} 所有目标站已满，进入等待卸货状态");
                                }
                            }
                            break;

                        case "前往送货":
                            {
                                string pId = _agvCurrentProc[agvName];
                                
                                // ★★★ 防重复导航：仅在AGV空闲且不在目标点时下发GoTo ★★★
                                if (agv.Tasks.Count < 1 && agv.API.On != pId)
                                    agv.API.GoTo(pId, null);
                                if (agv.API.On == pId) _agvStatus[agvName] = "卸货";
                            }
                            break;

                        case "卸货":
                            {
                                string procId = _agvCurrentProc[agvName];
                                var procSta = API.GetStation(procId);
                                
                                if (_agvDeliveryPlan[agvName].Count > 0)
                                    _agvDeliveryPlan[agvName].Dequeue();
                                
                                string targetAreaName = _agvMissionType[agvName] == "输出" ? "输出机器" : "加工机器左区";
                                agv.MoveToOtherArea("agv缓冲区", procSta, targetAreaName);
                                Tools.trace($"{agvName} 卸货完成到站 {procId} (区域:{targetAreaName})");
                                
                                var bufArea = agv.CargoAreas.FirstOrDefault(a => a.Name == "agv缓冲区");
                                bool hasRemaining = bufArea != null && bufArea.CargoChildren.Any();
                                
                                if (hasRemaining)
                                {
                                    if (_agvDeliveryPlan[agvName].Count > 0)
                                    {
                                        var nextPlan = _agvDeliveryPlan[agvName].Peek();
                                        _agvCurrentProc[agvName] = nextPlan.stationId.ToString();
                                        _agvStatus[agvName] = "前往送货";
                                        Tools.trace($"{agvName} 继续配送: 下一站{nextPlan.stationId}");
                                    }
                                    else
                                    {
                                        string[] cargoNames = GetCargoNamesForStageByMission(_agvMissionType[agvName]);
                                        int[] bufCounts = GetBufferCargoCounts(bufArea, cargoNames);
                                        int[] targetStations2 = GetTargetStationsForMission(_agvMissionType[agvName]);
                                        _agvDeliveryPlan[agvName] = _agvMissionType[agvName] == "输出"
                                            ? BuildOutputDeliveryPlan(bufCounts, targetStations2)
                                            : BuildDeliveryPlanForTarget(bufCounts, targetStations2);
                                        if (_agvDeliveryPlan[agvName].Count > 0)
                                        {
                                            var nextPlan = _agvDeliveryPlan[agvName].Peek();
                                            _agvCurrentProc[agvName] = nextPlan.stationId.ToString();
                                            _agvStatus[agvName] = "前往送货";
                                            Tools.trace($"{agvName} 重新规划配送: 首站{nextPlan.stationId}");
                                        }
                                        else
                                        {
                                            _agvStatus[agvName] = "等待卸货";
                                            Tools.trace($"{agvName} 配送计划用完且无可用容量，进入等待卸货");
                                        }
                                    }
                                }
                                else
                                {
                                    _agvStatus[agvName] = "等待任务";
                                    _agvDeliveryPlan[agvName] = new Queue<(int, int[])>();
                                }
                            }
                            break;
                        case "等待卸货":
                            {
                                var bufArea = agv.CargoAreas.FirstOrDefault(a => a.Name == "agv缓冲区");
                                if (bufArea == null || !bufArea.CargoChildren.Any())
                                {
                                    _agvStatus[agvName] = "等待任务";
                                    break;
                                }
                                string[] cargoNames = GetCargoNamesForStageByMission(_agvMissionType[agvName]);
                                int[] bufCounts = GetBufferCargoCounts(bufArea, cargoNames);
                                int[] targetStations = GetTargetStationsForMission(_agvMissionType[agvName]);
                                _agvDeliveryPlan[agvName] = _agvMissionType[agvName] == "输出"
                                    ? BuildOutputDeliveryPlan(bufCounts, targetStations)
                                    : BuildDeliveryPlanForTarget(bufCounts, targetStations);
                                if (_agvDeliveryPlan[agvName].Count > 0)
                                {
                                    var plan = _agvDeliveryPlan[agvName].Peek();
                                    _agvCurrentProc[agvName] = plan.stationId.ToString();
                                    _agvStatus[agvName] = "前往送货";
                                    Tools.trace($"{agvName} 等待卸货结束，首站{plan.stationId}");
                                }
                            }
                            break;
                    }
                    agv.SetValue("状态", $"[{_agvMissionType[agvName]}] {_agvStatus[agvName]}");
                }
            }, 1000);

            // ==========================================
            // 逻辑 C：多加工站独立加工逻辑 (5.0: 支持货物转换)
            // ==========================================
            API.SetInterval(tc =>
            {
                foreach (var id in _processStations)
                {
                    var sta = API.GetStation(id.ToString());
                    if (sta == null) continue;

                    var leftArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器左区");
                    bool leftHasCargo = leftArea != null && leftArea.CargoChildren.Any();

                    if (!_isProcessingMap[id] && leftHasCargo)
                    {
                        _isProcessingMap[id] = true;
                        double u = _rand.NextDouble();
                        bool isRoughStation2 = _roughProcessStations.Contains(id);
                        double usedLambda = isRoughStation2 ? _lambdaRoughProcess : _lambdaFineProcess;
                        double clampMin = isRoughStation2 ? 30 : 60;  // 粗加工机器加工时间限制在[30, 300]，精加工机器加工时间限制在[60, 500]
                        double clampMax = isRoughStation2 ? 300 : 500;
                        _processTimers[id] = -Math.Log(1 - u) / usedLambda;
                        _processTimers[id] = Math.Clamp(_processTimers[id], clampMin, clampMax);
                    }

                    if (_isProcessingMap[id])
                    {
                        if (!leftHasCargo)
                        {
                            _isProcessingMap[id] = false;
                            _processTimers[id] = 0;
                        }
                        else
                        {
                            _processTimers[id] -= 1;
                            if (_processTimers[id] <= 0)
                            {
                                // 5.0: 加工完成后货物类型转换
                                // 粗加工站(307-334): 货物1→5, 2→6, 3→7, 4→8
                                // 精加工站(201-240): 货物5→9, 6→10, 7→11, 8→12
                                bool isRoughStation = _roughProcessStations.Contains(id);
                                var transformDict = isRoughStation ? _roughTransform : _fineTransform;
                                string[] sourceCargos = isRoughStation ? _allSourceCargos : _allRoughCargos;

                                foreach (var srcCargo in sourceCargos)
                                {
                                    int qty = (int)(leftArea?.Contains(srcCargo)?.Quanlity ?? 0);
                                    if (qty > 0 && transformDict.ContainsKey(srcCargo))
                                    {
                                        string targetCargo = transformDict[srcCargo];
                                        sta.CargoTake("加工机器左区", srcCargo, qty);
                                        sta.CargoPlace("加工机器右区", targetCargo, qty);
                                    }
                                }
                                _isProcessingMap[id] = false;
                            }
                        }
                    }
                    string isRough = _roughProcessStations.Contains(id) ? "粗" : "精";
                    sta.SetValue("加工状态", _isProcessingMap[id] ? $"[{isRough}]加工中:{(int)_processTimers[id]}s" : "等待进料");
                }
            }, 1000);

            // ==========================================
            // 逻辑 D：组装站组装逻辑 (5.0)
            // 105-114: 1x货物9+1x货物10 → 1x成品13
            // 115-124: 1x货物11+1x货物12 → 1x成品14
            // ==========================================
            API.SetInterval(tc =>
            {
                foreach (var id in _allAssemblyStations)
                {
                    var sta = API.GetStation(id.ToString());
                    if (sta == null) continue;

                    var leftArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器左区");

                    bool isProduct13 = _assemblyStations13.Contains(id);
                    string productName = isProduct13 ? "成品13" : "成品14";
                    string cargoA = isProduct13 ? "货物9" : "货物11";
                    string cargoB = isProduct13 ? "货物10" : "货物12";

                    int countA = (int)(leftArea?.Contains(cargoA)?.Quanlity ?? 0);
                    int countB = (int)(leftArea?.Contains(cargoB)?.Quanlity ?? 0);
                    bool canAssemble = countA >= 1 && countB >= 1;

                    if (!_isAssemblyMap[id] && canAssemble)
                    {
                        _isAssemblyMap[id] = true;
                        double u = _rand.NextDouble();
                        _assemblyTimers[id] = -Math.Log(1 - u) / _lambdaAssembly;
                        _assemblyTimers[id] = Math.Clamp(_assemblyTimers[id], 60, 600);
                    }

                    if (_isAssemblyMap[id])
                    {
                        if (!canAssemble)
                        {
                            _isAssemblyMap[id] = false;
                            _assemblyTimers[id] = 0;
                        }
                        else
                        {
                            _assemblyTimers[id] -= 1;
                            if (_assemblyTimers[id] <= 0)
                            {
                                sta.CargoTake("加工机器左区", cargoA, 1);
                                sta.CargoTake("加工机器左区", cargoB, 1);
                                sta.CargoPlace("加工机器右区", productName, 1);
                                _isAssemblyMap[id] = false;
                            }
                        }
                    }
                    string pn = isProduct13 ? "13" : "14";
                    sta.SetValue("加工状态", _isAssemblyMap[id] ? $"[组装{pn}]组装中:{(int)_assemblyTimers[id]}s" : "等待进料");
                }
            }, 1000);

            // ==========================================
            // 手动控制模式调度循环 (100ms检查一次)
            // ==========================================
            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        // 1) 处理控制台命令
                        string cmd;
                        while (_manualCmdQueue.TryDequeue(out cmd))
                        {
                            ProcessManualCommand(cmd);
                        }

                        // 2) 手动模式下，推进AGV任务状态
                        if (_manualMode)
                        {
                            StepManualAgvTask();
                        }
                    }
                    catch (Exception ex)
                    {
                        Tools.trace($"[手动模式] 异常: {ex.Message}");
                    }
                    Thread.Sleep(100);
                }
            }) { IsBackground = true }.Start();

            // ==========================================
            // 手动控制提示线程 (每秒检查，当有AGV空闲时自动弹出推荐)
            // ==========================================
            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        if (_manualMode)
                        {
                            // 检查是否有空闲AGV（手动模式下状态为"手动空闲"）
                            var idleAgvs = _agvNames
                                .Where(n => _agvStatus.ContainsKey(n) && _agvStatus[n] == "手动空闲")
                                .ToList();

                            if (idleAgvs.Count > 0)
                            {
                                // 检查是否有货可取
                                var sourceCandidates = ScanManualSourceCandidates();
                                if (sourceCandidates.Count > 0)
                                {
                                    var best = sourceCandidates.OrderByDescending(s => s.priority).First();
                                    Tools.trace($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                                    Tools.trace($"  手动模式任务推荐:");
                                    Tools.trace($"  可用AGV: {string.Join(", ", idleAgvs)}");
                                    Tools.trace($"  推荐取货: 站{best.stationId} ({best.cargoType}) 优先级:{best.priority}");
                                    Tools.trace($"  目标类型: {best.missionType}");
                                    Tools.trace($"  跨楼层: {(best.needElevator ? $"是(推{best.elevatorSide}梯)" : "否")}");
                                    Tools.trace($"  输入格式: {idleAgvs[0].Replace("Agv-","")} {best.stationId}");
                                    if (best.needElevator)
                                        Tools.trace($"      或: {idleAgvs[0].Replace("Agv-","")} {best.stationId} {best.elevatorSide}");
                                    Tools.trace($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                                }
                            }
                        }
                    }
                    catch { }
                    Thread.Sleep(5000); // 每5秒检查一次
                }
            }) { IsBackground = true }.Start();

            while (true) { Thread.Sleep(1000); }
        }
        
        /// <summary>
        /// 根据任务类型获取缓冲区检查的货物名称数组
        /// </summary>
        private string[] GetCargoNamesForStageByMission(string missionType)
        {
            switch (missionType)
            {
                case "粗加工": return _allSourceCargos;
                case "精加工": return _allRoughCargos;
                case "组装": return _allFineCargos;
                case "输出": return _allProductCargos;
                default: return _allSourceCargos;
            }
        }

        /// <summary>
        /// 根据任务类型获取目标站点数组
        /// </summary>
        private int[] GetTargetStationsForMission(string missionType)
        {
            switch (missionType)
            {
                case "粗加工": return _roughProcessStations;
                case "精加工": return _fineProcessStations;
                case "组装": return _allAssemblyStations;
                case "输出": return _outputStations;
                default: return _roughProcessStations;
            }
        }

        /// <summary>
        /// 获取AGV缓冲区中各货物的数量（支持2元素产品数组）
        /// </summary>
        private int[] GetBufferCargoCounts(ICargoArea bufArea, string[] cargoNames)
        {
            int len = cargoNames.Length;
            int[] counts = new int[len];
            for (int i = 0; i < len; i++)
            {
                counts[i] = (int)(bufArea?.Contains(cargoNames[i])?.Quanlity ?? 0);
            }
            return counts;
        }

        /// <summary>
        /// 构建输出配送计划（2产品版）：扫描输出站的输出机器区剩余容量，按容量降序贪婪分配
        /// </summary>
        private Queue<(int stationId, int[] counts)> BuildOutputDeliveryPlan(int[] totalCounts, int[] targetStations)
        {
            var plan = new Queue<(int, int[])>();
            int[] remaining = (int[])totalCounts.Clone();

            var stationCapacities = new List<(int id, int freeSlots)>();
            foreach (var id in targetStations)
            {
                var sta = API.GetStation(id.ToString());
                if (sta == null) continue;
                var outArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "输出机器");
                if (outArea == null) continue;

                int currentCount = outArea.Quanlity;
                int maxCapacity = outArea.Info.EnableLimitedQuanlity ? outArea.Info.LimitedQuanlity : 10;
                int freeSlots = maxCapacity - currentCount;
                if (freeSlots > 0)
                {
                    stationCapacities.Add((id, freeSlots));
                }
            }

            stationCapacities.Sort((a, b) => b.freeSlots.CompareTo(a.freeSlots));

            foreach (var (sid, freeSlots) in stationCapacities)
            {
                int totalRemaining = remaining.Sum();
                if (totalRemaining <= 0) break;

                int[] allocate = new int[2];
                int slotsLeft = freeSlots;
                for (int i = 0; i < 2 && slotsLeft > 0; i++)
                {
                    allocate[i] = Math.Min(remaining[i], slotsLeft);
                    remaining[i] -= allocate[i];
                    slotsLeft -= allocate[i];
                }

                if (allocate.Sum() > 0)
                {
                    plan.Enqueue((sid, allocate));
                }
            }

            return plan;
        }
        /// <summary>
        /// 根据阶段获取对应的货物名称数组
        /// </summary>
        private string[] GetCargoNamesForStage(string stage)
        {
            switch (stage)
            {
                case "source": return _allSourceCargos;
                case "rough": return _allRoughCargos;
                case "fine": return _allFineCargos;
                default: return _allSourceCargos;
            }
        }
        /// <summary>
        /// 构建智能配送计划（4货物版）：扫描指定加工站数组的左区剩余容量，按容量降序贪婪分配
        /// </summary>
        private Queue<(int stationId, int[] counts)> BuildDeliveryPlanForTarget(int[] totalCounts, int[] targetStations)
        {
            var plan = new Queue<(int, int[])>();
            int[] remaining = (int[])totalCounts.Clone();

            var stationCapacities = new List<(int id, int freeSlots)>();
            foreach (var id in targetStations)
            {
                var sta = API.GetStation(id.ToString());
                if (sta == null) continue;
                var leftArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器左区");
                if (leftArea == null) continue;

                int currentCount = leftArea.Quanlity;
                int maxCapacity = leftArea.Info.EnableLimitedQuanlity ? leftArea.Info.LimitedQuanlity : 5;
                int freeSlots = maxCapacity - currentCount;
                if (freeSlots > 0)
                {
                    stationCapacities.Add((id, freeSlots));
                }
            }

            stationCapacities.Sort((a, b) => b.freeSlots.CompareTo(a.freeSlots));

            foreach (var (sid, freeSlots) in stationCapacities)
            {
                int totalRemaining = remaining.Sum();
                if (totalRemaining <= 0) break;

                int[] allocate = new int[4];
                int slotsLeft = freeSlots;
                for (int i = 0; i < 4 && slotsLeft > 0; i++)
                {
                    allocate[i] = Math.Min(remaining[i], slotsLeft);
                    remaining[i] -= allocate[i];
                    slotsLeft -= allocate[i];
                }

                if (allocate.Sum() > 0)
                {
                    plan.Enqueue((sid, allocate));
                }
            }

            return plan;
        }

        /// <summary>
        /// 【保留】兼容旧接口，扫描所有加工站（_processStations）
        /// </summary>
        private Queue<(int stationId, int[] counts)> BuildDeliveryPlan(int[] totalCounts)
        {
            return BuildDeliveryPlanForTarget(totalCounts, _processStations);
        }

        // ====================================================================
        // 手动控制模式辅助方法
        // ====================================================================

        /// <summary>
        /// 处理控制台命令
        /// </summary>
        private void ProcessManualCommand(string cmd)
        {
            cmd = cmd.ToLower().Trim();

            if (cmd == "manual on")
            {
                _manualMode = true;
                // 将所有空闲AGV改为手动空闲
                foreach (var n in _agvNames)
                {
                    if (_agvStatus[n] == "等待任务" || string.IsNullOrEmpty(_agvStatus[n]))
                        _agvStatus[n] = "手动空闲";
                }
                Tools.trace("[手动模式] 已启用手动控制模式，自动调度已停止");
                return;
            }

            if (cmd == "manual off")
            {
                _manualMode = false;
                // 将所有手动状态的AGV还原
                foreach (var n in _agvNames)
                {
                    if (_agvStatus[n] == "手动空闲")
                        _agvStatus[n] = "等待任务";
                }
                Tools.trace("[手动模式] 已切换回自动调度模式");
                return;
            }

            // 在手动模式下处理任务命令: [agv编号] [目标站] [left/right]
            if (_manualMode)
            {
                var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    if (int.TryParse(parts[0], out int agvIdx) && int.TryParse(parts[1], out int targetStation))
                    {
                        string agvName = $"Agv-{agvIdx}";
                        if (!_agvNames.Contains(agvName))
                        {
                            Tools.trace($"[手动模式] 错误: AGV Agv-{agvIdx} 不存在，可用AGV: {string.Join(", ", _agvNames)}");
                            return;
                        }
                        if (_agvStatus[agvName] != "手动空闲")
                        {
                            Tools.trace($"[手动模式] 错误: {agvName} 当前状态为 '{_agvStatus[agvName]}'，非空闲状态");
                            return;
                        }

                        string elevatorSide = parts.Length >= 3 ? parts[2] : "";

                        // 判断任务类型
                        string missionType = DetermineManualMissionType(targetStation);
                        if (string.IsNullOrEmpty(missionType))
                        {
                            Tools.trace($"[手动模式] 错误: 站点{targetStation}不是有效的卸货目标站");
                            return;
                        }

                        // 找到合适的取货源站
                        int sourceStation = FindManualSourceStation(missionType);
                        if (sourceStation == -1)
                        {
                            Tools.trace($"[手动模式] 当前没有可用的取货源站（{missionType}任务）");
                            return;
                        }

                        // 设置手动任务
                        _manualAgvName = agvName;
                        _manualSrcStation = sourceStation.ToString();
                        _manualTgtStation = targetStation.ToString();
                        _manualMissionType = missionType;
                        _manualElevatorSide = elevatorSide;
                        _manualTaskState = ManualTaskState.GoingToSource;

                        _agvStatus[agvName] = "手动前往取货";
                        _agvMissionType[agvName] = missionType;
                        _agvCurrentSource[agvName] = sourceStation.ToString();

                        Tools.trace($"[手动模式] {agvName} → 取货站{sourceStation}({missionType}) → 卸货站{targetStation}");
                    }
                    else
                    {
                        Tools.trace($"[手动模式] 命令格式错误。用法: [agv编号] [目标站地标] [left/right可选]");
                    }
                }
            }
        }

        /// <summary>
        /// 推进手动AGV任务状态机 (100ms周期)
        /// </summary>
        private void StepManualAgvTask()
        {
            if (_manualTaskState == ManualTaskState.Idle) return;
            if (string.IsNullOrEmpty(_manualAgvName)) return;

            var agv = API.GetAgv(_manualAgvName);
            if (agv == null) return;

            switch (_manualTaskState)
            {
                case ManualTaskState.GoingToSource:
                    // 防重复导航
                    if (agv.Tasks.Count < 1 && agv.API.On != _manualSrcStation)
                        agv.API.GoTo(_manualSrcStation, null);

                    if (agv.API.On == _manualSrcStation)
                    {
                        // 到达取货站 → 开始取货
                        ManualTakeCargo(agv);
                    }
                    break;

                case ManualTaskState.GoingToTarget:
                    // 防重复导航
                    if (agv.Tasks.Count < 1 && agv.API.On != _manualTgtStation)
                        agv.API.GoTo(_manualTgtStation, null);

                    if (agv.API.On == _manualTgtStation)
                    {
                        // 到达目标站 → 卸货
                        ManualUnloadCargo(agv);
                    }
                    break;
            }
        }

        /// <summary>
        /// 手动取货：从源站取货到AGV缓冲区
        /// </summary>
        private void ManualTakeCargo(IAgv agv)
        {
            var srcSta = API.GetStation(_manualSrcStation);
            if (srcSta == null) { ResetManualTask(); return; }

            string areaName;
            string[] cargoNames;

            if (_manualMissionType == "粗加工")
            {
                areaName = "输入机器";
                cargoNames = _allSourceCargos;
            }
            else
            {
                areaName = "加工机器右区";
                if (_manualMissionType == "精加工") cargoNames = _allRoughCargos;
                else if (_manualMissionType == "组装") cargoNames = _allFineCargos;
                else cargoNames = _allProductCargos; // 输出
            }

            var srcArea = srcSta.CargoAreas.FirstOrDefault(a => a.Name == areaName);
            if (srcArea == null || !srcArea.CargoChildren.Any())
            {
                Tools.trace($"[手动模式] 源站{_manualSrcStation}没有可取的货物，任务取消");
                ResetManualTask();
                return;
            }

            var agvBuf = agv.CargoAreas.FirstOrDefault(a => a.Name == "agv缓冲区");
            int availableSlots = agvBuf != null ? Math.Max(0, 5 - agvBuf.Quanlity) : 0;
            if (availableSlots <= 0)
            {
                Tools.trace($"[手动模式] {_manualAgvName} 缓冲区已满，无法取货");
                ResetManualTask();
                return;
            }

            // 取货：每种货物尽量取
            int taken = 0;
            foreach (var cn in cargoNames)
            {
                int qty = (int)(srcArea.Contains(cn)?.Quanlity ?? 0);
                int take = Math.Min(qty, availableSlots - taken);
                if (take > 0)
                {
                    srcSta.CargoTake(areaName, cn, take);
                    agv.CargoPlace("agv缓冲区", cn, take);
                    taken += take;
                }
                if (taken >= availableSlots) break;
            }

            Tools.trace($"[手动模式] {_manualAgvName} 从站{_manualSrcStation}取了{taken}件货物");

            // 切换到前往目标站
            _agvStatus[_manualAgvName] = "手动前往卸货";
            _agvCurrentProc[_manualAgvName] = _manualTgtStation;
            _manualTaskState = ManualTaskState.GoingToTarget;
        }

        /// <summary>
        /// 手动卸货：从AGV缓冲区卸货到目标站
        /// </summary>
        private void ManualUnloadCargo(IAgv agv)
        {
            var tgtSta = API.GetStation(_manualTgtStation);
            if (tgtSta == null) { ResetManualTask(); return; }

            string targetArea = _manualMissionType == "输出" ? "输出机器" : "加工机器左区";
            agv.MoveToOtherArea("agv缓冲区", tgtSta, targetArea);
            Tools.trace($"[手动模式] {_manualAgvName} 卸货到站{_manualTgtStation}({targetArea}) 完成");

            // 检查是否还有货物
            var bufArea = agv.CargoAreas.FirstOrDefault(a => a.Name == "agv缓冲区");
            if (bufArea != null && bufArea.CargoChildren.Any())
            {
                // 还有残留，保持"手动空闲"让用户决定
                Tools.trace($"[手动模式] {_manualAgvName} 缓冲区还有残留货物，保持手动空闲");
            }

            ResetManualTask();
        }

        /// <summary>
        /// 重置手动任务状态
        /// </summary>
        private void ResetManualTask()
        {
            // 先将完成任务的AGV设回手动空闲（在清空前保存）
            string completedAgv = _manualAgvName;
            if (!string.IsNullOrEmpty(completedAgv))
                _agvStatus[completedAgv] = "手动空闲";

            _manualTaskState = ManualTaskState.Idle;
            _manualAgvName = "";
            _manualSrcStation = "";
            _manualTgtStation = "";
            _manualMissionType = "";
            _manualElevatorSide = "";

            // 将所有不在任务中的AGV保持手动空闲
            foreach (var n in _agvNames)
            {
                if (_agvStatus[n] != "手动前往取货" && _agvStatus[n] != "手动前往卸货")
                {
                    var agv = API.GetAgv(n);
                    if (agv != null)
                    {
                        var buf = agv.CargoAreas.FirstOrDefault(a => a.Name == "agv缓冲区");
                        if (buf == null || !buf.CargoChildren.Any())
                            _agvStatus[n] = "手动空闲";
                    }
                }
            }
        }
        /// <summary>
        /// 根据目标站地标判断任务类型
        /// </summary>
        private string DetermineManualMissionType(int stationMark)
        {
            if (_roughProcessStations.Contains(stationMark)) return "粗加工";
            if (_fineProcessStations.Contains(stationMark)) return "精加工";
            if (_allAssemblyStations.Contains(stationMark)) return "组装";
            if (_outputStations.Contains(stationMark)) return "输出";
            return "";
        }

        /// <summary>
        /// 找到可用的取货源站（按优先级）
        /// </summary>
        private int FindManualSourceStation(string missionType)
        {
            switch (missionType)
            {
                case "粗加工":
                    // 源：产生站有货物1-4
                    return _sourceStations.FirstOrDefault(s => !API.GetStation(s.ToString()).IsEmpty());
                case "精加工":
                    // 源：粗加工站右区有货物5-8
                    return _roughProcessStations.FirstOrDefault(s => {
                        var sta = API.GetStation(s.ToString());
                        var ra = sta?.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                        return ra != null && ra.CargoChildren.Any();
                    });
                case "组装":
                    // 源：精加工站右区有货物9-12
                    return _fineProcessStations.FirstOrDefault(s => {
                        var sta = API.GetStation(s.ToString());
                        var ra = sta?.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                        return ra != null && ra.CargoChildren.Any();
                    });
                case "输出":
                    // 源：组装站右区有成品13/14
                    return _allAssemblyStations.FirstOrDefault(s => {
                        var sta = API.GetStation(s.ToString());
                        var ra = sta?.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                        return ra != null && ra.CargoChildren.Any();
                    });
            }
            return -1;
        }

        /// <summary>
        /// 扫描可用的取货候选（用于推荐）
        /// </summary>
        private List<(int stationId, string cargoType, string missionType, int priority, bool needElevator, string elevatorSide)> ScanManualSourceCandidates()
        {
            var candidates = new List<(int, string, string, int, bool, string)>();

            // Priority 1: 组装站右区有成品 → 送输出站 (最高优先级)
            foreach (var s in _allAssemblyStations)
            {
                var sta = API.GetStation(s.ToString());
                var ra = sta?.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                if (ra != null && ra.CargoChildren.Any())
                {
                    candidates.Add((s, "成品13/14", "输出", 100, true, "left"));
                }
            }

            // Priority 2: 精加工站右区有货物9-12 → 送组装站
            foreach (var s in _fineProcessStations)
            {
                var sta = API.GetStation(s.ToString());
                var ra = sta?.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                if (ra != null && ra.CargoChildren.Any())
                {
                    candidates.Add((s, "货物9-12", "组装", 80, true, "left"));
                }
            }

            // Priority 3: 粗加工站右区有货物5-8 → 送精加工站
            foreach (var s in _roughProcessStations)
            {
                var sta = API.GetStation(s.ToString());
                var ra = sta?.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                if (ra != null && ra.CargoChildren.Any())
                {
                    candidates.Add((s, "货物5-8", "精加工", 60, false, ""));
                }
            }

            // Priority 4: 产生站有货物
            foreach (var s in _sourceStations)
            {
                var sta = API.GetStation(s.ToString());
                if (sta != null && !sta.IsEmpty())
                {
                    candidates.Add((s, "货物1-4", "粗加工", 40, false, ""));
                }
            }

            return candidates;
        }

        /// <summary>
        /// 根据站点地标判断所在楼层
        /// 100-199→1F, 200-299→2F, 300-399→3F
        /// </summary>
        private int GetStationFloor(string mark)
        {
            if (string.IsNullOrEmpty(mark)) return 3;
            if (int.TryParse(mark, out int num))
            {
                if (num >= 100 && num < 200) return 1;
                if (num >= 200 && num < 300) return 2;
                if (num >= 300 && num < 400) return 3;
            }
            return 3;
        }
        
        private string GetStationType(int mark)
        {
            if (_sourceStations.Contains(mark)) return "产生站";
            if (_roughProcessStations.Contains(mark)) return "粗加工站";
            if (_fineProcessStations.Contains(mark)) return "精加工站";
            if (_assemblyStations13.Contains(mark)) return "组装站-成品13";
            if (_assemblyStations14.Contains(mark)) return "组装站-成品14";
            if (_outputStations.Contains(mark)) return "输出站";
            return "未知";
        }
        
        public void Kill()
        {
        }
    }
}
                                                                                                              