import os
import time
import h5py
import logging
import torch
import numpy as np
from torch.optim import Adam
from torch.distributions import Categorical
import torch.nn.utils as nn_utils

# ★ 新增：TensorBoard
from torch.utils.tensorboard import SummaryWriter

# 导入我们之前写好的环境和模型
from env_wrapper import FactoryHttpEnv
from mappo_models import AgvActor, ElevatorActor, GlobalCritic, compute_gae

# ==========================================
# 1. 训练超参数配置
# ==========================================
class MAPPOArgs:
    num_episodes = 200       # 训练总回合数
    steps_per_episode = 2000   # 每个回合运行的秒数(步数)
    
    lr_actor = 3e-4           # Actor 学习率
    lr_critic = 1e-3          # Critic 学习率
    gamma = 0.99              # 奖励折扣因子
    gae_lambda = 0.95         # GAE 平滑参数
    clip_ratio = 0.2          # PPO 裁剪系数
    entropy_coef = 0.05       # 探索激励系数
    max_grad_norm = 0.5       # 梯度裁剪防爆炸
    
    save_dir = "./saved_models" # 模型保存路径
    traj_dir = "./trajectories" # 离线轨迹保存路径

    # ★ 新增：断点续训控制参数
    load_model = True         
    resume_episode = 200      

