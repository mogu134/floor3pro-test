import numpy as np
import requests
import json
import time
import random

# ==========================================
# 1. 绝对映射的特征提取器 (破除维度灾难)
# ==========================================
class FeatureExtractor:
    def __init__(self):
        self.agv_names = ["Agv-0", "Agv-3", "Agv-4", "Agv-2", "Agv-5", "Agv-6", "Agv-1", "Agv-7", "Agv-8"]
        self.elev_sides = ["left", "right"]
        
        # 完美复刻 C# 物理地标
        self.source_stations = list(range(300, 307))           # 7个
        self.rough_stations = list(range(307, 335))            # 28个
        self.fine_stations = list(range(201, 241))             # 40个
        self.assembly_stations = list(range(105, 125))         # 20个
        self.output_stations = list(range(100, 105))           # 5个
        
        self.all_stations = sorted(
            self.source_stations + self.rough_stations + 
            self.fine_stations + self.assembly_stations + self.output_stations
        )
        self.num_stations = len(self.all_stations) # 精确的 100 个站
        
        # 双向映射字典
        self.sta_to_idx = {str(sta): i for i, sta in enumerate(self.all_stations)}
        self.idx_to_sta = {i: str(sta) for i, sta in enumerate(self.all_stations)}
        self.max_capacity = 5

    def parse_state(self, raw_json_state):
        state = raw_json_state if isinstance(raw_json_state, dict) else json.loads(raw_json_state)
        
        # --- 1. 提取全厂宏观库存，并建立快速查询字典 ---
        station_features = []
        sta_qtys = {} # 方便后续做 Mask 掩码查询
        for sta in state.get("stations", []):
            mark = sta["mark"]
            l_qty = sta["leftQty"]
            r_qty = sta["rightQty"]
            sta_qtys[mark] = (l_qty, r_qty)
            station_features.extend([l_qty / 10.0, r_qty / 10.0])
        station_features = np.array(station_features, dtype=np.float32)

        # --- 2. 提取电梯特征 ---
        elev_features = []
        for side in self.elev_sides:
            elev = state["elevators"][side]
            elev_features.extend([
                elev["current_floor"] / 3.0, elev["has_agv"],
                elev["wait_1F"] / 5.0, elev["wait_2F"] / 5.0, elev["wait_3F"] / 5.0
            ])
        elev_features = np.array(elev_features, dtype=np.float32)

        obs_dict = {}
        mask_dict = {}
        all_agv_features = []

        # --- 3. 提取 AGV 局部特征并生成【智能动作掩码 Action Mask】 ---
        for agv_data in state.get("agvs", []):
            name = agv_data["name"]
            pos_mark = agv_data["position"]
            
            floor_val = int(pos_mark[0]) / 3.0 if pos_mark.isdigit() and len(pos_mark) == 3 else 0.0
            pos_idx = self.sta_to_idx.get(pos_mark, -1) 
            pos_norm = (pos_idx + 1) / (self.num_stations + 1)
            
            load_val = agv_data["load"] / float(self.max_capacity)
            is_busy = float(agv_data["is_busy"])
            
            agv_feature = np.array([floor_val, pos_norm, load_val, is_busy], dtype=np.float32)
            all_agv_features.append(agv_feature)
            
            # 拼装局部观测：自身(4) + 全局库存(200) + 电梯(10)
            local_obs = np.concatenate([agv_feature, station_features, elev_features])
            obs_dict[name] = local_obs
            
            # ====================================================
            # ★ 核心业务掩码逻辑 (破除 AGV 盲目探索，极大加速训练)
            # ====================================================
            action_mask = np.ones(1 + self.num_stations, dtype=np.bool_)
            
            if is_busy == 1.0:
                # 状态 A：忙碌中，屏蔽所有移动指令，只能选 Idle(索引0)
                action_mask[1:] = False 
            else:
                # 遍历所有 100 个站点的候选动作 (注意 idx 要 +1)
                for mark, idx in self.sta_to_idx.items():
                    act_idx = idx + 1
                    sta_num = int(mark)
                    l_qty, r_qty = sta_qtys.get(mark, (0, 0))

                    if load_val == 0.0:
                        # 状态 B：空车载货 -> 意图是去【取货】
                        # 规则 1：输入站必须有原材料 (leftQty > 0)
                        if sta_num in self.source_stations and l_qty == 0:
                            action_mask[act_idx] = False
                        # 规则 2：加工站和组装站必须有产成品 (rightQty > 0)
                        elif sta_num in (self.rough_stations + self.fine_stations + self.assembly_stations) and r_qty == 0:
                            action_mask[act_idx] = False
                        # 规则 3：绝对不能去输出站取货 (输出站只进不出)
                        elif sta_num in self.output_stations:
                            action_mask[act_idx] = False
                            
                    else:
                        # 状态 C：满载送货 -> 意图是去【卸货】
                        # 规则 1：绝对不能把货往回送到输入机器！
                        if sta_num in self.source_stations:
                            action_mask[act_idx] = False
                        # 规则 2：如果你能让 C# 返回 cargo_type，这里还能精确屏蔽掉不需要当前货物的机器。
                        # 目前采用基础屏蔽，如果 AGV 瞎送货（例如把原材料送去组装机器），C# 将下发 -2 分使其学会自我纠正。

            # 载货原地发呆也没意义，空车且全厂没货可取时才允许原地待命
            if load_val > 0.0:
                action_mask[0] = False 
                
            # 安全兜底：如果所有动作都被屏蔽了（全厂没货且AGV空载），强制允许 Idle
            if not action_mask.any():
                action_mask[0] = True
                
            mask_dict[name] = action_mask

        # --- 4. 拼装 Global State ---
        global_state = np.concatenate(all_agv_features + [elev_features, station_features])
        return obs_dict, global_state, mask_dict

