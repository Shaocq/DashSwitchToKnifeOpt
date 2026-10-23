using System;
using System.Collections.Generic;
using System.Reflection;
using ModSetting.Api;
using UnityEngine;

namespace DashSwitchToKnifeOpt
{
    /// <summary>
    /// 与 ModSetting（工坊 3595729494）的集成。
    ///
    /// 采用「引用 ModSetting.dll + SettingsBuilder」的方式（无反射调用），
    /// 但只在确认 AppDomain 已载入 ModSetting 程序集后才执行引用其类型的代码：
    /// 未安装/未启用 ModSetting 时，本 mod 的切刀功能照常独立运行。
    ///
    /// 注意：读取 ModSetting 保存的值依赖它先完成初始化（Saver.Load），
    /// 因此推荐把本 mod 排在 ModSetting 之后；即使排在前面，这里也会每秒重试，
    /// 等 ModSetting 启用并初始化完成后再注册界面。
    /// </summary>
    internal static class ModSettingIntegration
    {
        private const string ModSettingAssemblyName = "ModSetting";

        private static readonly List<string> SlotOptions = new List<string>
        {
            "近战武器",
            "主武器",
            "副武器"
        };

        private static Assembly modSettingAssembly;
        private static bool warnedFailure;

        /// <summary>是否已成功向 ModSetting 注册界面。</summary>
        internal static bool Registered { get; private set; }

        /// <summary>mod 停用时调用，允许重新启用后再次注册。</summary>
        internal static void Reset()
        {
            Registered = false;
            warnedFailure = false;
        }

        /// <summary>尝试注册设置界面；ModSetting 尚未加载/启用时返回 false，由调用方稍后重试。</summary>
        internal static bool TryRegister(ModBehaviour mod)
        {
            if (Registered)
                return true;

            if (mod == null || string.IsNullOrEmpty(mod.info.path))
                return false;

            if (!IsAssemblyLoaded())
                return false;

            return RegisterCore(mod);
        }

        /// <summary>在 AppDomain 中查找已经载入的 ModSetting（不引用其类型，保证未安装时也能安全调用）。</summary>
        private static bool IsAssemblyLoaded()
        {
            if (modSettingAssembly != null)
                return true;

            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (assembly.GetName().Name == ModSettingAssemblyName)
                        {
                            modSettingAssembly = assembly;
                            return true;
                        }
                    }
                    catch
                    {
                        // 个别程序集的 GetName 可能抛异常，跳过即可
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError(ModBehaviour.LogTag + " 查找 ModSetting 失败：" + e);
            }

            return false;
        }

        // 只有确认 ModSetting 已载入后才会调用：本方法引用了 ModSetting 的类型
        private static bool RegisterCore(ModBehaviour mod)
        {
            try
            {
                if (!ModSetting.ModBehaviour.Enable)
                    return false; // ModSetting 已加载但未启用，等待其启用

                SettingsBuilder builder = SettingsBuilder.Create(mod.info);
                if (builder == null)
                    return false;

                // 先读取 ModSetting 里保存的值并应用，保证界面尚未建好时运行时行为也正确
                bool hasSlot = builder.GetSavedValue<string>("slot", out string savedSlot);
                bool hasDisableAttack = builder.GetSavedValue<bool>("disableAttack", out bool savedDisableAttack);
                mod.ApplyModSettingValues(hasSlot, savedSlot, hasDisableAttack, savedDisableAttack);

                string defaultOption = hasSlot
                    ? OptionFromSlot(SlotFromOption(savedSlot))
                    : OptionFromSlot(mod.FileSlot);
                bool defaultDisableAttack = hasDisableAttack ? savedDisableAttack : mod.FileDisableAttack;

                builder.AddDropdownList(
                    "slot",
                    "奔跑时切换到的装备槽",
                    SlotOptions,
                    defaultOption,
                    option => mod.OnModSettingSlotChanged(option));

                builder.AddToggle(
                    "disableAttack",
                    "奔跑中禁止近战攻击（需要 HarmonyLib 前置）",
                    defaultDisableAttack,
                    value => mod.OnModSettingDisableAttackChanged(value));

                Registered = true;
                Debug.Log(ModBehaviour.LogTag + " ModSetting 设置界面已注册");
                return true;
            }
            catch (Exception e)
            {
                // 版本不兼容等问题：只警告一次，避免每秒刷屏
                if (!warnedFailure)
                {
                    warnedFailure = true;
                    Debug.LogWarning(ModBehaviour.LogTag + " 注册 ModSetting 设置界面失败（将在后台重试）：" + e);
                }
                return false;
            }
        }

        /// <summary>UI 选项 → 装备槽名（同时兼容旧存档里的英文槽位名）。</summary>
        internal static string SlotFromOption(string option)
        {
            string value = (option ?? string.Empty).Trim();

            if (value.Equals("主武器", StringComparison.Ordinal) ||
                value.Equals("PrimaryWeapon", StringComparison.OrdinalIgnoreCase))
                return "PrimaryWeapon";

            if (value.Equals("副武器", StringComparison.Ordinal) ||
                value.Equals("SecondaryWeapon", StringComparison.OrdinalIgnoreCase))
                return "SecondaryWeapon";

            return "MeleeWeapon";
        }

        /// <summary>装备槽名 → UI 选项。</summary>
        internal static string OptionFromSlot(string slot)
        {
            if (slot == "PrimaryWeapon")
                return "主武器";
            if (slot == "SecondaryWeapon")
                return "副武器";
            return "近战武器";
        }
    }
}
