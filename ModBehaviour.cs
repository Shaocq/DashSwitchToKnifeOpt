using System;
using System.Collections;
using System.IO;
using ItemStatsSystem;
using ItemStatsSystem.Items;
using UnityEngine;

namespace DashSwitchToKnifeOpt
{
    /// <summary>
    /// 奔跑自动切刀（优化版）。
    ///
    /// 行为：
    ///   - 开始奔跑时，把当前手持物品换成指定装备槽（默认 MeleeWeapon）里的武器；
    ///   - 停止奔跑且双手空闲时，恢复奔跑前的手持物品；
    ///   - 奔跑中按下瞄准 / 开火时，立即切回奔跑前的枪械并中断奔跑，
    ///     避免对着敌人按左键却挥刀（见 HandleAdsInput / HandleTriggerInput）；
    ///   - 可选：奔跑过程中禁止近战攻击（由 Harmony 前缀实现，见 HarmonyPatches.cs）。
    ///
    /// 相对原版的修复：
    ///   - 所有 CurrentHoldItemAgent / GetSlot 访问都做了空引用保护；
    ///   - 还原后清空保存的物品，避免状态残留导致每帧空引用；
    ///   - 停用 mod 时恢复手持物品，不会把玩家卡在拿刀状态；
    ///   - 不再反射修改 CA_Attack 的私有字段 cd，改为 Harmony 前缀拦截 IsReady；
    ///   - 配置忽略大小写与首尾空白，支持热重载，并优先从 mod 目录读取。
    /// </summary>
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        internal const string LogTag = "[奔跑自动切刀-优化版]";

        private const float ConfigCheckInterval = 1f;   // 配置热重载检查间隔（秒）
        private const float RestoreTimeout = 2f;        // 停跑后还原手持物品的最长重试时间（秒）
        private const float ManualChangeGrace = 0.35f;  // 判定「玩家主动换物品」的持续时间（秒）
        private const float PlayerLookupInterval = 0.25f; // 主角色缓存失效后重新查找的间隔（秒）
        private const float CombatDrawWindow = 0.4f;     // 切回枪械后抬枪时间内按下的开火需要补发（秒）
        private const float PendingTriggerTimeout = 0.6f; // 待补发开火的有效期，超时作废（秒）
        private const float RearmIdleTime = 0.2f;        // 战斗后连续停跑多久算一次新奔跑（秒）

        /// <summary>当前生效的 mod 实例，供 Harmony 前缀查询。</summary>
        internal static ModBehaviour Instance { get; private set; }

        private bool modEnabled;
        private bool disableAttack;
        private int weaponSlotHash = "MeleeWeapon".GetHashCode(); // 每帧取槽位时直接复用，避免重复计算字符串哈希

        // ---- 配置来源 ----
        // config.txt / 旧版 StreamingAssets 配置
        private string fileSlot = "MeleeWeapon";
        private bool fileDisableAttack;

        // ModSetting 界面保存/修改的值：存在时优先于文件配置
        private bool modSettingSlotLoaded;
        private string modSettingSlot = "MeleeWeapon";
        private bool modSettingDisableAttackLoaded;
        private bool modSettingDisableAttack;

        // ---- 奔跑会话状态 ----
        private bool inRun;             // 是否处于一次已处理的奔跑中
        private bool switchedThisRun;   // 本次奔跑是否由本 mod 切换过手持物品
        private Item savedItem;         // 奔跑开始前的手持物品（空手时为 null）
        private bool savedWasGun;       // 奔跑前手持的是否为枪械（决定瞄准/开火时是否值得切回）
        private float restoreTimer;     // 停跑后还原的重试计时
        private float itemChangedTimer; // 检测玩家是否主动换了别的物品

        // ---- 战斗输入（瞄准 / 开火）----
        // 奔跑中按瞄准或开火时切回原枪，并在按住期间强制 CanRun 返回 false，
        // 让 Movement 真正进入行走状态（瞄准和开火都要求未处于奔跑）。
        private bool adsHeld;           // 是否按住瞄准
        private bool triggerHeld;       // 是否按住开火
        private bool combatOverride;    // 战斗输入接管中：强制停止奔跑
        private bool pendingTrigger;    // 切枪后待补发的开火输入（单发武器需要）
        private Item combatGun;         // 战斗切回后正在抬枪的枪械
        private float combatSwitchTime; // 战斗切回枪械的时刻（unscaledTime）
        private float pendingTriggerTime; // pendingTrigger 的产生时刻（unscaledTime）

        // ---- 战斗之后的休眠 ----
        // 战斗输入切回枪并停跑后，本 mod 进入休眠，直到玩家真正停下来（连续一段时间
        // 未奔跑且没有按住瞄准/开火）后，下一次起跑才重新判定切刀。
        // 否则玩家一直按着 Shift 时，一松开鼠标就会立刻又被切回近战武器。
        private bool suppressUntilNewRun;
        private float rearmTimer;       // 休眠期间「连续未奔跑」的计时