# ==========================================
# 2. 训练主引擎
# ==========================================
def train():
    args = MAPPOArgs()
    os.makedirs(args.save_dir, exist_ok=True)
    os.makedirs(args.traj_dir, exist_ok=True)

    # ======= 【修改 3】: 配置同步日志 =======
    timestamp = time.strftime("%Y%m%d_%H%M%S")
    log_filename = f"python_train_{timestamp}.log"
    logging.basicConfig(
        level=logging.INFO,
        format='[%(asctime)s.%(msecs)03d] [%(levelname)s] - %(message)s',
        datefmt='%Y-%m-%d %H:%M:%S',
        handlers=[
            logging.FileHandler(log_filename, encoding='utf-8'),
            logging.StreamHandler()
        ]
    )

    # ======= 【修改 4】: 接入 TensorBoard =======
    writer = SummaryWriter(log_dir=f"./runs/mappo_experiment_{timestamp}")
    
    # ======= 【修改 2】: 初始化离线经验池 =======
    trajectory_memory = []

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    logging.info(f"🔥 开始 MAPPO 训练，使用设备: {device}")

    # 1. 初始化环境与固定名称顺序
    env = FactoryHttpEnv()
    agv_names = env.agv_names
    elev_names = env.elev_sides
    num_agvs = len(agv_names)
    num_elevs = len(elev_names)

    # 2. 实例化网络与优化器
    agv_actor = AgvActor().to(device)
    elev_actor = ElevatorActor().to(device)
    global_critic = GlobalCritic().to(device)

    opt_agv = Adam(agv_actor.parameters(), lr=args.lr_actor)
    opt_elev = Adam(elev_actor.parameters(), lr=args.lr_actor)
    opt_critic = Adam(global_critic.parameters(), lr=args.lr_critic)
    
    # 断点续训加载模型
    if args.load_model:
        agv_model_path = f"{args.save_dir}/agv_actor_ep{args.resume_episode}.pth"
        critic_model_path = f"{args.save_dir}/global_critic_ep{args.resume_episode}.pth"
        if os.path.exists(agv_model_path) and os.path.exists(critic_model_path):
            agv_actor.load_state_dict(torch.load(agv_model_path, map_location=device))
            global_critic.load_state_dict(torch.load(critic_model_path, map_location=device))
            logging.info(f"🧠 成功注入预训练记忆！(基于 Episode {args.resume_episode})")
        else:
            logging.warning("⚠️ 找不到预训练模型，将从零开始训练。")

    # 3. 开始回合大循环
    for episode in range(1, args.num_episodes + 1):
        try:
            obs_dict, global_state, mask_dict = env.reset()
        except Exception as e:
            logging.error(f"环境重置失败，跳过本回合: {e}")
            continue
            
        buffer = {
            'agv_obs': [], 'agv_masks': [], 'agv_actions': [], 'agv_logprobs': [],
            'elev_obs': [], 'elev_actions': [], 'elev_logprobs': [],
            'global_states': [], 'rewards': [], 'values': [], 'dones': []
        }
        
        agv_hidden = (torch.zeros(1, num_agvs, 128).to(device), 
                      torch.zeros(1, num_agvs, 128).to(device))

        ep_reward_sum = 0
        
        for step in range(args.steps_per_episode):
            agv_obs_tensor = torch.tensor(np.array([obs_dict[n] for n in agv_names]), dtype=torch.float32).to(device)
            agv_mask_tensor = torch.tensor(np.array([mask_dict[n] for n in agv_names]), dtype=torch.bool).to(device)
            global_tensor = torch.tensor(global_state, dtype=torch.float32).to(device)
            
            with torch.no_grad():
                agv_probs, agv_hidden = agv_actor(agv_obs_tensor.unsqueeze(1), agv_hidden, agv_mask_tensor)
                agv_dist = Categorical(agv_probs)
                agv_actions = agv_dist.sample()
                agv_logprobs = agv_dist.log_prob(agv_actions)
                
            with torch.no_grad():
                elev_obs_tensor = global_tensor.unsqueeze(0).repeat(num_elevs, 1)
                elev_probs = elev_actor(elev_obs_tensor)
                elev_dist = Categorical(elev_probs)
                elev_actions = elev_dist.sample()
                elev_logprobs = elev_dist.log_prob(elev_actions)

            with torch.no_grad():
                val = global_critic(global_tensor.unsqueeze(0)).squeeze()

            action_dict_agv = {agv_names[i]: int(agv_actions[i].item()) for i in range(num_agvs)}
            action_dict_elev = {elev_names[i]: int(elev_actions[i].item()) for i in range(num_elevs)}

            next_obs_dict, next_global_state, rewards_dict, next_mask_dict, done = env.step(action_dict_agv, action_dict_elev)
            
            if done is True and next_obs_dict is None:
                logging.warning("通信异常，提前结束本回合")
                break

            step_reward = sum(rewards_dict.values())
            ep_reward_sum += step_reward

            buffer['agv_obs'].append(agv_obs_tensor)
            buffer['agv_masks'].append(agv_mask_tensor)
            buffer['agv_actions'].append(agv_actions)
            buffer['agv_logprobs'].append(agv_logprobs)
            buffer['elev_obs'].append(elev_obs_tensor)
            buffer['elev_actions'].append(elev_actions)
            buffer['elev_logprobs'].append(elev_logprobs)
            buffer['global_states'].append(global_tensor)
            buffer['rewards'].append(step_reward)
            buffer['values'].append(val)
            buffer['dones'].append(1.0 if done else 0.0)

            # ======= 【修改 2】: 写入轨迹存储池 =======
            trajectory_memory.append({
                "global_state": global_state.tolist(), # 存为列表防止序列化报错
                "agv_actions": action_dict_agv,
                "elev_actions": action_dict_elev,
                "rewards": rewards_dict,
                "done": done
            })
            
            obs_dict, global_state, mask_dict = next_obs_dict, next_global_state, next_mask_dict
            
            if done:
                break

        if len(buffer['rewards']) == 0: continue 

        with torch.no_grad():
            last_global_tensor = torch.tensor(global_state, dtype=torch.float32).to(device)
            last_val = global_critic(last_global_tensor.unsqueeze(0)).squeeze()

        b_rewards = torch.tensor(buffer['rewards'], dtype=torch.float32).to(device).unsqueeze(1)
        b_values = torch.stack(buffer['values']).unsqueeze(1)
        b_dones = torch.tensor(buffer['dones'], dtype=torch.float32).to(device).unsqueeze(1)

        advantages, returns = compute_gae(b_rewards, b_values, b_dones, last_val, args.gamma, args.gae_lambda)

        b_agv_obs = torch.stack(buffer['agv_obs'])
        b_agv_masks = torch.stack(buffer['agv_masks'])
        b_agv_acts = torch.stack(buffer['agv_actions'])
        b_old_agv_logp = torch.stack(buffer['agv_logprobs'])

        b_elev_obs = torch.stack(buffer['elev_obs'])
        b_elev_acts = torch.stack(buffer['elev_actions'])
        b_old_elev_logp = torch.stack(buffer['elev_logprobs'])
        b_global = torch.stack(buffer['global_states'])

        # --- 1. 更新 Global Critic ---
        new_values = global_critic(b_global).squeeze(1)
        critic_loss = torch.nn.functional.mse_loss(new_values, returns.squeeze(1))
        
        opt_critic.zero_grad()
        critic_loss.backward()
        nn_utils.clip_grad_norm_(global_critic.parameters(), args.max_grad_norm)
        opt_critic.step()

        # --- 2. 更新 AGV Actor ---
        hidden = (torch.zeros(1, num_agvs, 128).to(device), torch.zeros(1, num_agvs, 128).to(device))
        agv_seq_obs = b_agv_obs.transpose(0, 1) 
        
        x = torch.nn.functional.relu(agv_actor.fc1(agv_seq_obs))
        x = torch.nn.functional.relu(agv_actor.fc2(x))
        x, _ = agv_actor.lstm(x, hidden) 
        logits = agv_actor.action_out(x)
        
        logits = logits.transpose(0, 1)
        logits = logits.masked_fill(~b_agv_masks, -1e9)
        
        dist_agv = Categorical(logits=logits)
        new_agv_logp = dist_agv.log_prob(b_agv_acts)
        entropy_agv = dist_agv.entropy().mean()

        ratio_agv = torch.exp(new_agv_logp - b_old_agv_logp)
        adv_expanded = advantages.expand_as(ratio_agv)
        
        surr1_agv = ratio_agv * adv_expanded
        surr2_agv = torch.clamp(ratio_agv, 1.0 - args.clip_ratio, 1.0 + args.clip_ratio) * adv_expanded
        actor_loss_agv = -torch.min(surr1_agv, surr2_agv).mean() - args.entropy_coef * entropy_agv
        
        opt_agv.zero_grad()
        actor_loss_agv.backward()
        nn_utils.clip_grad_norm_(agv_actor.parameters(), args.max_grad_norm)
        opt_agv.step()

        # --- 3. 更新 Elevator Actor ---
        elev_probs = elev_actor(b_elev_obs)
        dist_elev = Categorical(elev_probs)
        new_elev_logp = dist_elev.log_prob(b_elev_acts)
        entropy_elev = dist_elev.entropy().mean()
        
        ratio_elev = torch.exp(new_elev_logp - b_old_elev_logp)
        adv_expanded_elev = advantages.expand_as(ratio_elev)
        
        surr1_elev = ratio_elev * adv_expanded_elev
        surr2_elev = torch.clamp(ratio_elev, 1.0 - args.clip_ratio, 1.0 + args.clip_ratio) * adv_expanded_elev
        actor_loss_elev = -torch.min(surr1_elev, surr2_elev).mean() - args.entropy_coef * entropy_elev

        opt_elev.zero_grad()
        actor_loss_elev.backward()
        nn_utils.clip_grad_norm_(elev_actor.parameters(), args.max_grad_norm)
        opt_elev.step()

        # ======= 【修改 4】: 将关键指标写入 TensorBoard =======
        writer.add_scalar("Training/Total_Reward", ep_reward_sum, episode)
        writer.add_scalar("Loss/Critic_Loss", critic_loss.item(), episode)
        writer.add_scalar("Loss/Actor_AGV_Loss", actor_loss_agv.item(), episode)
        writer.add_scalar("Loss/Actor_Elev_Loss", actor_loss_elev.item(), episode)

        # 日志输出 (自动带毫秒级时间戳)
        logging.info(f"Ep {episode:04d} | 总奖励: {ep_reward_sum:7.1f} | 损失 [Critic: {critic_loss.item():.2f}, AGV: {actor_loss_agv.item():.2f}, Elev: {actor_loss_elev.item():.2f}]")

        # ======= 【修改 2】: 每 10 局保存一次离线交互轨迹 =======
        # ======= 【修改 2: HDF5 离线轨迹保存】 =======
        if episode % 10 == 0 and len(trajectory_memory) > 0:
            traj_filepath = f"{args.traj_dir}/traj_ep{episode}_{time.strftime('%Y%m%d_%H%M%S')}.h5"
            
            # 使用 h5py 创建文件
            with h5py.File(traj_filepath, 'w') as f:
                # 1. 提取并转换全局状态和完成标志为连续的 NumPy 数组
                global_states = np.array([step["global_state"] for step in trajectory_memory], dtype=np.float32)
                dones = np.array([step["done"] for step in trajectory_memory], dtype=np.bool_)
                
                # 写入根目录
                f.create_dataset("global_states", data=global_states, compression="gzip")
                f.create_dataset("dones", data=dones, compression="gzip")
                
                # 2. 创建 agv_actions 分组
                agv_grp = f.create_group("agv_actions")
                # 遍历每台 AGV 的名字 (Agv-0, Agv-1...)
                for agv_name in trajectory_memory[0]["agv_actions"].keys():
                    agv_data = np.array([step["agv_actions"][agv_name] for step in trajectory_memory], dtype=np.int32)
                    agv_grp.create_dataset(agv_name, data=agv_data, compression="gzip")
                
                # 3. 创建 elev_actions 分组
                elev_grp = f.create_group("elev_actions")
                for elev_name in trajectory_memory[0]["elev_actions"].keys():
                    elev_data = np.array([step["elev_actions"][elev_name] for step in trajectory_memory], dtype=np.int32)
                    elev_grp.create_dataset(elev_name, data=elev_data, compression="gzip")
                
                # 4. 创建 rewards 分组
                # rew_grp = f.create_group("rewards")
                # for agent_name in trajectory_memory[0]["rewards"].keys():
                #     rew_data = np.array([step["rewards"][agent_name] for step in trajectory_memory], dtype=np.float32)
                #     rew_grp.create_dataset(agent_name, data=rew_data, compression="gzip")
                
                rew_grp = f.create_group("rewards")
                
                # 获取这 10 个回合中出现过的所有奖励的键（解决 C# 动态省略 0 值字段的问题）
                all_reward_keys = set()
                for step in trajectory_memory:
                    all_reward_keys.update(step["rewards"].keys())
                
                for agent_name in all_reward_keys:
                    # 使用 .get(agent_name, 0.0) 进行安全获取，防止 KeyError
                    rew_data = np.array([step["rewards"].get(agent_name, 0.0) for step in trajectory_memory], dtype=np.float32)
                    rew_grp.create_dataset(agent_name, data=rew_data, compression="gzip")

            trajectory_memory.clear() # 保存后清理内存
            logging.info(f"💾 离线交互轨迹(HDF5格式)已打包保存至: {traj_filepath}")

        # 每 100 局保存一次权重
        if episode % 100 == 0:
            torch.save(agv_actor.state_dict(), f"{args.save_dir}/agv_actor_ep{episode}.pth")
            torch.save(global_critic.state_dict(), f"{args.save_dir}/global_critic_ep{episode}.pth")
            logging.info(f"✅ 模型权重已保存至 {args.save_dir}")

    writer.close() # 训练结束关闭 TensorBoard 句柄

if __name__ == "__main__":
    train()