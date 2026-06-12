import os
import torch
import numpy as np
from torch.optim import Adam
from torch.distributions import Categorical
import torch.nn.utils as nn_utils

# 导入我们之前写好的环境和模型
from env_wrapper import FactoryHttpEnv
from mappo_models import AgvActor, ElevatorActor, GlobalCritic, compute_gae

# ==========================================
# 1. 训练超参数配置
# ==========================================
class MAPPOArgs:
    num_episodes = 100       # 训练总回合数
    steps_per_episode = 2400   # 每个回合运行的秒数(步数)
    
    lr_actor = 3e-4           # Actor 学习率
    lr_critic = 1e-3          # Critic 学习率
    gamma = 0.99              # 奖励折扣因子
    gae_lambda = 0.95         # GAE 平滑参数
    clip_ratio = 0.2          # PPO 裁剪系数
    entropy_coef = 0.05       # 探索激励系数 (随着训练可以衰减)
    max_grad_norm = 0.5       # 梯度裁剪防爆炸
    
    save_dir = "./saved_models" # 模型保存路径

    # ★ 新增：断点续训控制参数
    load_model = True         # 是否加载预训练模型
    resume_episode = 500      # 假设阶段一在第 500 局停下，就填 500
# ==========================================
# 2. 训练主引擎
# ==========================================
def train():
    args = MAPPOArgs()
    os.makedirs(args.save_dir, exist_ok=True)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"🔥 开始 MAPPO 训练，使用设备: {device}")

    # 1. 初始化环境与固定名称顺序
    env = FactoryHttpEnv()
    agv_names = env.agv_names
    elev_names = env.elev_sides
    num_agvs = len(agv_names)
    num_elevs = len(elev_names)

    # 2. 实例化网络与优化器
    agv_actor = AgvActor().to(device)
    elev_actor = ElevatorActor().to(device) # 电梯动作空间为4(0,1,2,3)
    global_critic = GlobalCritic().to(device)

    opt_agv = Adam(agv_actor.parameters(), lr=args.lr_actor)
    opt_elev = Adam(elev_actor.parameters(), lr=args.lr_actor)
    opt_critic = Adam(global_critic.parameters(), lr=args.lr_critic)
    
    # ==========================================
    # ★ 新增：加载阶段一的“脑子” (断点续训)
    # ==========================================
    if args.load_model:
        agv_model_path = f"{args.save_dir}/agv_actor_ep{args.num_episodes}.pth"
        critic_model_path = f"{args.save_dir}/global_critic_ep{args.num_episodes}.pth"
        #print(f"{agv_model_path} and {os.path.exists(critic_model_path)}")
        if os.path.exists(agv_model_path) and os.path.exists(critic_model_path):
            agv_actor.load_state_dict(torch.load(agv_model_path, map_location=device))
            global_critic.load_state_dict(torch.load(critic_model_path, map_location=device))
            print(f"🧠 成功注入预训练记忆！(基于 Episode {args.num_episodes})")
        else:
            print("⚠️ 找不到预训练模型，将从零开始训练。")

    # 3. 开始回合大循环
    for episode in range(1, args.num_episodes + 1):
        try:
            obs_dict, global_state, mask_dict = env.reset()
        except Exception as e:
            print(f"环境重置失败，跳过本回合: {e}")
            continue
            
        # --- 轨迹记录缓冲区 (Rollout Buffer) ---
        buffer = {
            'agv_obs': [], 'agv_masks': [], 'agv_actions': [], 'agv_logprobs': [],
            'elev_obs': [], 'elev_actions': [], 'elev_logprobs': [],
            'global_states': [], 'rewards': [], 'values': [], 'dones': []
        }
        
        # LSTM 初始隐藏状态 [num_layers, batch_size, hidden_dim]
        # batch_size 就是 agv 的数量 (9台)
        agv_hidden = (torch.zeros(1, num_agvs, 128).to(device), 
                      torch.zeros(1, num_agvs, 128).to(device))

        ep_reward_sum = 0
        
        # ----------------------------------------------------
        # 阶段 A：与 C# 仿真器交互，采集一条轨迹 (Rollout)
        # ----------------------------------------------------
        for step in range(args.steps_per_episode):
            # 将字典状态转换为张量并放到同构 Batch 中
            agv_obs_tensor = torch.tensor(np.array([obs_dict[n] for n in agv_names]), dtype=torch.float32).to(device)
            agv_mask_tensor = torch.tensor(np.array([mask_dict[n] for n in agv_names]), dtype=torch.bool).to(device)
            global_tensor = torch.tensor(global_state, dtype=torch.float32).to(device)
            
            # --- AGV 动作采样 ---
            with torch.no_grad():
                # 注意：LSTM 要求输入有序列维度，所以 unsqueeze(1) 变成 [9, 1, 214]
                agv_probs, agv_hidden = agv_actor(agv_obs_tensor.unsqueeze(1), agv_hidden, agv_mask_tensor)
                agv_dist = Categorical(agv_probs)
                agv_actions = agv_dist.sample()
                agv_logprobs = agv_dist.log_prob(agv_actions)
                
            # --- 电梯 动作采样 (简单起见，电梯直接看全局状态) ---
            with torch.no_grad():
                elev_obs_tensor = global_tensor.unsqueeze(0).repeat(num_elevs, 1) # [2, 246]
                elev_probs = elev_actor(elev_obs_tensor)
                elev_dist = Categorical(elev_probs)
                elev_actions = elev_dist.sample()
                elev_logprobs = elev_dist.log_prob(elev_actions)

            # --- 全局价值评估 ---
            with torch.no_grad():
                # value 形状为 [1]
                val = global_critic(global_tensor.unsqueeze(0)).squeeze()

            # 将 Tensor 还原为字典，发给 C#
            action_dict_agv = {agv_names[i]: int(agv_actions[i].item()) for i in range(num_agvs)}
            action_dict_elev = {elev_names[i]: int(elev_actions[i].item()) for i in range(num_elevs)}

            # 执行 1 秒物理演进
            next_obs_dict, next_global_state, rewards_dict, next_mask_dict, done = env.step(action_dict_agv, action_dict_elev)
            
            if done is True and next_obs_dict is None:
                print("通信异常，提前结束本回合")
                break

            # 聚合本步奖励 (可根据需要将全局总分分配给每个 Agent，这里采用纯合作共享奖励)
            step_reward = sum(rewards_dict.values())
            ep_reward_sum += step_reward

            # 保存到 Buffer
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

            # 更新状态
            obs_dict, global_state, mask_dict = next_obs_dict, next_global_state, next_mask_dict
            
            if done:
                break

        # ----------------------------------------------------
        # 阶段 B：计算 GAE 并执行 MAPPO 网络参数更新
        # ----------------------------------------------------
        if len(buffer['rewards']) == 0: continue # 防止全空异常

        # 获取最后一个状态的 value 用于 bootstrapped return
        with torch.no_grad():
            last_global_tensor = torch.tensor(global_state, dtype=torch.float32).to(device)
            last_val = global_critic(last_global_tensor.unsqueeze(0)).squeeze()

        # 转换为方便计算的 Tensor 形状 [seq_len, ...]
        b_rewards = torch.tensor(buffer['rewards'], dtype=torch.float32).to(device).unsqueeze(1) # [seq, 1]
        b_values = torch.stack(buffer['values']).unsqueeze(1) # [seq, 1]
        b_dones = torch.tensor(buffer['dones'], dtype=torch.float32).to(device).unsqueeze(1) # [seq, 1]

        # 计算 GAE 优势值
        advantages, returns = compute_gae(b_rewards, b_values, b_dones, last_val, args.gamma, args.gae_lambda)

        # 堆叠历史数据
        b_agv_obs = torch.stack(buffer['agv_obs'])         # [seq, 9, 214]
        b_agv_masks = torch.stack(buffer['agv_masks'])     # [seq, 9, 101]
        b_agv_acts = torch.stack(buffer['agv_actions'])    # [seq, 9]
        b_old_agv_logp = torch.stack(buffer['agv_logprobs']) # [seq, 9]

        b_elev_obs = torch.stack(buffer['elev_obs'])       # [seq, 2, 246]
        b_elev_acts = torch.stack(buffer['elev_actions'])  # [seq, 2]
        b_old_elev_logp = torch.stack(buffer['elev_logprobs']) # [seq, 2]
        
        b_global = torch.stack(buffer['global_states'])    # [seq, 246]

        # --- 1. 更新 Global Critic ---
        new_values = global_critic(b_global).squeeze(1) # [seq]
        critic_loss = torch.nn.functional.mse_loss(new_values, returns.squeeze(1))
        
        opt_critic.zero_grad()
        critic_loss.backward()
        nn_utils.clip_grad_norm_(global_critic.parameters(), args.max_grad_norm)
        opt_critic.step()

        # --- 2. 更新 AGV Actor (带 LSTM 的整序列更新) ---
        # 重置隐藏状态，沿时间轴重新前向传播
        hidden = (torch.zeros(1, num_agvs, 128).to(device), torch.zeros(1, num_agvs, 128).to(device))
        
        # 将 [seq, 9, 214] 维度转置为 [9, seq, 214]，因为 batch_first=True
        agv_seq_obs = b_agv_obs.transpose(0, 1) 
        
        # 前向传播整个序列
        x = torch.nn.functional.relu(agv_actor.fc1(agv_seq_obs))
        x = torch.nn.functional.relu(agv_actor.fc2(x))
        x, _ = agv_actor.lstm(x, hidden) 
        logits = agv_actor.action_out(x) # [9, seq, 101]
        
        # 转回 [seq, 9, 101] 应用 mask
        logits = logits.transpose(0, 1)
        logits = logits.masked_fill(~b_agv_masks, -1e9)
        
        dist_agv = Categorical(logits=logits)
        new_agv_logp = dist_agv.log_prob(b_agv_acts)
        entropy_agv = dist_agv.entropy().mean()

        # PPO 公式
        ratio_agv = torch.exp(new_agv_logp - b_old_agv_logp)
        adv_expanded = advantages.expand_as(ratio_agv) # 共享全局优势值
        
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

        # ----------------------------------------------------
        # 阶段 C：日志打印与模型保存
        # ----------------------------------------------------
        print(f"Ep {episode:04d} | 总奖励: {ep_reward_sum:7.1f} | 损失 [Critic: {critic_loss.item():.2f}, AGV: {actor_loss_agv.item():.2f}, Elev: {actor_loss_elev.item():.2f}]")

        # 每 100 局保存一次权重
        if episode % 100 == 0:
            torch.save(agv_actor.state_dict(), f"{args.save_dir}/agv_actor_ep{episode}.pth")
            torch.save(global_critic.state_dict(), f"{args.save_dir}/global_critic_ep{episode}.pth")
            print(f"✅ 模型已保存至 {args.save_dir}")

if __name__ == "__main__":
    train()