        // ---- 主角色缓存 ----
        // CharacterMainControl.Main 在 LevelManager 不存在时会调用 FindFirstObjectByType
        // 全场景扫描（主菜单 / 读图期间），因此缓存引用，失效后按间隔节流重新查找。
        private CharacterMainControl cachedPlayer;
        private float nextPlayerLookup;

        // ---- 配置热重载 ----
        private bool configInitialized;
        private bool usingNewConfig;
        private string configPath;
        private DateTime configWriteTime;
        private float nextConfigCheck;

        // ---- Harmony / ModSetting ----
        private Coroutine harmonyRetry;
        private Coroutine modSettingRetry;
        private float lastErrorLogTime;

        // ---- 诊断日志文件 ----
        // 游戏运行日志会被其他 mod 的日志 handler 接管，本 mod 的 Debug.Log 不一定
        // 出现在 Player.log 里；因此关键状态变化另外写一份到 mod 目录，便于排查。
        private static string logFilePath;
        private static bool logFileUnavailable;
        private static readonly object logFileLock = new object();

        public void Awake()
        {
            Debug.Log(LogTag + " 已加载");
        }

        public void OnDestroy()
        {
            Debug.Log(LogTag + " 已卸载");
        }

        public void OnEnable()
        {
            try
            {
                Instance = this;
                modEnabled = true;

                // 首次加载时 Setup() 还没调用，info.path 为空，此时由 OnAfterSetup 初始化；
                // 组件被重新启用（非首次加载）时在这里重新加载配置并安装补丁。
                if (!string.IsNullOrEmpty(info.path))
                {
                    ReloadConfig(force: true);
                    TryApplyHarmony();
                    TryRegisterModSetting();
                }
            }
            catch (Exception e)
            {
                LogException("OnEnable 失败", e);
            }
        }

        /// <summary>
        /// Setup() 在 AddComponent 之后调用，此时 info（mod 目录）才可用，
        /// 因此配置读取和 Harmony 安装放在这里，而不是 OnEnable。
        /// </summary>
        protected override void OnAfterSetup()
        {
            try
            {
                ReloadConfig(force: true);
                LogToFile("初始化完成");
                TryApplyHarmony();
                TryRegisterModSetting();
            }
            catch (Exception e)
            {
                LogException("初始化失败", e);
            }
        }

        public void OnDisable()
        {
            try
            {
                modEnabled = false;

                if (harmonyRetry != null)
                {
                    StopCoroutine(harmonyRetry);
                    harmonyRetry = null;
                }

                if (modSettingRetry != null)
                {
                    StopCoroutine(modSettingRetry);
                    modSettingRetry = null;
                }

                // 战斗输入接管必须随 mod 一起解除，否则会一直把主角色按在行走状态
                ResetInputState();

                // 允许 mod 重新启用后再次注册界面（ModSetting 会在 mod 停用时自行移除旧 UI）
                ModSettingIntegration.Reset();

                // 必须在调用前检查 Applied：0Harmony 未加载时，Unapply 的方法体
                // 会因解析 HarmonyLib 类型而抛异常。
                if (HarmonyBootstrap.Applied)
                    HarmonyBootstrap.Unapply();

                RestoreOnShutdown();
            }
            catch (Exception e)
            {
                LogException("OnDisable 失败", e);
            }
            finally
            {
                Instance = null;
            }
        }

        public void Update()
        {
            if (!modEnabled)
                return;

            try
            {
                ReloadConfigIfNeeded();

                // 打开 UI / 暂停 / 死亡时输入回调可能收不到「松开」事件，
                // 主动清掉按住状态，避免 combatOverride 残留把角色一直按在行走状态。
                if ((adsHeld || triggerHeld || combatOverride) && !InputManager.InputActived)
                {
                    adsHeld = false;
                    triggerHeld = false;
                    combatOverride = false;
                }

                UpdateRunningState();
            }
            catch (Exception e)
            {
                LogException("Update 失败", e);
            }
        }

        /// <summary>Harmony 前缀调用：是否要拦下本次近战攻击。</summary>
        internal bool ShouldSuppressAttack(CA_Attack action)
        {
            try
            {
                if (!modEnabled || !disableAttack)
                    return false;

                CharacterMainControl main = GetPlayer();
                if (main == null || action == null || action != main.attackAction)
                    return false;

                // 奔跑中禁止近战攻击：不论手上拿的是不是本 mod 切换的武器都生效
                return main.Running;
            }
            catch
            {
                return false;
            }
        }

