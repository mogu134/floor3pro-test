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
    // ========== RL Step DTO：C#是被动执行器，仅接收JSON指令 ==========
    public class RlStepRequest
    {
        public Dictionary<string, AgvAction> Agvs { get; set; }
        public Dictionary<string, ElevatorAction> Elevators { get; set; }
    }
    public class AgvAction
    {
        public string Action { get; set; } // "Idle" 或 "Transport"
        public int Tgt { get; set; }       // 目标站点的标记(Mark)
    }
    public class ElevatorAction
    {
        public string Action { get; set; } // 仅限 "Idle" 或 "Move"
        public int TargetFloor { get; set; } // 1, 2, 3
    }

    public partial class floor3pro : IAppScript
    {
        // 1. 定义站点与设备集合
        private int[] _sourceStations = { 300, 301, 302, 303, 304, 305, 306 }; 
        private int[] _processStations = { 201, 202, 203, 204, 205, 206, 207, 208, 209, 210, 211, 212, 213, 214, 215, 216, 217, 218, 219, 220, 221, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 234, 235, 236, 237, 238, 239, 240, 307, 308, 309, 310, 311, 312, 313, 314, 315, 316, 317, 318, 319, 320, 321, 322, 323, 324, 325, 326, 327 };
        private string[] _agvNames = { "Agv-0", "Agv-3", "Agv-4", "Agv-2", "Agv-5",  "Agv-6",  "Agv-1", "Agv-7", "Agv-8" };
        private int[] _outputStations = { 100, 101, 102, 103, 104 };
        private int[] _roughProcessStations = { 307, 308, 309, 310, 311, 312, 313, 314, 315, 316, 317, 318, 319, 320, 321, 322, 323, 324, 325, 326, 327, 328, 329, 330, 331, 332, 333, 334 };
        private int[] _fineProcessStations = { 201, 202, 203, 204, 205, 206, 207, 208, 209, 210, 211, 212, 213, 214, 215, 216, 217, 218, 219, 220, 221, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 234, 235, 236, 237, 238, 239, 240 };
        private int[] _assemblyStations13 = { 105, 106, 107, 108, 109, 110, 111, 112, 113, 114 };
        private int[] _assemblyStations14 = { 115, 116, 117, 118, 119, 120, 121, 122, 123, 124 };
        private int[] _allAssemblyStations = { 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124 };
        private string[] _allSourceCargos = { "货物1", "货物2", "货物3", "货物4" };
        private string[] _allRoughCargos = { "货物5", "货物6", "货物7", "货物8" };
        private string[] _allFineCargos = { "货物9", "货物10", "货物11", "货物12" };
        private string[] _allProductCargos = { "成品13", "成品14" };
        private static readonly Dictionary<string, string> _roughTransform = new Dictionary<string, string> { {"货物1", "货物5"}, {"货物2", "货物6"}, {"货物3", "货物7"}, {"货物4", "货物8"} };
        private static readonly Dictionary<string, string> _fineTransform = new Dictionary<string, string> { {"货物5", "货物9"}, {"货物6", "货物10"}, {"货物7", "货物11"}, {"货物8", "货物12"} };

        // 3. 物理引擎参数与状态
        private double _lambdaArrival = 0.05, _lambdaArrival4 = 0.05;
        private double _lambdaRoughProcess = 0.01, _lambdaFineProcess = 0.005, _lambdaAssembly = 0.005;
        private Random _rand = new Random();
        private Dictionary<int, double> _arrivalTimers = new Dictionary<int, double>();
        private Dictionary<int, double> _processTimers = new Dictionary<int, double>();
        private Dictionary<int, bool> _isProcessingMap = new Dictionary<int, bool>();
        private Dictionary<int, double> _assemblyTimers = new Dictionary<int, double>();
        private Dictionary<int, bool> _isAssemblyMap = new Dictionary<int, bool>();

        // ========== RL 核心状态变量 (使用 ConcurrentDictionary 保证线程安全) ==========
        private ConcurrentDictionary<string, float> _stepRewards = new ConcurrentDictionary<string, float>();
        private ConcurrentDictionary<string, bool> _agvBusy = new ConcurrentDictionary<string, bool>();

        // 电梯状态记录
        private ConcurrentDictionary<string, int> _elevCurrentFloor = new ConcurrentDictionary<string, int>(
            new Dictionary<string, int> { { "left", 1 }, { "right", 1 } }
        );
        private ConcurrentDictionary<string, IAgv> _elevLoadedAgv = new ConcurrentDictionary<string, IAgv>();

        public void Main()
        {
            API.StartVirtualSim();

            // 1. 注册纯净的 RL HTTP 接口
            try
            {
                API.Http("http://localhost:58080", app =>
                {
                    app.MapPost("/rl-step", async ctx =>
                    {
                        try
                        {
                            var request = await ctx.Request.ReadFromJsonAsync<RlStepRequest>();
                            _stepRewards.Clear(); // 必须在每步开头清零，只收集这一秒内的即时奖励

                            // 执行 AGV 动作
                            if (request?.Agvs != null)
                            {
                                foreach (var kvp in request.Agvs)
                                {
                                    string agvName = kvp.Key;
                                    var act = kvp.Value;
                                    var agv = API.GetAgv(agvName);
                                    if (agv == null) continue;

                                    if (act.Action == "Transport")
                                    {
                                        // 检查是否正在执行异步任务或已有底层Task
                                        if (_agvBusy.GetValueOrDefault(agvName, false) || agv.Tasks.Count > 0) continue; 

                                        // ★智能动作掩码校验：目标站点是否有货可取/能否收货
                                        if (!CheckActionValid(agvName, act.Tgt))
                                        {
                                            _stepRewards.AddOrUpdate(agvName, -2f, (k, v) => v - 2f);
                                            continue;
                                        }

                                        // 校验通过，异步执行物理搬运链
                                        Task.Run(() => ExecuteAgvTaskAsync(agvName, act));
                                    }
                                    else if (act.Action == "Idle")
                                    {
                                        var buf = agv.CargoAreas.FirstOrDefault(a => a.Name == "agv缓冲区");
                                        if (buf != null && buf.CargoChildren.Any())
                                        {
                                            _stepRewards.AddOrUpdate(agvName, -0.1f, (k, v) => v - 0.1f); // 载货发呆，惩罚 -0.1
                                        }
                                    }
                                }
                            }

                            // 执行 Elevator 动作
                            if (request?.Elevators != null)
                            {
                                foreach (var kvp in request.Elevators)
                                {
                                    if (kvp.Value.Action == "Move")
                                    {
                                        Task.Run(() => ExecuteElevatorTaskAsync(kvp.Key, kvp.Value.TargetFloor));
                                    }
                                }
                            }

                            // 2. 阻塞等待 1 秒，让物理世界演进 (RL中的 1 Step)
                            // 改为8倍速
                            int simSpeed = 8; 

                            // 如果是 8 倍速，现实中只需要等待 1000/8 = 125 毫秒，仿真世界就已经度过了 1 秒
                            await Task.Delay(1000 / simSpeed);

                            // 3. 组装环境最新状态返回
                            var response = new
                            {
                                state = GetCurrentRLState(),
                                rewards = _stepRewards,
                                done = CheckIfDone()
                            };
                            ctx.Response.ContentType = "application/json; charset=utf-8";
                            await ctx.Response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(response));
                        }
                        catch (Exception ex)
                        {
                            Tools.trace($"[RL API 异常] {ex.Message}");
                            await ctx.Response.WriteAsync($"{{\"error\":\"{ex.Message}\"}}");
                        }
                    });

                    // ★ 新增：环境重置接口
                    app.MapPost("/rl-reset", async ctx =>
                    {
                        try
                        {
                            // 1. 重置所有计时器和状态标记
                            foreach (var id in _sourceStations) _arrivalTimers[id] = _rand.Next(1, 10);
                            foreach (var id in _processStations) { _processTimers[id] = 0; _isProcessingMap[id] = false; }
                            foreach (var id in _allAssemblyStations) { _assemblyTimers[id] = 0; _isAssemblyMap[id] = false; }
                            
                            // 2. 清空所有站点的货物
//                            var allStations = _sourceStations.Concat(_processStations).Concat(_allAssemblyStations).Concat(_outputStations);
//                            foreach (var id in allStations)
//                            {
//                                var sta = API.GetStation(id.ToString());
//                                if (sta == null) continue;
//                                foreach (var area in sta.CargoAreas)
//                                {
//                                    var cargos = area.CargoChildren.ToList();
//                                    foreach (var c in cargos) sta.CargoTake(area.Name, c.Info.Name, (int)c.Quanlity);
//                                    if (area.Name == "加工机器右区" || area.Name == "输入机器" || area.Name == "输出机器") 
//                                        sta.SetValue("加工状态", "等待进料");
//                                }
//                            }
                            API.ClearCargos(
                                API.Stations.Where(x => x.CargoAreas.Count > 0)
                                    .SelectMany(x => x.CargoAreas)
                                    .Concat(
                                        API.Agvs.Where(x => x.CargoAreas.Count > 0)
                                            .SelectMany(x => x.CargoAreas)
                                    )
                                    .Distinct()
                                    .ToList()
                            );

                            // 3. 重置所有 AGV (卸下货物、解开繁忙锁定、得分清零)
                            foreach (var name in _agvNames)
                            {
                                var agv = API.GetAgv(name);
                                _agvBusy[name] = false;
                                _stepRewards[name] = 0f;
//                                if (agv != null)
//                                {
//                                    var buf = agv.CargoAreas.FirstOrDefault(a => a.Name == "agv缓冲区");
//                                    var cargos = buf?.CargoChildren.ToList();
//                                    if (cargos != null)
//                                    {
//                                        foreach (var c in cargos) agv.CargoTake("agv缓冲区", c.Info.Name, (int)c.Quanlity);
//                                    }
//                                }
                            }
                            // 增加agv回到默认位置
                            // 预先定义默认位置
                            var parkingMap = new Dictionary<string, IStation>()
                            {
                                { "Agv-0", API.GetStation("308") },
                                { "Agv-1", API.GetStation("309") },
                                { "Agv-2", API.GetStation("310") },
                                { "Agv-3", API.GetStation("310") },
                                { "Agv-4", API.GetStation("311") },
                                { "Agv-5", API.GetStation("312") },
                                { "Agv-6", API.GetStation("313") },
                                { "Agv-7", API.GetStation("321") },
                                { "Agv-8", API.GetStation("322") },
                            };

                            API.Agvs.Foreach(agv =>
                            {
                                if (parkingMap.TryGetValue(agv.Name, out var parkingSta))
                                {
                                	agv.TaskKillAll();                               // 终止所有任务
                                    agv.TakeAll();                                   // 拿走所有货物
                                    agv.View.Radian = Math.PI;                      // 调整朝向
                                    agv.View.Move(parkingSta.View.KeyPoint, true);// 移动到停车站点
                                    agv.View.ChangeToNetwork(parkingSta.Network); // 切换到目标楼层的网络，防止不在同一楼层
                                    agv.View.CalcBound();                            // 重新计算边界
                                    agv.View.SafeInvalidate();                      // 刷新显示
                                }
                            });
                            
                            // 4. 重置电梯
                            _elevCurrentFloor["left"] = 1; _elevCurrentFloor["right"] = 1;
                            _elevLoadedAgv["left"] = null; _elevLoadedAgv["right"] = null;

                            Tools.trace("[RL 环境] /rl-reset 触发，全厂数据已重置归零！");
                            ctx.Response.ContentType = "application/json; charset=utf-8";
                            await ctx.Response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(new { state = GetCurrentRLState() }));
                        }
                        catch (Exception ex)
                        {
                            Tools.trace($"[RL 重置异常] {ex.Message}");
                            await ctx.Response.WriteAsync($"{{\"error\":\"{ex.Message}\"}}");
                        }
                    });
                });
                Tools.trace("[RL 环境] /rl-step 接口已就绪!");
            }
            catch (Exception ex) { Tools.trace($"[HTTP 启动失败] {ex.Message}"); }

            // 2. 初始化环境计时器
            foreach (var id in _sourceStations) _arrivalTimers[id] = _rand.Next(1, 10);
            foreach (var id in _processStations) { _processTimers[id] = 0; _isProcessingMap[id] = false; }
            foreach (var id in _allAssemblyStations) { _assemblyTimers[id] = 0; _isAssemblyMap[id] = false; }
            foreach (var name in _agvNames) _agvBusy[name] = false;

            // 3. 启动后台物理引擎法则 (货物生成与加工的自然演进)
            StartPhysicsEngine();

            // 保持主线程存活
            while (true) { Thread.Sleep(1000); }
        }

        // ====================================================================
        // 核心执行器：AGV 异步动作流 (★ 恢复智能上下文装卸逻辑)
        // ====================================================================
        private async Task ExecuteAgvTaskAsync(string agvName, AgvAction act)
        {
            _agvBusy[agvName] = true;
            try
            {
                var agv = API.GetAgv(agvName);
                var tgtSta = API.GetStation(act.Tgt.ToString());
                if (agv == null || tgtSta == null) return;

                Tools.trace($"[RL 执行] {agvName} 开始导航至站点 {act.Tgt}");

                // 1. 导航至目标站点
                agv.API.GoTo(act.Tgt.ToString(), null);
                while (agv.API.On != act.Tgt.ToString()) { await Task.Delay(200); }

                // 2. 智能上下文判定：空车取货，满载送货
                var buf = agv.CargoAreas.FirstOrDefault(a => a.Name == "agv缓冲区");
                int currentLoad = (int)(buf?.Quanlity ?? 0);

                if (currentLoad == 0)
                {
                    // ===== 意图：取货 (Take) =====
                    string srcAreaName = _sourceStations.Contains(act.Tgt) ? "输入机器" : "加工机器右区";
                    var srcArea = tgtSta.CargoAreas.FirstOrDefault(a => a.Name == srcAreaName);
                    var cargoToTake = srcArea?.CargoChildren.FirstOrDefault(); 
                    
                    if (cargoToTake != null && cargoToTake.Quanlity > 0)
                    {
                        int takeQty = (int)Math.Min(5, cargoToTake.Quanlity); 
                        tgtSta.CargoTake(srcAreaName, cargoToTake.Info.Name, takeQty);
                        agv.CargoPlace("agv缓冲区", cargoToTake.Info.Name, takeQty);
                        _stepRewards.AddOrUpdate(agvName, 2f, (k, v) => v + 2f); 
                        Tools.trace($"[RL] {agvName} 在 {act.Tgt} 自动提取了 {takeQty} 个 {cargoToTake.Info.Name}");
                    }
                    else
                    {
                        _stepRewards.AddOrUpdate(agvName, -2f, (k, v) => v - 2f);
                        Tools.trace($"[RL 惩罚] {agvName} 在 {act.Tgt} 扑空，扣除 -2 分");
                    }
                }
                else
                {
                    // ===== 意图：送货 (Place) =====
                    var carriedCargo = buf.CargoChildren.FirstOrDefault();
                    if (carriedCargo != null)
                    {
                        string cargoName = carriedCargo.Info.Name;
                        int placeQty = (int)carriedCargo.Quanlity;
                        bool canPlace = false;
                        string tgtAreaName = "加工机器左区";

                        // 核心业务法则强校验：车上的货，这个站收不收？
                        if (_roughProcessStations.Contains(act.Tgt) && _allSourceCargos.Contains(cargoName)) canPlace = true;
                        else if (_fineProcessStations.Contains(act.Tgt) && _allRoughCargos.Contains(cargoName)) canPlace = true;
                        else if (_assemblyStations13.Contains(act.Tgt) && (cargoName == "货物9" || cargoName == "货物10")) canPlace = true;
                        else if (_assemblyStations14.Contains(act.Tgt) && (cargoName == "货物11" || cargoName == "货物12")) canPlace = true;
                        else if (_outputStations.Contains(act.Tgt) && _allProductCargos.Contains(cargoName))
                        {
                            canPlace = true;
                            tgtAreaName = "输出机器";
                        }

                        if (canPlace)
                        {
                            agv.CargoTake("agv缓冲区", cargoName, placeQty);
                            tgtSta.CargoPlace(tgtAreaName, cargoName, placeQty);
                            
                            if (tgtAreaName == "输出机器") {
                                _stepRewards.AddOrUpdate(agvName, 50f, (k, v) => v + 50f);
                                Tools.trace($"[RL 终极奖励] {agvName} 成功送达成品至 {act.Tgt}，+50分!");
                            } else {
                                _stepRewards.AddOrUpdate(agvName, 10f, (k, v) => v + 10f);
                                Tools.trace($"[RL 奖励] {agvName} 成功跨站送达 {cargoName} 至 {act.Tgt}，+10分");
                            }
                        }
                        else
                        {
                            _stepRewards.AddOrUpdate(agvName, -2f, (k, v) => v - 2f);
                            Tools.trace($"[RL 惩罚] {agvName} 试图将 {cargoName} 卸载到错误的站点 {act.Tgt}，扣除 -2 分");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Tools.trace($"[RL 执行异常] {agvName}: {ex.Message}");
            }
            finally
            {
                _agvBusy[agvName] = false;
            }
        }

        // ====================================================================
        // 核心执行器：电梯控制与跨楼层装卸逻辑
        // ====================================================================
        private async Task ExecuteElevatorTaskAsync(string elevSide, int targetFloor)
        {
            try
            {
                // 1. 获取主站点 (left: 198, right: 199)
                string baseMark = elevSide == "left" ? "198" : "199";
                var baseSta = API.GetStation(baseMark);
                if (baseSta == null) return;
                
                var mainSta = baseSta.GetElevatorMainSta();
                if (mainSta == null) return;

                int currentFloor = _elevCurrentFloor.GetValueOrDefault(elevSide, 1);
                
                // 2. 界面更新：电梯移动到目标层
                if (mainSta is Station st) 
                {
                    st.SetElevatorFloor(targetFloor.ToString());
                }
                _elevCurrentFloor[elevSide] = targetFloor;

                // 3. 装载 / 卸载逻辑
                var loadedAgv = _elevLoadedAgv.GetValueOrDefault(elevSide, null);
                
                if (loadedAgv != null)
                {
                    // === 情况 A：卸车（将携带的AGV送到目标层） ===
                    if (currentFloor != targetFloor) 
                    {
                        string targetStaMark = elevSide == "left" ? $"{targetFloor}98" : $"{targetFloor}99";
                        var targetSta = (mainSta as Station)?.ElevatorStas.FirstOrDefault(x => x.Network.WareHouseFloorNumber == targetFloor.ToString()) ?? API.GetStation(targetStaMark);

                        if (targetSta != null)
                        {
                            // 传参给通讯协议做地图切换
                            var jobj = new JObject();
                            jobj.Add(new JProperty("命令", "切换楼层"));
                            jobj.Add(new JProperty("目标楼层", targetFloor));
                            jobj.Add(new JProperty("工作站点", mainSta.Mark));
                            loadedAgv.Tag2 = jobj;

                            // 触发地图切换与路径恢复
                            loadedAgv.DoAction("切换地图" + targetFloor);
                            (loadedAgv as Agv)?.EnterNewRouteItem(targetSta.Mark);
                            
                            Tools.trace($"[RL 电梯] {elevSide}梯将 {loadedAgv.Name} 成功送达 {targetFloor}F");
                            
                            // 给予电梯成功运送的奖励
                            _stepRewards.AddOrUpdate("elev_" + elevSide, 10f, (k, v) => v + 10f); 
                        }
                    }
                    // 到达目标楼层后释放 AGV，轿厢置空
                    _elevLoadedAgv[elevSide] = null; 
                }
                else
                {
                    // === 情况 B：装车 或 空转 ===
                    string checkStaMark = elevSide == "left" ? $"{targetFloor}98" : $"{targetFloor}99";
                    var checkSta = API.GetStation(checkStaMark);
                    
                    // 检查该楼层的桥接站是否有AGV在排队等待
                    var waitingAgv = checkSta?.AgvOns.FirstOrDefault();
                    
                    if (waitingAgv != null)
                    {
                        // 发现排队的AGV，吸入轿厢！
                        _elevLoadedAgv[elevSide] = waitingAgv;
                        Tools.trace($"[RL 电梯] {elevSide}梯在 {targetFloor}F 桥接站装载了 {waitingAgv.Name}");
                    }
                    else if (currentFloor != targetFloor)
                    {
                        // 没有车等，且发生了跨楼层移动，给予空转惩罚
                        _stepRewards.AddOrUpdate("elev_" + elevSide, -1f, (k, v) => v - 1f);
                        Tools.trace($"[RL 电梯] {elevSide}梯空载移动到 {targetFloor}F，惩罚 -1");
                    }
                }
                
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Tools.trace($"[RL 电梯执行异常] {elevSide}: {ex.Message}");
            }
        }

        // ====================================================================
        // ★ 修复：动作有效性智能拦截器 (Action Masking C# 端代理)
        // ====================================================================
        private bool CheckActionValid(string agvName, int tgt)
        {
            var agv = API.GetAgv(agvName);
            var tgtSta = API.GetStation(tgt.ToString());
            if (agv == null || tgtSta == null) return false;

            var buf = agv.CargoAreas.FirstOrDefault(a => a.Name == "agv缓冲区");
            int currentLoad = (int)(buf?.Quanlity ?? 0);

            if (currentLoad == 0)
            {
                // 空车去取货：目标站必须有货可取
                string srcAreaName = _sourceStations.Contains(tgt) ? "输入机器" : "加工机器右区";
                var srcArea = tgtSta.CargoAreas.FirstOrDefault(a => a.Name == srcAreaName);
                return srcArea != null && srcArea.CargoChildren.Any(c => c.Quanlity > 0);
            }
            else
            {
                // 满载去送货：目标站必须能接收该货物
                var carriedCargo = buf.CargoChildren.FirstOrDefault();
                if (carriedCargo == null) return false;
                string cargoName = carriedCargo.Info.Name;

                if (_roughProcessStations.Contains(tgt) && _allSourceCargos.Contains(cargoName)) return true;
                if (_fineProcessStations.Contains(tgt) && _allRoughCargos.Contains(cargoName)) return true;
                if (_assemblyStations13.Contains(tgt) && (cargoName == "货物9" || cargoName == "货物10")) return true;
                if (_assemblyStations14.Contains(tgt) && (cargoName == "货物11" || cargoName == "货物12")) return true;
                if (_outputStations.Contains(tgt) && _allProductCargos.Contains(cargoName)) return true;
                
                return false; // 送错站了，拦截！
            }
        }

        // ====================================================================
        // RL State 组装
        // ====================================================================
        private JObject GetCurrentRLState()
        {
            var state = new JObject();
            
            // AGV 状态
            var agvs = new JArray();
            foreach (var name in _agvNames)
            {
                var agv = API.GetAgv(name);
                string pos = agv?.API.On ?? "?";
                var buf = agv?.CargoAreas.FirstOrDefault(a => a.Name == "agv缓冲区");
                int load = (int)(buf?.Quanlity ?? 0);
                int isBusy = _agvBusy.GetValueOrDefault(name, false) ? 1 : 0;
                
                agvs.Add(new JObject {
                    ["name"] = name,
                    ["position"] = pos,
                    ["load"] = load,
                    ["is_busy"] = isBusy
                });
            }
            state["agvs"] = agvs;
            
            // 站点聚合状态 (全局宏观库存)
            var stations = new JArray();
            foreach (var id in _sourceStations.Concat(_roughProcessStations).Concat(_fineProcessStations).Concat(_allAssemblyStations).Concat(_outputStations))
            {
                var sta = API.GetStation(id.ToString());
                if (sta == null) continue;
                var left = sta.CargoAreas.FirstOrDefault(a => a.Name == "输入机器" || a.Name == "加工机器左区" || a.Name == "输出机器");
                var right = sta.CargoAreas.FirstOrDefault(a => a.Name == "加工机器右区");
                stations.Add(new JObject {
                    ["mark"] = id.ToString(),
                    ["leftQty"] = left?.Quanlity ?? 0,
                    ["rightQty"] = right?.Quanlity ?? 0
                });
            }
            state["stations"] = stations;

            // 电梯与排队状态
            var elevators = new JObject();
            foreach (var side in new[] { "left", "right" }) 
            {
                int floor = _elevCurrentFloor.GetValueOrDefault(side, 1);
                bool hasAgv = _elevLoadedAgv.GetValueOrDefault(side, null) != null;
                
                // 统计该侧各楼层桥接入口站的排队车辆数
                int wait1 = API.GetStation(side == "left" ? "198" : "199")?.AgvOns.Count() ?? 0;
                int wait2 = API.GetStation(side == "left" ? "298" : "299")?.AgvOns.Count() ?? 0;
                int wait3 = API.GetStation(side == "left" ? "398" : "399")?.AgvOns.Count() ?? 0;
                
                elevators[side] = new JObject {
                    ["current_floor"] = floor,
                    ["has_agv"] = hasAgv ? 1 : 0,
                    ["wait_1F"] = wait1,
                    ["wait_2F"] = wait2,
                    ["wait_3F"] = wait3
                };
            }
            state["elevators"] = elevators;

            return state;
        }

        private bool CheckIfDone()
        {
            int outputFreeSlots = 0;
            foreach (var id in _outputStations)
            {
                var sta = API.GetStation(id.ToString());
                if (sta == null) continue;
                var outArea = sta.CargoAreas.FirstOrDefault(a => a.Name == "输出机器");
                if (outArea != null)
                {
                    int maxCap = outArea.Info.EnableLimitedQuanlity ? (int)outArea.Info.LimitedQuanlity : 10;
                    outputFreeSlots += (int)(maxCap - outArea.Quanlity);
                }
            }
            return outputFreeSlots <= 0;
        }

        // ====================================================================
        // 原封不动的物理引擎：原汁原味还原逻辑A、C、D的指数分布与UI更新
        // ====================================================================
        private void StartPhysicsEngine()
        {
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
                    // ★ 恢复用于地图 UI 显示的 SetValue
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
                    // ★ 恢复用于地图 UI 显示的 SetValue
                    string pn = isProduct13 ? "13" : "14";
                    sta.SetValue("加工状态", _isAssemblyMap[id] ? $"[组装{pn}]组装中:{(int)_assemblyTimers[id]}s" : "等待进料");
                }
            }, 1000);
        }
        
        public void Kill() { }
    }
}                