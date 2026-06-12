import torch
import torch.nn as nn
import torch.nn.functional as F

# ==========================================
# 1. AGV 策略网络 (Actor) - 带 LSTM 与 动作掩码
# ==========================================
class AgvActor(nn.Module):
    def __init__(self, obs_dim=214, hidden_dim=128, action_dim=101):
        super(AgvActor, self).__init__()
        self.fc1 = nn.Linear(obs_dim, hidden_dim)
        self.fc2 = nn.Linear(hidden_dim, hidden_dim)
        
        # LSTM 记忆层：解决流水线中的长延迟和部分可观测问题
        self.lstm = nn.LSTM(hidden_dim, hidden_dim, batch_first=True)
        
        self.action_out = nn.Linear(hidden_dim, action_dim)

    def forward(self, obs, hidden_state, action_mask):
        """
        obs: [batch_size, seq_len, obs_dim]
        hidden_state: tuple(h_0, c_0)
        action_mask: [batch_size, action_dim] 布尔张量 (True代表合法)
        """
        x = F.relu(self.fc1(obs))
        x = F.relu(self.fc2(x))
        
        # 通过 LSTM
        x, new_hidden = self.lstm(x, hidden_state)
        
        # 取 LSTM 序列的最后一步输出计算动作
        x_last = x[:, -1, :] 
        logits = self.action_out(x_last)
        
        # ★ 严苛动作掩码：将非法动作的 Logits 降至极小值
        logits = logits.masked_fill(~action_mask, -1e9)
        
        # 返回动作概率分布和新的隐藏状态
        action_probs = F.softmax(logits, dim=-1)
        return action_probs, new_hidden

# ==========================================
# 2. 电梯 策略网络 (Actor) - 简单 MLP
# ==========================================
class ElevatorActor(nn.Module):
    def __init__(self, obs_dim=246, hidden_dim=64, action_dim=4):
        # 为了简单，电梯可以直接看全局状态 (246维) 来决定去哪层
        super(ElevatorActor, self).__init__()
        self.fc1 = nn.Linear(obs_dim, hidden_dim)
        self.fc2 = nn.Linear(hidden_dim, hidden_dim)
        self.action_out = nn.Linear(hidden_dim, action_dim)

    def forward(self, obs):
        x = F.relu(self.fc1(obs))
        x = F.relu(self.fc2(x))
        logits = self.action_out(x)
        return F.softmax(logits, dim=-1)

# ==========================================
# 3. 集中式全局价值网络 (Critic) - 带 Self-Attention
# ==========================================
class GlobalCritic(nn.Module):
    def __init__(self, global_obs_dim=246, hidden_dim=128, num_heads=4):
        super(GlobalCritic, self).__init__()
        
        # 先将 246 维特征映射到高维空间
        self.feature_proj = nn.Linear(global_obs_dim, hidden_dim)
        
        # 自注意力层：让上帝大脑找出全厂的“瓶颈”
        self.attention = nn.MultiheadAttention(embed_dim=hidden_dim, num_heads=num_heads, batch_first=True)
        
        self.fc1 = nn.Linear(hidden_dim, hidden_dim)
        self.value_out = nn.Linear(hidden_dim, 1)

    def forward(self, global_state):
        """
        global_state: [batch_size, global_obs_dim]
        """
        x = F.relu(self.feature_proj(global_state))
        
        # 为了使用 MultiheadAttention，需要伪造一个序列维度 [batch, seq=1, hidden]
        x = x.unsqueeze(1)
        
        # Attention 机制 (Query=x, Key=x, Value=x)
        attn_out, _ = self.attention(x, x, x)
        
        # 降维回 [batch, hidden]
        attn_out = attn_out.squeeze(1)
        
        x = F.relu(self.fc1(attn_out))
        state_value = self.value_out(x)
        return state_value

# ==========================================
# 4. GAE (广义优势估计) 计算工具
# ==========================================
def compute_gae(rewards, values, dones, next_value, gamma=0.99, lam=0.95):
    """
    计算强化学习更新所需的优势值 (Advantage) 和 目标价值 (Returns)
    """
    seq_len = rewards.shape[0]
    advantages = torch.zeros_like(rewards)
    last_gae_lam = 0
    
    for t in reversed(range(seq_len)):
        if t == seq_len - 1:
            next_non_terminal = 1.0 - dones[t]
            next_val = next_value
        else:
            next_non_terminal = 1.0 - dones[t]
            next_val = values[t + 1]
            
        delta = rewards[t] + gamma * next_val * next_non_terminal - values[t]
        last_gae_lam = delta + gamma * lam * next_non_terminal * last_gae_lam
        advantages[t] = last_gae_lam
        
    returns = advantages + values
    
    # 优势值标准化，有助于训练稳定
    adv_mean = advantages.mean()
    adv_std = advantages.std() + 1e-8
    normalized_advantages = (advantages - adv_mean) / adv_std
    
    return normalized_advantages, returns