        // ==================== 战斗输入（瞄准 / 开火） ====================

        /// <summary>
        /// Harmony 调用：战斗输入接管期间，主角色的 CanRun() 是否应返回 false。
        /// 这里只做缓存引用比较，Movement 每帧调用一次，开销可以忽略。
        /// </summary>
        internal bool ShouldForceStopRunning(CharacterMainControl character)
        {
            if (!combatOverride || character == null || cachedPlayer == null)
                return false;

            return ReferenceEquals(character, cachedPlayer);
        }

        /// <summary>Harmony 前缀调用：瞄准输入变化（按住 / 松开）。</summary>
        internal void HandleAdsInput(bool ads)
        {
            try
            {
                if (!modEnabled)
                    return;

                // started / performed 可能在同一帧各回调一次，同一次变化只处理一次
                if (adsHeld == ads)
                    return;

                adsHeld = ads;
                LogToFile(ads ? "瞄准按下" : "瞄准松开");

                if (ads)
                {
                    // 奔跑中由本 mod 换成近战武器时，切回原枪并中断奔跑，准星才能正常开镜
                    if (TrySwitchBackForCombat(replayTrigger: false))
                        combatOverride = true;
                    else if (ShouldInterruptRunForCombat(cachedPlayer))
                        combatOverride = true; // 休眠期按瞄准：先停跑，IsInAdsInput 才会生效
                }

                RefreshCombatOverride();
            }
            catch (Exception e)
            {
                LogException("处理瞄准输入失败", e);
            }
        }

        /// <summary>Harmony 调用：玩家重新按下奔跑键（started）。</summary>
        internal void HandleRunStarted()
        {
            try
            {
                if (!modEnabled)
                    return;

                // 重新按下奔跑键 = 新一轮奔跑意图：立即结束上一轮战斗留下的休眠。
                // 若此刻仍按着瞄准 / 开火，BeginRun 会跳过本次切刀，松手后再正常判定。
                if (suppressUntilNewRun)
                {
                    suppressUntilNewRun = false;
                    rearmTimer = 0f;
                    LogToFile("重新按下奔跑键，战斗休眠解除");
                }
            }
            catch (Exception e)
            {
                LogException("处理奔跑输入失败", e);
            }
        }

        /// <summary>
        /// Harmony 前缀调用：开火输入（每帧都会携带当前按住状态）。
        /// 在游戏处理 Trigger 之前切回原枪，本帧的触发器就会直接交给枪械；
        /// 随后 InputManager 调用的 Attack() 因为手上没有近战武器而不会挥刀。
        /// </summary>
        internal void HandleTriggerInput(CharacterMainControl character, bool trigger)
        {
            try
            {
                if (!modEnabled || !IsMainCharacter(character))
                    return;

                // SetTrigger 每帧都会调用，只有「未按住 → 按住」才算一次新的射击意图
                bool pressed = trigger && !triggerHeld;
                triggerHeld = trigger;

                if (pressed)
                {
                    LogToFile("开火按下");
                    if (TrySwitchBackForCombat(replayTrigger: true))
                    {
                        combatOverride = true;
                    }
                    else if (IsWaitingForCombatGun(character))
                    {
                        // 已经切回枪、但枪还在约 0.15 秒的抬枪阶段（GameObject 未激活，
                        // 无法消费 triggerThisFrame）：先记下这次点击，抬枪结束后补发一枪。
                        // 即使玩家很快松开也要补发——按下开火本身就是一次明确的射击意图。
                        MarkPendingTrigger();
                    }
                    else if (ShouldInterruptRunForCombat(character))
                    {
                        // 休眠期里玩家仍按着 Shift 自动奔跑：CharacterMainControl.Trigger
                        // 在 Running 时会把 trigger/triggerThisFrame 直接清成 false，
                        // 这一枪会被静默吞掉。先强制停跑，并记下这次点击，等角色真正
                        // 停下后由枪械状态机补发（单发武器也不会丢枪）。
                        combatOverride = true;
                        MarkPendingTrigger();
                        LogToFile("休眠期开火：强制停跑并等待补发");
                    }
                }

                RefreshCombatOverride();
            }
            catch (Exception e)
            {
                LogException("处理开火输入失败", e);
            }
        }

        private bool IsMainCharacter(CharacterMainControl character)
        {
            return character != null && cachedPlayer != null && ReferenceEquals(character, cachedPlayer);
        }

