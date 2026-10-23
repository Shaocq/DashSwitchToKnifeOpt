# 奔跑自动切刀-优化版（DashSwitchToKnifeOpt）

[![游戏](https://img.shields.io/badge/游戏-Escape%20from%20Duckov-blue)](https://store.steampowered.com/app/3167020/)
[![协议](https://img.shields.io/badge/协议-MIT-yellow)](LICENSE)
[![版本](https://img.shields.io/badge/版本-1.0.0-brightgreen)](CHANGELOG.md)

> 奔跑时自动切换到指定装备槽的武器，停止奔跑后恢复原手持物品；奔跑中按下瞄准或开火会自动切回原枪并中断奔跑，避免对敌挥刀。

| 项目 | 信息 |
| --- | --- |
| 游戏 | 逃离鸭科夫（Escape from Duckov，AppID `3167020`） |
| 框架 | `Duckov.Modding.ModBehaviour` |
| 前置 | HarmonyLib（工坊 `3589088839`）—— **完整功能需要**；未安装时切刀功能仍可正常使用 |
| 当前版本 | 1.0.0 |
| 开源协议 | [MIT](LICENSE) |

## 功能特性

| 功能 | 说明 |
| --- | --- |
| 奔跑自动切刀 | 进入奔跑状态时，自动切换到指定装备槽里的武器 |
| 停止自动恢复 | 停止奔跑后，恢复奔跑前手持的物品，不打断原有操作 |
| 瞄准 / 开火保护 | 奔跑中按下瞄准或开火，立即切回原枪并中断奔跑，避免冲向敌人时挥刀 |
| 近战限制 | 可选开启「奔跑中禁止近战攻击」 |
| 配置方式 | 支持 ModSetting 设置界面；也可直接编辑 mod 目录下的 `config.txt`（参考 `config.example.txt`） |

## 安装

1. 订阅前置 **HarmonyLib**（工坊 `3589088839`），并确保它排在本 mod **之前**加载；
2. 把 `DashSwitchToKnifeOpt` 文件夹放入 `Escape from Duckov\Duckov_Data\Mods\`；
3. 可选安装 ModSetting（工坊 `3595729494`）以便在游戏内调整设置。

> 未安装 HarmonyLib 时，切刀本体仍可工作，但「瞄准 / 开火保护」与「奔跑中禁止近战」等增强功能不可用。

## 配置

首次运行会在 mod 目录生成或读取 `config.txt`，字段说明见仓库内的 `config.example.txt`。安装 ModSetting 后可直接在设置页里改。

## 从源码构建

```bash
dotnet build DashSwitchToKnifeOpt.csproj -c Release
```

游戏不在默认路径时加 `-p:DuckovPath="你的游戏目录"`。

> `lib/0Harmony.dll` 不在仓库内。需要编译完整功能时，请从创意工坊前置 HarmonyLib 目录复制一份到 `lib/`。

## 与原版 / 上游的关系

本项目基于 [kalonocat/DashSwitchToKnife](https://github.com/kalonocat/DashSwitchToKnife) 修改，原项目未附带开源协议。本仓库仅以 MIT 协议开放**本项目作者新增与修改的部分**，原作者代码的版权仍归原作者所有。

## 更新日志

完整版本记录见 [CHANGELOG.md](CHANGELOG.md)。

## 许可证

本项目以 [MIT 协议](LICENSE) 开源，可自由使用、修改与再发布，保留版权声明即可。

## 致谢

- 游戏与 mod 框架：TeamSoda《逃离鸭科夫》
- 原始 mod 作者：[kalonocat](https://github.com/kalonocat)
- Harmony 补丁框架：[Harmony](https://github.com/pardeike/Harmony)（MIT）
