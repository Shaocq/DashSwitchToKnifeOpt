# 更新日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/) 与 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 格式。

## [1.0.0] - 2026-10-23

首次开源发布。当前版本包含的全部功能：

### 新增

- **奔跑自动切刀**：进入奔跑状态时自动切换到指定装备槽的武器
- **停止恢复**：停止奔跑后恢复奔跑前手持的物品，不打断原有操作
- **瞄准 / 开火保护**：奔跑中按下瞄准或开火立即切回原枪并中断奔跑，避免冲向敌人时挥刀
- **近战限制**：可选「奔跑中禁止近战攻击」
- **配置**：支持 ModSetting 设置界面，也可直接编辑 `config.txt`（参考 `config.example.txt`）

### 变更

- 程序集名从上游的 `DashSwitchToKnife` 改为 `DashSwitchToKnifeOpt`，可与原版 mod 区分加载
- 补丁拆分为 `HarmonyPatches.cs` / `ModSettingIntegration.cs`，无前置时自动降级为只切刀

### 说明

- 完整功能依赖前置 HarmonyLib（工坊 `3589088839`）；未安装时切刀本体仍可用
- 基于 [kalonocat/DashSwitchToKnife](https://github.com/kalonocat/DashSwitchToKnife) 修改