        /// <summary>
        /// 战斗切回枪之后的休眠期里，玩家很可能一直按着奔跑键，角色会自动恢复奔跑。
        /// 而游戏在奔跑时会把战斗输入整个丢掉：
        ///   - CharacterMainControl.Trigger 在 Running 时把 trigger/triggerThisFrame 清成 false；
        ///   - IsInAdsInput 在 Running 时恒为 false。
        /// 所以每次按下瞄准/开火都要重新强制停跑，否则「点一下之后就再也开不了枪」。
        /// 只在休眠期且手里有枪时接管，普通奔跑保持原版行为。
        /// </summary>
        private bool ShouldInterruptRunForCombat(CharacterMainControl player)
        {
            if (!modEnabled || !suppressUntilNewRun || player == null || !player.Running)
                return false;

            return player.GetGun() != null;
        }

        private void RefreshCombatOverride()
        {
            if (!adsHeld && !triggerHeld)
                combatOverride = false;
        }

        /// <summary>
        /// 瞄准 / 开火时切回奔跑前手持的枪械。
        /// 只有「本次奔跑由本 mod 换成近战武器、且玩家此刻手里仍是那把武器」时才介入，
        /// 玩家手动换过装的情况下保持原样，不抢玩家的操作。
        /// </summary>
        private bool TrySwitchBackForCombat(bool replayTrigger)
        {
            if (!modEnabled || !inRun || !switchedThisRun || !savedWasGun || savedItem == null)
                return false;

            CharacterMainControl player = cachedPlayer;
            if (player == null)
                return false;

            // 原枪械已经被移出装备槽（存进容器 / 丢弃），没有可切回的东西
            if (savedItem.PluggedIntoSlot == null)
            {
                ResetRunState();
                return false;
            }

            // 手持的已经不是本 mod 切出来的那把武器：视为玩家主动换装，不干预
            Item target = GetSlotContent(player, weaponSlotHash);
            Item held = GetHeldItem(player);
            if (held == null || held != target)
                return false;

            // ResetRunState 会清空 savedItem，先把切回的枪记下来
            Item gunToDraw = savedItem;
            if (!player.ChangeHoldItem(gunToDraw))
                return false;

            Debug.Log(LogTag + " 按下瞄准/开火，已切回奔跑前的枪械");
            LogToFile("战斗输入：已切回奔跑前的枪械（TypeID=" + gunToDraw.TypeID + "）");

            combatGun = gunToDraw;
            combatSwitchTime = Time.unscaledTime;
            ResetRunState();

            // 本次奔跑已被战斗打断：进入休眠，等玩家真正停下来再重新起跑判定切刀
            suppressUntilNewRun = true;
            rearmTimer = 0f;

            // 换手后新武器有约 0.15 秒的抬枪时间（ItemAgentHolder.holdStadyTime），
            // 这期间按下的开火会丢掉 triggerThisFrame；记下来，等枪真正开始
            // 更新状态时由 GunUpdateStatesPatch 补发一次。
            if (replayTrigger)
                MarkPendingTrigger();

            return true;
        }

        /// <summary>
        /// 当前手持的枪是否正处于「刚切回来、还在抬枪」的阶段。
        /// 抬枪期间 ItemAgent 的 GameObject 未激活，Update 不执行，
        /// 游戏这一帧送来的 triggerThisFrame 会被之后的输入覆盖掉，因此需要补发。
        /// </summary>
        private bool IsWaitingForCombatGun(CharacterMainControl player)
        {
            if (player == null || combatGun == null)
                return false;

            // 抬枪时间 0.15 秒，留一倍余量；超时说明枪早已可用，按原版逻辑处理即可
            if (Time.unscaledTime - combatSwitchTime > CombatDrawWindow)
                return false;

            ItemAgent_Gun gun = player.GetGun();
            if (gun == null || gun.Item != combatGun)
                return false;

            return !gun.gameObject.activeInHierarchy;
        }

        /// <summary>记录一次待补发的开火，带有效期，避免枪械迟到很久后凭空开火。</summary>
        private void MarkPendingTrigger()
        {
            pendingTrigger = true;
            pendingTriggerTime = Time.unscaledTime;
        }

        /// <summary>
        /// Harmony 前缀调用：这一枪是否需要由枪械状态机补发。
        /// 通过 ItemAgent_Gun.UpdateStates 前缀调用，保证设置好的 triggerThisFrame
        /// 就在紧接着的同一段状态机逻辑里被消费，不会被下一帧的输入覆盖。
        /// </summary>
        internal bool ShouldReplayTrigger(ItemAgent_Gun gun)
        {
            if (!pendingTrigger)
                return false;

            // 超时作废：玩家按下开火后即使很快松开也要补发一次（那是明确的射击意图），
            // 但枪械若因换弹 / 动画迟迟不可用，则不能事后再凭空开一枪。
            if (Time.unscaledTime - pendingTriggerTime > PendingTriggerTimeout)
            {
                pendingTrigger = false;
                return false;
            }

            if (!modEnabled || gun == null)
            {
                pendingTrigger = false;
                return false;
            }

            CharacterMainControl player = cachedPlayer;
            if (player == null)
            {
                pendingTrigger = false;
                return false;
            }

            // 玩家已经换成别的物品：这一枪不再补发
            if (player.GetGun() != gun)
                return false;

            // 角色还在奔跑时，游戏不会处理开火（Trigger 内部会把输入清掉）；
            // 等 Movement 真正退出奔跑之后再补发，否则这一枪会在奔跑状态下被吞掉。
            if (player.Running)
                return false;

            pendingTrigger = false;
            return true;
        }