# ==========================================
# 2. HTTP 环境交互封装器
# ==========================================
class FactoryHttpEnv:
    def __init__(self, host="http://localhost:58080"):
        self.host = host
        self.step_url = f"{self.host}/rl-step"
        self.reset_url = f"{self.host}/rl-reset"
        
        self.extractor = FeatureExtractor()
        self.agv_names = self.extractor.agv_names
        self.elev_sides = self.extractor.elev_sides
        self.last_parsed_state = None # 记录最新的状态字典，用于判断需求

    def reset(self):
        """通知 C# 重置工厂物理世界，获取初始状态"""
        max_retries = 3
        for attempt in range(max_retries):
            try:
                response = requests.post(self.reset_url, timeout=5).json()
                self.last_parsed_state = response["state"] if isinstance(response["state"], dict) else json.loads(response["state"])
                obs_dict, global_state, mask_dict = self.extractor.parse_state(self.last_parsed_state)
                return obs_dict, global_state, mask_dict
            except requests.exceptions.RequestException as e:
                print(f"[RL Env] 重置请求失败 (尝试 {attempt+1}/{max_retries})... 错误: {e}")
                time.sleep(1)
        raise ConnectionError("无法连接到 C# 仿真环境的 /rl-reset 接口，请确保 ProfControl 已启动并运行脚本。")

    def step(self, agv_actions, elev_actions):
        """
        向 C# 派发动作，推进 1 步 (1秒)
        agv_actions: dict, {"Agv-0": action_idx, ...} (范围 0~100)
        elev_actions: dict, {"left": action_idx, ...} (范围 0~3)
        """
        payload = {
            "Agvs": {},
            "Elevators": {}
        }
        
        # 1. 组装 AGV 动作
        for name, act_idx in agv_actions.items():
            if act_idx == 0:
                payload["Agvs"][name] = {"Action": "Idle", "Tgt": 0}
            else:
                target_idx = act_idx - 1
                target_sta_mark = int(self.extractor.idx_to_sta[target_idx])
                payload["Agvs"][name] = {
                    "Action": "Transport", 
                    "Tgt": target_sta_mark 
                }
                
        # 2. 组装电梯动作 (0=Idle, 1=1F, 2=2F, 3=3F)
        for name, act_idx in elev_actions.items():
            # ======= 【修改 1】: 需求驱动的动作掩码 (防止电梯空转) =======
            if self.last_parsed_state is not None:
                elev_state = self.last_parsed_state["elevators"].get(name, {})
                has_agv = elev_state.get("has_agv", 0)
                wait_1f = elev_state.get("wait_1F", 0)
                wait_2f = elev_state.get("wait_2F", 0)
                wait_3f = elev_state.get("wait_3F", 0)
                total_demand = has_agv + wait_1f + wait_2f + wait_3f
                
                # 如果轿厢内没车，且各个楼层都没有排队等待的AGV，强制让网络输出的动作归零(Idle)
                if total_demand == 0:
                    act_idx = 0
            # ============================================================

            if act_idx == 0:
                payload["Elevators"][name] = {"Action": "Idle", "TargetFloor": 1}
            else:
                payload["Elevators"][name] = {
                    "Action": "Move", 
                    "TargetFloor": int(act_idx)
                }

        # 3. HTTP 同步阻塞调用
        try:
            response = requests.post(self.step_url, json=payload, timeout=3).json()
        except requests.exceptions.Timeout:
            print("[RL Env] 警告：C# 物理步进超时。")
            return None, None, None, None, True
        except Exception as e:
            print(f"[RL Env] 与环境断开连接: {e}")
            return None, None, None, None, True
            
        # 4. 解析返回值并更新最新状态
        self.last_parsed_state = response["state"] if isinstance(response["state"], dict) else json.loads(response["state"])
        obs_dict, global_state, mask_dict = self.extractor.parse_state(self.last_parsed_state)
        rewards = response["rewards"]
        done = response["done"]
        
        return obs_dict, global_state, rewards, mask_dict, done