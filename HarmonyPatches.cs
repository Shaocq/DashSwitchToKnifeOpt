using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DashSwitchToKnifeOpt
{
    /// <summary>
    /// 奔跑中禁止近战攻击。
    ///
    /// 只拦截主角色（CharacterMainControl.Main）的 CA_Attack.IsReady：
    /// 返回 false 后，CharacterMainControl.Attack() 会在 StartAction 之前直接返回，
    /// 不会出刀，也不会打断奔跑。
    ///
    /// 相比原版直接反射修改 CA_Attack 的私有字段 cd，这里不依赖任何私有字段名，
    /// 游戏更新后只要 IsReady 还在就依然有效。
    /// </summary>
    [HarmonyPatch(typeof(CA_Attack), nameof(CA_Attack.IsReady))]
    internal static class AttackIsReadyPatch
    {
        private static bool Prefix(CA_Attack __instance)
        {
            try
            {
                ModBehaviour mod = ModBehaviour.Instance;
                return mod == null || !mod.ShouldSuppressAttack(__instance);
            }
            catch
            {
                // 补丁出问题绝不能把异常抛回游戏，放行原逻辑即可
                return true;
            }
        }
    }

    /// <summary>
    /// 奔跑中按下瞄准（右键）时切回奔跑前的枪械。
    ///
    /// 不能挂 CharacterMainControl.SetAdsInput：实测它只有 2 条 IL（字段赋值），
    /// Mono JIT 会在调用点把它内联，Harmony 前缀根本不执行（右键完全没反应）。
    /// 这里挂输入回调 CharacterInputControl.OnPlayerAdsInput：它由 InputAction
    /// 的委托调用，调用点无法内联，一定会经过本方法。同时用
    /// CharacterInputControl.Instance 过滤掉联机时其他玩家的输入。
    /// </summary>
    [HarmonyPatch(typeof(CharacterInputControl), nameof(CharacterInputControl.OnPlayerAdsInput))]
    internal static class AdsInputPatch
    {
        private static void Prefix(CharacterInputControl __instance, InputAction.CallbackContext context)
        {
            try
            {
                if (!ReferenceEquals(__instance, CharacterInputControl.Instance))
                    return; // 不是本地玩家（联机时其他角色也有输入控件）

                ModBehaviour mod = ModBehaviour.Instance;
                if (mod == null)
                    return;

                // 与游戏自身的判断保持一致：started = 按下，canceled = 松开，
                // performed 是同一帧的重复通知，忽略。
                if (context.started)
                    mod.HandleAdsInput(InputManager.InputActived);
                else if (context.canceled)
                    mod.HandleAdsInput(false);
            }
            catch
            {
                // 补丁出问题绝不能把异常抛回游戏
            }
        }
    }

    /// <summary>
    /// 玩家重新按下奔跑键时结束战斗休眠。
    ///
    /// 同样挂输入回调而不是 CharacterMainControl.SetRunInput（后者只有 2 条 IL，
    /// 会被内联，导致补丁不执行、休眠永远无法解除——这正是「开火后再跑不切刀」的原因）。
    /// </summary>
    [HarmonyPatch(typeof(CharacterInputControl), nameof(CharacterInputControl.OnPlayerRunInput))]
    internal static class RunInputPatch
    {
        private static void Prefix(CharacterInputControl __instance, InputAction.CallbackContext context)
        {
            try
            {
                if (!ReferenceEquals(__instance, CharacterInputControl.Instance))
                    return;

                if (context.started)
                {
                    ModBehaviour mod = ModBehaviour.Instance;
                    if (mod != null)
                        mod.HandleRunStarted();
                }
            }
            catch
            {
                // 补丁出问题绝不能把异常抛回游戏
            }
        }
    }

    /// <summary>
    /// 奔跑中按下开火（左键）时切回奔跑前的枪械。
    ///
    /// 前缀在 CharacterMainControl.Trigger 之前执行，切枪成功后本帧的 Trigger
    /// 就会直接转发给枪械；紧接着 InputManager 调用的 Attack() 因为手上没有
    /// 近战武器而不会挥刀。
    /// </summary>
    [HarmonyPatch(typeof(CharacterMainControl), nameof(CharacterMainControl.Trigger))]
    internal static class TriggerInputPatch
    {
        private static void Prefix(CharacterMainControl __instance, bool trigger)
        {
            try
            {
                ModBehaviour mod = ModBehaviour.Instance;
                if (mod != null)
                    mod.HandleTriggerInput(__instance, trigger);
            }
            catch
            {
                // 补丁出问题绝不能把异常抛回游戏
            }
        }
    }

    /// <summary>
    /// 战斗输入接管期间，让主角色 CanRun() 返回 false。
    /// Movement.UpdateMovement 每帧都会读取 CanRun()，这样角色会真正从奔跑切到行走，
    /// 松开瞄准/开火后 InputManager 会重新把 runInput 设为按住状态，奔跑自然恢复。
    /// </summary>
    [HarmonyPatch(typeof(CharacterMainControl), nameof(CharacterMainControl.CanRun))]
    internal static class CanRunPatch
    {
        private static bool Prefix(CharacterMainControl __instance, ref bool __result)
        {
            try
            {
                ModBehaviour mod = ModBehaviour.Instance;
                if (mod != null && mod.ShouldForceStopRunning(__instance))
                {
                    __result = false;
                    return false;
                }
            }
            catch
            {
                // 补丁出问题绝不能把异常抛回游戏，放行原逻辑即可
            }
            return true;
        }
    }

    /// <summary>
    /// 切枪后补发开火输入。
    ///
    /// 新装备的武器有约 0.15 秒的抬枪时间，这期间 GameObject 未激活、状态机不更新，
    /// 玩家按下的那一帧 triggerThisFrame 会被后续每帧的 SetTrigger 覆盖掉。
    /// 这里在 UpdateStates 真正消费开火标志之前补发一次，单发武器也能立即出膛。
    /// </summary>
    [HarmonyPatch(typeof(ItemAgent_Gun), nameof(ItemAgent_Gun.UpdateStates))]
    internal static class GunUpdateStatesPatch
    {
        private static void Prefix(ItemAgent_Gun __instance)
        {
            try
            {
                ModBehaviour mod = ModBehaviour.Instance;
                if (mod != null && mod.ShouldReplayTrigger(__instance))
                    __instance.SetTrigger(true, true, false);
            }
            catch
            {
                // 补丁出问题绝不能把异常抛回游戏
            }
        }
    }

    /// <summary>
    /// 延迟查找前置「HarmonyLib」（工坊 3589088839 / HarmonyLoadMod）载入的 0Harmony，
    /// 找到后再安装补丁。本 mod 不内嵌 0Harmony。
    /// </summary>
    internal static class HarmonyBootstrap
    {
        private const string HarmonyId = "com.dashswitchtoknifeopt";

        private static Assembly harmonyAssembly;
        private static bool loggedApplyFailure;

        internal static string Version { get; private set; } = "未加载";
        internal static bool Applied { get; private set; }

        /// <summary>在 AppDomain 中查找已经载入的 0Harmony（只接受 2.x）。</summary>
        internal static Assembly Load0Harmony()
        {
            if (harmonyAssembly != null)
                return harmonyAssembly;

            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (assembly.GetName().Name != "0Harmony")
                            continue;

                        Version version = assembly.GetName().Version;
                        if (version != null && version.Major >= 2)
                        {
                            harmonyAssembly = assembly;
                            Version = version.ToString();
                            return harmonyAssembly;
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
                Debug.LogError(ModBehaviour.LogTag + " 查找 0Harmony 失败：" + e);
            }

            return null;
        }

        /// <summary>确认 0Harmony 已加载后再安装补丁；未加载时返回 false，由调用方重试。</summary>
        internal static bool Apply()
        {
            if (Applied)
                return true;

            if (Load0Harmony() == null)
                return false;

            return ApplyCore();
        }

        // 只有确认 0Harmony 已加载后才会调用：本方法体引用了 HarmonyLib 的类型
        private static bool ApplyCore()
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                harmony.UnpatchAll(HarmonyId); // 防止重复安装
                harmony.PatchAll(typeof(ModBehaviour).Assembly);
                Applied = true;
                Debug.Log(ModBehaviour.LogTag + " Harmony 补丁已应用（Harmony " + Version + "）");
                ModBehaviour.LogToFile("Harmony 补丁已应用（Harmony " + Version + "）");
                return true;
            }
            catch (Exception e)
            {
                // 每秒重试一次；只记录第一次的完整异常，避免游戏更新导致目标方法改名时刷屏
                if (!loggedApplyFailure)
                {
                    loggedApplyFailure = true;
                    Debug.LogError(ModBehaviour.LogTag + " 应用 Harmony 补丁失败：" + e);
                    ModBehaviour.LogToFile("应用 Harmony 补丁失败：" + e.Message);
                }
                return false;
            }
        }

        internal static void Unapply()
        {
            if (!Applied)
                return;

            UnapplyCore();
        }

        // 仅当 Applied 为 true（即 0Harmony 已加载）时才会调用
        private static void UnapplyCore()
        {
            try
            {
                new Harmony(HarmonyId).UnpatchAll(HarmonyId);
            }
            catch (Exception e)
            {
                Debug.LogError(ModBehaviour.LogTag + " 移除 Harmony 补丁失败：" + e);
            }

            Applied = false;
        }
    }
}