        // ==================== ModSetting 集成 ====================

        /// <summary>config.txt / 旧版配置里的槽位（供 ModSetting 界面作为默认值）。</summary>
        internal string FileSlot => fileSlot;

        /// <summary>config.txt / 旧版配置里的禁攻击开关（供 ModSetting 界面作为默认值）。</summary>
        internal bool FileDisableAttack => fileDisableAttack;

        /// <summary>应用 ModSetting 中已保存的值；未保存的项继续沿用文件配置。</summary>
        internal void ApplyModSettingValues(bool hasSlot, string slot, bool hasDisableAttack, bool disableAttackValue)
        {
            try
            {
                modSettingSlotLoaded = hasSlot;
                if (hasSlot)
                    modSettingSlot = ModSettingIntegration.SlotFromOption(slot);

                modSettingDisableAttackLoaded = hasDisableAttack;
                if (hasDisableAttack)
                    modSettingDisableAttack = disableAttackValue;

                ApplyEffectiveSettings();
            }
            catch (Exception e)
            {
                LogException("应用 ModSetting 配置失败", e);
            }
        }

        /// <summary>ModSetting 下拉框变化回调。</summary>
        internal void OnModSettingSlotChanged(string option)
        {
            try
            {
                modSettingSlotLoaded = true;
                modSettingSlot = ModSettingIntegration.SlotFromOption(option);
                ApplyEffectiveSettings();
                Debug.Log(LogTag + " ModSetting：切换槽位为 " + modSettingSlot);
            }
            catch (Exception e)
            {
                LogException("应用 ModSetting 槽位失败", e);
            }
        }

        /// <summary>ModSetting 开关变化回调。</summary>
        internal void OnModSettingDisableAttackChanged(bool value)
        {
            try
            {
                modSettingDisableAttackLoaded = true;
                modSettingDisableAttack = value;
                ApplyEffectiveSettings();
                Debug.Log(LogTag + " ModSetting：奔跑中禁止近战攻击 = " + value);
            }
            catch (Exception e)
            {
                LogException("应用 ModSetting 禁攻击开关失败", e);
            }
        }

        /// <summary>把文件配置与 ModSetting 覆盖值合并成实际生效的配置。</summary>
        private void ApplyEffectiveSettings()
        {
            string slot = modSettingSlotLoaded ? modSettingSlot : fileSlot;
            weaponSlotHash = slot.GetHashCode();
            disableAttack = modSettingDisableAttackLoaded ? modSettingDisableAttack : fileDisableAttack;
        }

        // ==================== 奔跑状态机 ====================

        /// <summary>
        /// 获取主角色。游戏内每帧只做一次 Unity 判空（几个纳秒）；
        /// 缓存失效（换图 / 读档 / 主菜单）后才按 PlayerLookupInterval 节流重新查找，
        /// 避免每帧触发 LevelManager.Instance 的 FindFirstObjectByType 全场景扫描。
        /// </summary>
        private CharacterMainControl GetPlayer()
        {
            if (cachedPlayer != null)
                return cachedPlayer;

            float now = Time.unscaledTime;
            if (now < nextPlayerLookup)
                return null;

            nextPlayerLookup = now + PlayerLookupInterval;
            CharacterMainControl found = CharacterMainControl.Main;
            if (!ReferenceEquals(found, cachedPlayer))
            {
                // 换角色 / 读档 / 换图：上一角色的输入状态和奔跑会话都已失效，
                // 尤其不能让 combatOverride 残留，否则新角色会被一直按在行走状态；
                // 旧角色待还原的物品也不可能再装到新角色手上，一并丢弃。
                ResetInputState();
                ResetRunState();
            }
            cachedPlayer = found;
            return cachedPlayer;
        }

        /// <summary>清空与玩家输入相关的临时状态（战斗接管、补发开火、休眠等）。</summary>
        private void ResetInputState()
        {
            adsHeld = false;
            triggerHeld = false;
            combatOverride = false;
            pendingTrigger = false;
            combatGun = null;
            combatSwitchTime = 0f;
            pendingTriggerTime = 0f;
            suppressUntilNewRun = false;
            rearmTimer = 0f;
        }

