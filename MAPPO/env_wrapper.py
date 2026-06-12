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
        
        # 1. 宏观库存特征 (200维)
        station_features = []
        for sta in state.get("stations", []):
            station_features.extend([sta["leftQty"] / 10.0, sta["rightQty"] / 10.0])
        station_features = np.array(station_features, dtype=np.float32)

        # 2. 电梯特征 (10维)
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

        # 3. AGV 局部特征与掩码
        for agv_data in state.get("agvs", []):
            name = agv_data["name"]
            pos_mark = agv_data["position"]
            
            # 楼层感知与位置索引
            floor_val = int(pos_mark[0]) / 3.0 if pos_mark.isdigit() and len(pos_mark) == 3 else 0.0
            pos_idx = self.sta_to_idx.get(pos_mark, -1) 
            pos_norm = (pos_idx + 1) / (self.num_stations + 1)
            
            load_val = agv_data["load"] / float(self.max_capacity)
            is_busy = float(agv_data["is_busy"])
            
            agv_feature = np.array([floor_val, pos_norm, load_val, is_busy], dtype=np.float32)
            all_agv_features.append(agv_feature)
            
            # 局部观测 = 自身特征(4) + 库存(200) + 电梯(10) = 214维
            local_obs = np.concatenate([agv_feature, station_features, elev_features])
            obs_dict[name] = local_obs
            
            # 动作遮罩 (1个Idle + 100个去站点的动作 = 101维)
            action_mask = np.ones(1 + self.num_stations, dtype=np.bool_)
            if is_busy == 1.0:
                action_mask[1:] = False # 忙碌时强制屏蔽移动指令，只能选 Idle (索引0)
            mask_dict[name] = action_mask

        # 4. 全局状态 = 所有AGV(36) + 电梯(10) + 库存(200) = 246维
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

    def reset(self):
        """通知 C# 重置工厂物理世界，获取初始状态"""
        max_retries = 3
        for attempt in range(max_retries):
            try:
                response = requests.post(self.reset_url, timeout=5).json()
                obs_dict, global_state, mask_dict = self.extractor.parse_state(response["state"])
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
        # 注意：这里的 Key 使用了大写首字母，严格对齐 C# 的 DTO 类
        payload = {
            "Agvs": {},
            "Elevators": {}
        }
        
        # 1. 组装 AGV 动作 (触发 C# 的智能装卸)
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
            if act_idx == 0:
                payload["Elevators"][name] = {"Action": "Idle", "TargetFloor": 1}
            else:
                payload["Elevators"][name] = {
                    "Action": "Move", 
                    "TargetFloor": int(act_idx)
                }

        # 3. HTTP 同步阻塞调用 (等待 C# 演进一秒)
        try:
            # timeout 设为 3 秒，防止 C# 发生死锁卡死 Python
            response = requests.post(self.step_url, json=payload, timeout=3).json()
        except requests.exceptions.Timeout:
            print("[RL Env] 警告：C# 物理步进超时。")
            return None, None, None, None, True
        except Exception as e:
            print(f"[RL Env] 与环境断开连接: {e}")
            return None, None, None, None, True
            
        # 4. 解析返回值
        obs_dict, global_state, mask_dict = self.extractor.parse_state(response["state"])
        rewards = response["rewards"]
        done = response["done"]
        
        return obs_dict, global_state, rewards, mask_dict, done

# ==========================================
# 3. 联调测试主程序 (Random Agent)
# ==========================================
if __name__ == "__main__":
    print("🚀 正在初始化与 C# 仿真环境的连接...")
    env = FactoryHttpEnv()
    
    print("⏳ 尝试发送 /rl-reset 重置环境...")
    try:
        obs, global_state, masks = env.reset()
        print("✅ 环境重置成功！")
        print(f"📊 全局状态向量维度: {global_state.shape} (预期为 246)")
        print(f"📊 单个 AGV 局部观测维度: {obs['Agv-0'].shape} (预期为 214)")
    except Exception as e:
        print(f"❌ 初始化失败，请检查 C# 端报错: {e}")
        exit()

    print("\n" + "="*40)
    print("开始执行随机调度测试 (运行 10 步)")
    print("="*40)
    
    for step in range(10):
        print(f"\n--- 第 {step + 1} 步 ---")
        
        # 为每台 AGV 随机选择一个合法的动作
        agv_actions = {}
        for agv_name in env.agv_names:
            valid_action_indices = np.where(masks[agv_name])[0]
            # 从允许的动作(mask为True)中随机选一个
            chosen_action = int(np.random.choice(valid_action_indices))
            agv_actions[agv_name] = chosen_action
            
        # 电梯随机动作 (0: 停着, 1,2,3: 去对应楼层)
        elev_actions = {
            "left": random.randint(0, 3),
            "right": random.randint(0, 3)
        }
        
        # 将动作下发给 C# 环境
        obs, global_state, rewards, masks, done = env.step(agv_actions, elev_actions)
        
        if done is True and obs is None:
            print("❌ 步进超时或网络断开。")
            break
            
        # 打印即时奖励，看看有没有被 C# 拦截或加分
        print("🎁 本步奖励回传:")
        for name, r in rewards.items():
            if r != 0: # 只打印有动作分数的智能体
                print(f"  {name}: {r} 分")
                
        if done:
            print("🎉 环境报告订单已全部完成！")
            break
            
        time.sleep(0.5) # 稍微放慢一点，方便您观察 C# 地图上的动画