        private void UpdateRunningState()
        {
            CharacterMainControl player = GetPlayer();
            if (player == null)
            {
                // 角色不存在（读档 / 换图 / 主菜单）：丢弃本次会话，不做还原
                ResetRunState();
                return;
            }

            // 战斗切枪并停跑后进入休眠：期间即使玩家一直按着 Shift 自动恢复奔跑，
            // 也不会再切回近战武器，直到玩家真正停下来（见 UpdateCombatSuppression）。
            if (suppressUntilNewRun)
            {
                UpdateCombatSuppression(player);
                return;
            }

            if (player.Running)
            {
                if (!inRun)
                    BeginRun(player);

                if (inRun && switchedThisRun)
                    TrackManualItemChange(player);

                return;
            }

            if (!inRun)
                return;

            // 正处在占用双手的动作里（使用物品、死亡等），等动作结束再还原
            if (!player.CanUseHand())
                return;

            if (TryRestore(player))
            {
                ResetRunState();
                return;
            }

            // 极少数情况下换手会失败（例如背包编辑被临时禁止），短时间内重试
            restoreTimer += Time.deltaTime;
            if (restoreTimer >= RestoreTimeout)
            {
                Debug.LogWarning(LogTag + " 恢复手持物品超时，放弃本次恢复");
                ResetRunState();
            }
        }

        /// <summary>
        /// 战斗切回枪之后的休眠解除判定。
        ///
        /// 不能只看「按住 Shift 恢复奔跑」：那样一松开鼠标就会又切回刀。
        /// 这里要求玩家真正停下来——连续 RearmIdleTime 秒未奔跑、且没有按着
        /// 瞄准/开火——才算这一轮战斗结束；之后的下一次起跑会重新判定切刀。
        /// </summary>
        private void UpdateCombatSuppression(CharacterMainControl player)
        {
            if (player == null || player.Running || adsHeld || triggerHeld)
            {
                rearmTimer = 0f;
                return;
            }

            rearmTimer += Time.deltaTime;
            if (rearmTimer < RearmIdleTime)
                return;

            rearmTimer = 0f;
            suppressUntilNewRun = false;
            LogToFile("已连续停跑 " + RearmIdleTime.ToString("0.0") + " 秒，战斗休眠解除");
        }

        private void BeginRun(CharacterMainControl player)
        {
            inRun = true;
            switchedThisRun = false;
            savedItem = null;
            restoreTimer = 0f;
            itemChangedTimer = 0f;

            // 玩家正在瞄准/开火（通常发生在起跑的同一帧）：这一轮不进近战，保持当前武器
            if (adsHeld || triggerHeld)
                return;

            Item target = GetSlotContent(player, weaponSlotHash);
            if (target == null)
                return; // 目标槽位是空的，没有可切换的武器

            ItemAgent_Gun gun = player.GetGun();
            if (gun != null && gun.IsReloading())
                return; // 换弹中不打断

            Item held = GetHeldItem(player);
            if (held == target)
                return; // 已经拿着目标武器，无需切换

            if (!player.ChangeHoldItem(target))
                return; // 动作中 / 状态不允许换手

            // held 可能为 null：空手起跑时切换后停跑保持当前武器，不强制空手
            savedItem = held;
            savedWasGun = gun != null; // 只有奔跑前拿的是枪，瞄准/开火时切回去才有意义
            switchedThisRun = true;

            LogToFile("起跑：已换成槽位武器（TypeID=" + target.TypeID + "），" +
                      "原手持=" + (held != null ? held.TypeID.ToString() : "空手") +
                      "，原是否枪械=" + savedWasGun);
        }

        /// <summary>奔跑中检测玩家是否主动换成了别的物品；主动换过则本次不再还原。</summary>
        private void TrackManualItemChange(CharacterMainControl player)
        {
            Item target = GetSlotContent(player, weaponSlotHash);
            Item held = GetHeldItem(player);

            if (held != null && held == target)
            {
                itemChangedTimer = 0f;
                return;
            }

            // 使用物品时手上会临时换成被使用的物品，结束后会自动换回来，不算主动换武器
            if (player.useItemAction != null && player.useItemAction.Running)
            {
                itemChangedTimer = 0f;
                return;
            }

            itemChangedTimer += Time.deltaTime;
            if (itemChangedTimer < ManualChangeGrace)
                return;

            // 玩家主动换成了别的物品：本次奔跑结束时不再还原
            savedItem = null;
            switchedThisRun = false;
            itemChangedTimer = 0f;
        }

        /// <summary>尝试恢复奔跑前的手持物品；返回 false 表示本次失败、可稍后重试。</summary>
        private bool TryRestore(CharacterMainControl player)
        {
            if (savedItem == null)
                return true; // 开跑时就是空手，保持当前武器

            // 原物品已被移出装备槽（存进容器 / 被丢弃），不再强行装回手上
            if (savedItem.PluggedIntoSlot == null)
            {
                savedItem = null;
                return true;
            }

            if (GetHeldItem(player) == savedItem)
            {
                savedItem = null;
                return true;
            }

            if (!player.ChangeHoldItem(savedItem))
                return false;

            LogToFile("停跑：已还原奔跑前的手持物品（TypeID=" + savedItem.TypeID + "）");
            savedItem = null;
            return true;
        }

        private static Item GetHeldItem(CharacterMainControl player)
        {
            DuckovItemAgent agent = player.CurrentHoldItemAgent;
            if (agent == null)
                return null;
            return agent.Item;
        }

        private static Item GetSlotContent(CharacterMainControl player, int slotHash)
        {
            if (player == null)
                return null;

            Slot slot = player.GetSlot(slotHash);
            if (slot == null)
                return null;
            return slot.Content;
        }

        private void ResetRunState()
        {
            inRun = false;
            switchedThisRun = false;
            savedItem = null;
            savedWasGun = false;
            restoreTimer = 0f;
            itemChangedTimer = 0f;
        }

        /// <summary>停用 / 卸载 mod 时恢复手持物品，避免玩家被卡在拿刀状态。</summary>
        private void RestoreOnShutdown()
        {
            if (inRun && switchedThisRun && savedItem != null && savedItem.PluggedIntoSlot != null)
            {
                CharacterMainControl player = CharacterMainControl.Main;
                if (player != null)
                {
                    try
                    {
                        player.ChangeHoldItem(savedItem);
                    }
                    catch (Exception e)
                    {
                        LogException("停用 mod 时恢复手持物品失败", e);
                    }
                }
            }

            ResetRunState();
        }

        // ==================== 配置 ====================

        private void ReloadConfigIfNeeded()
        {
            float now = Time.unscaledTime;
            if (now < nextConfigCheck)
                return;

            nextConfigCheck = now + ConfigCheckInterval;
            ReloadConfig(force: false);
        }

        private void ReloadConfig(bool force)
        {
            string modDir = info.path;
            string newPath = string.IsNullOrEmpty(modDir) ? null : Path.Combine(modDir, "config.txt");

            // 优先读取 mod 目录下的 config.txt（参考 config.example.txt）
            if (!string.IsNullOrEmpty(newPath) && File.Exists(newPath))
            {
                DateTime writeTime = SafeGetWriteTime(newPath);
                bool changed = force || !usingNewConfig || configPath != newPath || writeTime != configWriteTime;
                if (changed)
                {
                    configPath = newPath;
                    configWriteTime = writeTime;
                    usingNewConfig = true;
                    configInitialized = true;
                    ParseConfigFile(newPath);
                    Debug.Log(LogTag + " 已加载配置文件：" + newPath);
                }
                return;
            }

            // 没有 config.txt 时回退旧版位置（每秒重读，文件很小）
            if (force || !configInitialized || usingNewConfig)
            {
                usingNewConfig = false;
                configPath = null;
                Debug.Log(LogTag + " 未找到 config.txt，使用默认配置或旧版 StreamingAssets 配置");
            }
            ParseLegacyConfig();
            configInitialized = true;
        }

        private void ParseConfigFile(string path)
        {
            string slot = "MeleeWeapon";
            bool disable = false;

            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                        continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    if (key.Equals("slot", StringComparison.OrdinalIgnoreCase))
                        slot = NormalizeSlot(value);
                    else if (key.Equals("disableAttack", StringComparison.OrdinalIgnoreCase))
                        disable = ParseBool(value, false);
                }
            }
            catch (Exception e)
            {
                // 读取失败（文件被编辑器占用等）时保留上一次的有效配置，不重置为默认值
                LogException("读取配置文件失败", e);
                return;
            }

            fileSlot = slot;
            fileDisableAttack = disable;
            ApplyEffectiveSettings();
        }

        /// <summary>旧版配置：游戏目录 StreamingAssets 下的两个文本文件。</summary>
        private void ParseLegacyConfig()
        {
            string slot = "MeleeWeapon";
            bool disable = false;

            try
            {
                string dir = Application.streamingAssetsPath;
                string switchPath = Path.Combine(dir, "DashSwitchToKnife_SwitchConfig.txt");
                if (File.Exists(switchPath))
                    slot = NormalizeSlot(File.ReadAllText(switchPath));

                disable = File.Exists(Path.Combine(dir, "DashSwitchToKnife_DisableAttack.txt"));
            }
            catch (Exception e)
            {
                // 读取失败时保留上一次的有效配置
                LogException("读取旧版配置失败", e);
                return;
            }

            fileSlot = slot;
            fileDisableAttack = disable;
            ApplyEffectiveSettings();
        }

        private static string NormalizeSlot(string value)
        {
            value = (value ?? string.Empty).Trim();

            if (value.Equals("PrimaryWeapon", StringComparison.OrdinalIgnoreCase))
                return "PrimaryWeapon";
            if (value.Equals("SecondaryWeapon", StringComparison.OrdinalIgnoreCase))
                return "SecondaryWeapon";
            if (value.Equals("MeleeWeapon", StringComparison.OrdinalIgnoreCase))
                return "MeleeWeapon";

            if (value.Length > 0)
                Debug.LogWarning(LogTag + " 无法识别的槽位「" + value + "」，已回退为 MeleeWeapon");
            return "MeleeWeapon";
        }

        private static bool ParseBool(string value, bool fallback)
        {
            value = (value ?? string.Empty).Trim();

            if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                value == "1")
                return true;

            if (value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                value == "0")
                return false;

            return fallback;
        }

        private static DateTime SafeGetWriteTime(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        // ==================== Harmony ====================

        private void TryApplyHarmony()
        {
            if (HarmonyBootstrap.Applied)
                return;

            if (HarmonyBootstrap.Apply())
                return;

            if (harmonyRetry == null)
                harmonyRetry = StartCoroutine(WaitForHarmony());
        }

        /// <summary>前置「HarmonyLib」可能比本 mod 晚加载，定时重试直到 0Harmony 就绪。</summary>
        private IEnumerator WaitForHarmony()
        {
            bool warned = false;

            while (modEnabled && !HarmonyBootstrap.Applied)
            {
                yield return new WaitForSecondsRealtime(1f);

                if (!modEnabled)
                    break;

                try
                {
                    if (HarmonyBootstrap.Apply())
                        break;

                    if (!warned && HarmonyBootstrap.Load0Harmony() == null)
                    {
                        warned = true;
                        Debug.LogWarning(LogTag + " 未找到 0Harmony，请启用前置「HarmonyLib」（工坊 3589088839）。" +
                                         "切刀功能不受影响，但「奔跑中禁止近战攻击」和「瞄准/开火时切回原枪」暂不可用。");
                        LogToFile("未找到 0Harmony，Harmony 相关功能不可用");
                    }
                }
                catch (Exception e)
                {
                    LogException("等待 Harmony 前置失败", e);
                }
            }

            harmonyRetry = null;
        }

        // ==================== ModSetting ====================

        private void TryRegisterModSetting()
        {
            if (ModSettingIntegration.Registered)
                return;

            if (ModSettingIntegration.TryRegister(this))
                return;

            if (modSettingRetry == null)
                modSettingRetry = StartCoroutine(WaitForModSetting());
        }

        /// <summary>ModSetting 可能比本 mod 晚启用，定时重试直到注册成功。</summary>
        private IEnumerator WaitForModSetting()
        {
            while (modEnabled && !ModSettingIntegration.Registered)
            {
                yield return new WaitForSecondsRealtime(1f);

                if (!modEnabled)
                    break;

                try
                {
                    if (ModSettingIntegration.TryRegister(this))
                        break;
                }
                catch (Exception e)
                {
                    LogException("注册 ModSetting 设置界面失败", e);
                }
            }

            modSettingRetry = null;
        }

        // ==================== 日志 ====================

        /// <summary>
        /// 关键状态写入 mod 目录下的诊断日志。
        /// 游戏运行日志会被其他 mod 的日志 handler 接管，本 mod 的 Debug.Log
        /// 不一定出现在 Player.log 里；写文件失败时直接放弃，绝不影响游戏。
        /// </summary>
        internal static void LogToFile(string message)
        {
            try
            {
                if (logFileUnavailable)
                    return;

                string path = logFilePath;
                if (string.IsNullOrEmpty(path))
                {
                    ModBehaviour mod = Instance;
                    string dir = mod != null ? mod.info.path : null;
                    if (string.IsNullOrEmpty(dir))
                        return; // info（mod 目录）还没就绪

                    path = Path.Combine(dir, "DashSwitchToKnifeOpt.log");
                    logFilePath = path;
                    File.WriteAllText(path,
                        LogTag + " 会话开始 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);
                }

                lock (logFileLock)
                {
                    File.AppendAllText(path,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
                }
            }
            catch
            {
                // 写日志失败（目录只读、文件被占用等）：以后不再尝试
                logFileUnavailable = true;
            }
        }

        /// <summary>异常日志限流，避免每帧刷屏。</summary>
        private void LogException(string what, Exception e)
        {
            float now = Time.unscaledTime;
            if (now - lastErrorLogTime < 10f)
                return;

            lastErrorLogTime = now;
            Debug.LogError(LogTag + " " + what + "：" + e);
        }
    }
}
