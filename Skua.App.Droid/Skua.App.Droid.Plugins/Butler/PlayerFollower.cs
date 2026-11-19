using ButlerV5;
using Skua.App.Droid.Core.Plugins;
using Skua.Core.Interfaces;
using Skua.Core.Models.Players;
using Skua.Core.Models.Skills;

namespace Skua.App.Droid.Plugins.Butler;

/// <summary>
/// ButlerV5's follower for one account, following any player by name.
/// </summary>
/// <remarks>
/// <para>
/// The desktop follower (Skua.Plugin.ButlerV5/Follower.cs) learns where its master is from the master's ButlerV5
/// sync file, so the master must be another local ButlerV5 account. Here the player is anyone: where they are comes
/// from the game itself, as in Butler v3: the server's /goto (rate-limited by the goto delay) brings this account to
/// the player's room, and once there the room's player list gives their cell, position and combat state.
/// </para>
/// <para>
/// Kept from the desktop follower, adapted where it read the sync file: suspend while a script runs and resume after,
/// re-arm after a relogin, Skua's Auto Attack/Hunt kept off, the CoreBots class and its skill set (started lazily in
/// the room, paused while travelling), passive attack (the player's combat state instead of the file's "attacking"),
/// leech mode, cell jumps without autoCorrect, copy walk, always goto, the warning packets ("Locked zone", "is full",
/// "ignoring goto"), parking (house / whitemap / stay) with aggro forced off, and the deaggro jump.
/// </para>
/// <para>
/// Not here (no meaning for one headless account): direct joins of the master's map-room from its file (the room is
/// unknown without it), quest bypasses / cloning / unlock-all / level fake (client-side gates the headless client does
/// not have), AntiLag and the butler display options (nothing is drawn).
/// </para>
/// </remarks>
internal sealed class PlayerFollower
{
    private const int LoopDelayMs = 250;
    private const int JumpMinIntervalMs = 500;
    private const int ParkedRetryMs = 10000;
    private const string ParkMap = "whitemap-100000";

    private readonly Func<ButlerOptions> _options;
    private readonly ButlerLog _log;
    private readonly Action<ButlerStatus> _status;
    private readonly Func<IEnumerable<AdvancedSkill>?> _skillSets;

    private IScriptInterface _bot = null!;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _lastGoto = DateTime.MinValue;
    private DateTime _lastJumpTime = DateTime.MinValue;
    private DateTime _lastWalkCommandTime = DateTime.MinValue;
    private DateTime _lastParkedTry = DateTime.MinValue;
    private int _lastWalkTargetX;
    private int _lastWalkTargetY;
    private bool _hasWalkTarget;
    private int _gotoAttempts;
    private int _failedRounds;
    private int _roomFullCount;
    private string? _skillClass;
    private ClassUseMode _skillMode = ClassUseMode.Base;
    private ButlerClassType _classType = ButlerClassType.None;
    private string? _modeNoteShownFor;
    private bool _skillsActive;
    private bool _skillsPaused;
    private bool _suspended;
    private bool _wasLoggedIn;
    private bool? _lastWithPlayer;
    private bool _autoStopLogged;
    private volatile bool _parked;
    private volatile bool _lockedZone;
    private volatile bool _roomFull;
    private volatile bool _gotoOff;
    private string? _ownHouseUsername;
    private string? _ownHouseRoom;

    /// <param name="options">The live options.</param>
    /// <param name="log">The butler's log.</param>
    /// <param name="status">Told every state change.</param>
    /// <param name="skillSets">Skua's loaded advanced skill sets (to pick a mode the class has), or null.</param>
    public PlayerFollower(Func<ButlerOptions> options, ButlerLog log, Action<ButlerStatus> status, Func<IEnumerable<AdvancedSkill>?> skillSets)
    {
        _skillSets = skillSets;
        _options = options;
        _log = log;
        _status = status;
    }

    public string? Player { get; private set; }

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start(IScriptInterface bot, string player)
    {
        Stop();
        _bot = bot;
        Player = player;
        _gotoAttempts = _failedRounds = _roomFullCount = 0;
        _lastGoto = _lastJumpTime = _lastWalkCommandTime = _lastParkedTry = DateTime.MinValue;
        _hasWalkTarget = false;
        _suspended = _parked = false;
        _lastWithPlayer = null;
        _lockedZone = _roomFull = _gotoOff = false;

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        _bot.Events.ExtensionPacketReceived += PacketListener;
        try { _bot.Flash.FlashCall += OnFlashCall; } catch { }
        ButlerOptions o = _options();
        _log.Debug("Follower", $"options: passiveAttack={o.PassiveAttack} leech={o.LeechMode} park={o.Park} gotoDelay={o.GotoDelayMs}ms " +
            $"alwaysGoto={o.AlwaysGoto} copyWalk={o.CopyWalk} rescue={o.RescueThreshold} instantWarnings={o.InstantWarnings} class={o.ClassType}");
        _log.Event($"Following {player}.");
        Report(ButlerFollowState.Traveling, $"Going to {player}.");
        _loop = Task.Run(() => Run(player, token), token);
    }

    public void Stop(bool park = false)
    {
        if (_cts is null)
            return;
        try { _bot.Events.ExtensionPacketReceived -= PacketListener; } catch { }
        try { _bot.Flash.FlashCall -= OnFlashCall; } catch { }
        _cts.Cancel();
        try { _loop?.Wait(3000); } catch { }
        _cts.Dispose();
        _cts = null;
        _loop = null;

        StopSkills();
        StopAttacking(deaggroJump: true);
        if (park && _bot.Player?.LoggedIn == true)
            Park("Stopped: parking.");
        _log.Event(Player is not null ? $"Stopped following {Player}." : "Follow stopped.");
        Player = null;
        _parked = false;
        _status(ButlerStatus.Idle);
    }

    private void Report(ButlerFollowState state, string detail) =>
        _status(new ButlerStatus(Player, state, detail, _gotoAttempts));

    private void Run(string player, CancellationToken token)
    {
        try
        {
            Arm(freshLogin: false);
            _wasLoggedIn = _bot.Player?.LoggedIn == true;

            while (!token.IsCancellationRequested)
            {
                if (_bot.Player?.LoggedIn != true || !_bot.Player.Alive)
                {
                    if (_bot.Player?.LoggedIn != true)
                    {
                        _wasLoggedIn = false;
                        Report(ButlerFollowState.Waiting, "Waiting for this account to log in again.");
                    }
                    else
                    {
                        Report(ButlerFollowState.Waiting, "Waiting to respawn.");
                    }
                    Wait(token, 1000);
                    continue;
                }

                // Relogin: the skill engine and the equipped class die with the connection; wait for the character
                // to settle, then re-arm with a class re-equip (desktop behaviour).
                if (!_wasLoggedIn)
                {
                    _wasLoggedIn = true;
                    _log.Debug("Follower", "re-login detected - waiting for character to load");
                    WaitForCharacterReady(token);
                    Arm(freshLogin: true);
                }

                // A script on this account takes the character over: suspend, resume when it stops.
                if (_bot.Manager.ScriptRunning)
                {
                    if (!_suspended)
                    {
                        _suspended = true;
                        StopSkills();
                        StopAttacking(deaggroJump: true);
                        _log.Event($"A script is running - following {player} is suspended until it stops.", ButlerLogLevel.Warning);
                        Report(ButlerFollowState.Suspended, "Suspended while a script runs.");
                    }
                    Wait(token, 1000);
                    continue;
                }
                if (_suspended)
                {
                    _suspended = false;
                    _gotoAttempts = 0;
                    _log.Event($"Script stopped - resuming follow of {player}.");
                }

                StopUserAuto();
                if (_parked)
                    ForceParkingAggroOff();

                ButlerOptions options = _options();
                PlayerInfo? info = FindInRoom(player);

                if (info is null)
                {
                    if (_parked && (DateTime.UtcNow - _lastParkedTry).TotalMilliseconds < ParkedRetryMs)
                    {
                        Wait(token, 1000);
                        continue;
                    }
                    _lastParkedTry = DateTime.UtcNow;

                    // Travelling: pause skills (they re-acquire targets and drag the butler back into combat).
                    if (_skillsActive && !_skillsPaused)
                        PauseSkills();
                    StopAttacking(deaggroJump: true);

                    if (ConsumeWarnings(player))
                    {
                        Wait(token, LoopDelayMs);
                        continue;
                    }

                    int threshold = Math.Clamp(options.RescueThreshold, 1, 10);
                    if (_gotoAttempts >= threshold)
                    {
                        _gotoAttempts = 0;
                        _failedRounds++;
                        _log.Debug("Follower", $"goto not landing after {threshold} attempts (round {_failedRounds})");
                        if (_failedRounds >= 3)
                        {
                            _failedRounds = 0;
                            ParkOnce($"Can't reach {player} after {threshold * 3} gotos - are they online, on this server, with goto on? Parked; trying again every {ParkedRetryMs / 1000} s.");
                            continue;
                        }
                    }
                    if (TryGoto(player, options))
                    {
                        _gotoAttempts++;
                        if (!_parked)
                            Report(ButlerFollowState.Traveling, $"Going to {player} (goto {_gotoAttempts}).");
                    }
                    Wait(token, LoopDelayMs);
                    continue;
                }

                // In the player's room.
                if (_lastWithPlayer != true)
                    _log.Debug("Follower", $"{player} is in this room ({_bot.Map?.Name}) cell={info.Cell}");
                _lastWithPlayer = true;
                if (_parked)
                {
                    _parked = false;
                    _log.Event($"{player} is here - resuming follow.");
                }
                _gotoAttempts = _failedRounds = _roomFullCount = 0;
                _lockedZone = _roomFull = _gotoOff = false;

                if (options.LeechMode)
                {
                    if (_skillsActive)
                        StopSkills();
                    StopAttacking(deaggroJump: true);
                    if (!string.Equals(_bot.Player.Cell, "Enter", StringComparison.OrdinalIgnoreCase) &&
                        (DateTime.UtcNow - _lastJumpTime).TotalMilliseconds >= JumpMinIntervalMs)
                    {
                        _log.Debug("Follower", $"leech: jump {_bot.Player.Cell} -> Enter/Spawn");
                        _bot.Map.Jump("Enter", "Spawn", autoCorrect: false);
                        _lastJumpTime = DateTime.UtcNow;
                    }
                    Report(ButlerFollowState.WithPlayer, $"With {player} (leech: waiting at Enter).");
                }
                else
                {
                    string targetCell = info.Cell;
                    string targetPad = string.IsNullOrEmpty(info.Pad) ? "Spawn" : info.Pad;
                    bool sameCell = _bot.Player.Cell.Equals(targetCell, StringComparison.OrdinalIgnoreCase);
                    bool canCopyWalk = options.CopyWalk && sameCell;
                    if (options.AlwaysGoto && !canCopyWalk && !_gotoOff)
                        TryGoto(player, options);

                    if (!sameCell && (DateTime.UtcNow - _lastJumpTime).TotalMilliseconds >= JumpMinIntervalMs)
                    {
                        // autoCorrect off, as the desktop follower: the target is the player's own cell.
                        _log.Debug("Follower", $"jump {_bot.Player.Cell}/{_bot.Player.Pad} -> {targetCell}/{targetPad}");
                        _bot.Map.Jump(targetCell, targetPad, autoCorrect: false);
                        _lastJumpTime = DateTime.UtcNow;
                    }

                    if (canCopyWalk)
                        CopyPlayerWalk(info, options);
                    else
                        _hasWalkTarget = false;

                    bool fighting = HandleCombat(info, options);
                    Report(ButlerFollowState.WithPlayer, $"With {player} in {_bot.Map.Name}, cell {targetCell}{(fighting ? ", fighting" : string.Empty)}.");
                }

                Wait(token, LoopDelayMs);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Event($"Follow loop error: {ex.Message}", ButlerLogLevel.Error);
        }
    }

    /// <summary>The player's entry in this room's player list, or null when they are not in this room.</summary>
    private PlayerInfo? FindInRoom(string player)
    {
        try
        {
            if (_bot.Map?.Loaded != true)
                return null;
            if (string.Equals(player, _bot.Player.Username, StringComparison.OrdinalIgnoreCase))
                return null;
            return _bot.Map.TryGetPlayer(player, out PlayerInfo? info) && !string.IsNullOrEmpty(info?.Cell) ? info : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Handles a warning seen since the last tick. True when this tick is used up.</summary>
    private bool ConsumeWarnings(string player)
    {
        if (_gotoOff)
        {
            _gotoOff = false;
            ParkOnce($"{player} is ignoring goto requests: they can only be followed after coming to this room. Parked; trying again every {ParkedRetryMs / 1000} s.");
            return true;
        }
        if (_roomFull)
        {
            _roomFull = false;
            if (++_roomFullCount >= 3)
            {
                _roomFullCount = 0;
                ParkOnce($"{player}'s room is full after 3 attempts. Parked; trying again every {ParkedRetryMs / 1000} s.");
            }
            else
            {
                _log.Event($"{player}'s room is full (attempt {_roomFullCount}).", ButlerLogLevel.Warning);
            }
            return true;
        }
        if (_lockedZone)
        {
            _lockedZone = false;
            // The desktop follower would join the master's room from its sync file; without one, keep trying goto.
            _log.Event($"{player} is in a locked or unavailable map; this account cannot enter it.", ButlerLogLevel.Warning);
            _gotoAttempts++;
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ arming, class and skills (desktop follower)

    private void Arm(bool freshLogin)
    {
        StopUserAuto();
        StopSkills();
        SetupClassAndSkills(forceEquipCurrent: freshLogin);
    }

    private void SetupClassAndSkills(bool forceEquipCurrent)
    {
        try
        {
            string? me = _bot.Player?.Username;
            if (string.IsNullOrEmpty(me))
                return;

            _skillClass = null;
            _skillMode = ClassUseMode.Base;
            _modeNoteShownFor = null;

            ButlerClassType type = (ButlerClassType)(int)_options().ClassType;
            _classType = type;
            string? classToEquip = null;

            if (type != ButlerClassType.None)
            {
                if (Enum.TryParse(AccountConfig.GetCboClassMode(me, type), ignoreCase: true, out ClassUseMode mode))
                    _skillMode = mode;

                string? cboClass = AccountConfig.GetCboClassName(me, type);
                if (string.IsNullOrEmpty(cboClass))
                    _log.Event($"No CoreBots {type} class configured for this account - keeping current class.", ButlerLogLevel.Warning);
                else if (cboClass.Equals(AccountConfig.CboCurrentClass, StringComparison.OrdinalIgnoreCase))
                    classToEquip = forceEquipCurrent ? _bot.Player?.CurrentClass?.Name : null;
                else
                    classToEquip = cboClass;
            }
            else if (forceEquipCurrent)
            {
                classToEquip = _bot.Player?.CurrentClass?.Name;
            }

            if (!string.IsNullOrEmpty(classToEquip))
            {
                _log.Event($"Equipping class: {classToEquip}");
                _bot.Inventory.EquipItem(classToEquip);
                bool equipped = false;
                for (int i = 0; i < 6; i++)
                {
                    Thread.Sleep(250);
                    string? worn = _bot.Player?.CurrentClass?.Name;
                    if (!string.IsNullOrEmpty(worn) && worn.Trim().Equals(classToEquip.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        equipped = true;
                        break;
                    }
                }
                if (equipped)
                {
                    _skillClass = classToEquip;
                }
                else
                {
                    string? worn = _bot.Player?.CurrentClass?.Name;
                    _log.Event($"Couldn't equip '{classToEquip}' - not in this account's inventory? Keeping current class ({worn ?? "unknown"}).", ButlerLogLevel.Warning);
                    _skillClass = worn;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug("Follower", $"class equip failed: {ex.Message}", ButlerLogLevel.Error);
        }
    }

    private void WaitForCharacterReady(CancellationToken token)
    {
        for (int i = 0; i < 40 && !token.IsCancellationRequested; i++)
        {
            try
            {
                if (_bot.Player?.LoggedIn == true && _bot.Player.Alive && _bot.Map?.Loaded == true &&
                    !string.IsNullOrEmpty(_bot.Player.CurrentClass?.Name))
                    break;
            }
            catch
            {
            }
            Wait(token, 250);
        }
    }

    private ClassUseMode ResolveSkillMode(string skillClass)
    {
        ClassUseMode mode = _skillMode;
        try
        {
            mode = AccountConfig.ResolveSkillMode(skillClass, _skillMode, _classType, _skillSets()?.ToList());
        }
        catch (Exception ex)
        {
            _log.Debug("Follower", $"skill mode lookup failed: {ex.Message}");
        }
        if (mode != _skillMode && _modeNoteShownFor != skillClass)
        {
            _modeNoteShownFor = skillClass;
            _log.Event($"{skillClass} has no {_skillMode} skill set - using {mode}.");
        }
        return mode;
    }

    private void StartSkills()
    {
        RefreshSkillState();
        if (_skillsActive)
        {
            if (_skillsPaused)
                ResumeSkills();
            return;
        }
        try
        {
            string skillClass = _skillClass ?? _bot.Player?.CurrentClass?.Name ?? "generic";
            ClassUseMode mode = ResolveSkillMode(skillClass);
            _log.Debug("Follower", $"starting skill engine: {skillClass} ({mode})");
            _bot.Skills.StartAdvanced(skillClass, false, mode);
            RefreshSkillState();
        }
        catch (Exception ex)
        {
            RefreshSkillState();
            _log.Debug("Follower", $"skill engine start failed: {ex.Message}", ButlerLogLevel.Error);
        }
    }

    private void StopUserAuto()
    {
        try
        {
            if (_bot.Auto.IsRunning)
            {
                if (!_autoStopLogged)
                {
                    _autoStopLogged = true;
                    _log.Event("Stopped Skua's Auto Attack/Hunt - it conflicts with following; the butler runs combat itself.");
                }
                _bot.Auto.Stop();
            }
            else
            {
                _autoStopLogged = false;
            }
        }
        catch
        {
        }
    }

    private void StopSkills()
    {
        try
        {
            // Wake a paused worker first so it sees the cancellation (ScriptSkill.Stop does not signal the pause).
            if (_bot.Skills is Skua.Core.Scripts.ScriptSkill ss && ss.IsPaused)
                ss.Resume();
            _bot.Skills.Stop();
        }
        catch (Exception ex)
        {
            _log.Debug("Follower", $"skill engine stop failed: {ex.Message}");
        }
        RefreshSkillState();
    }

    private void PauseSkills()
    {
        RefreshSkillState();
        if (!_skillsActive || _skillsPaused)
            return;
        if (_bot.Skills is Skua.Core.Scripts.ScriptSkill ss)
        {
            try
            {
                ss.Pause();
            }
            catch (Exception ex)
            {
                _log.Debug("Follower", $"skill pause failed: {ex.Message}");
            }
            RefreshSkillState();
        }
        else
        {
            StopSkills();
        }
    }

    private void ResumeSkills()
    {
        RefreshSkillState();
        if (!_skillsActive)
        {
            StartSkills();
            return;
        }
        if (!_skillsPaused)
            return;
        if (_bot.Skills is Skua.Core.Scripts.ScriptSkill ss)
        {
            try
            {
                ss.Resume();
            }
            catch (Exception ex)
            {
                _log.Debug("Follower", $"skill resume failed: {ex.Message}");
            }
            RefreshSkillState();
        }
    }

    private void RefreshSkillState()
    {
        try
        {
            bool running = _bot.Skills.TimerRunning;
            _skillsActive = running;
            _skillsPaused = running && _bot.Skills is Skua.Core.Scripts.ScriptSkill ss && ss.IsPaused;
        }
        catch
        {
            _skillsActive = _skillsPaused = false;
        }
    }

    /// <summary>Fights beside the player (or only while they fight, with passive attack). True when attacking.</summary>
    private bool HandleCombat(PlayerInfo player, ButlerOptions options)
    {
        try
        {
            RefreshSkillState();
            // intState 2 = in combat (the desktop follower read "attacking" from the master's sync file).
            bool shouldAttack = !options.PassiveAttack || player.State == 2;
            if (shouldAttack)
            {
                if (!_skillsActive)
                    StartSkills();
                else if (_skillsPaused)
                    ResumeSkills();
                _bot.Combat.Attack("*");
                return true;
            }
            if (_skillsActive && !_skillsPaused)
                PauseSkills();
            StopAttacking(deaggroJump: true);
        }
        catch
        {
        }
        return false;
    }

    private void StopAttacking(bool deaggroJump = false)
    {
        try
        {
            bool wasFighting = _bot.Player?.HasTarget == true || _bot.Player?.InCombat == true;
            if (!wasFighting)
                return;
            _bot.Combat.CancelAutoAttack();
            _bot.Combat.CancelTarget();
            if (deaggroJump)
            {
                _log.Debug("Follower", "same-cell rejoin to drop monster aggro");
                _bot.Map.Jump(_bot.Player?.Cell ?? "Enter", _bot.Player?.Pad ?? "Spawn", autoCorrect: false);
            }
        }
        catch
        {
        }
    }

    private void CopyPlayerWalk(PlayerInfo player, ButlerOptions options)
    {
        IScriptPlayer me = _bot.Player;
        int targetX = (int)Math.Round(player.X);
        int targetY = (int)Math.Round(player.Y);
        double dx = targetX - me.X;
        double dy = targetY - me.Y;
        if (dx * dx + dy * dy <= 100)
        {
            _hasWalkTarget = false;
            return;
        }

        DateTime now = DateTime.UtcNow;
        bool targetChanged = !_hasWalkTarget || Math.Abs(targetX - _lastWalkTargetX) >= 4 || Math.Abs(targetY - _lastWalkTargetY) >= 4;
        double elapsedMs = (now - _lastWalkCommandTime).TotalMilliseconds;
        if (!targetChanged && elapsedMs < 1500)
            return;
        if (elapsedMs < Math.Clamp(options.GotoDelayMs, LoopDelayMs, 2000))
            return;
        try
        {
            int speed = me.WalkSpeed > 0 ? Math.Clamp(me.WalkSpeed, 1, 20) : 8;
            _bot.Flash.Call("walkTo", targetX, targetY, speed);
            _lastWalkTargetX = targetX;
            _lastWalkTargetY = targetY;
            _lastWalkCommandTime = now;
            _hasWalkTarget = true;
        }
        catch (Exception ex)
        {
            _log.Debug("Follower", $"copy walk failed: {ex.Message}");
        }
    }

    /// <returns>True when a goto was sent (not held back by the goto delay).</returns>
    private bool TryGoto(string player, ButlerOptions options)
    {
        int delayMs = Math.Clamp(options.GotoDelayMs, 0, 60000);
        if ((DateTime.UtcNow - _lastGoto).TotalMilliseconds < delayMs)
            return false;
        _lastGoto = DateTime.UtcNow;
        try
        {
            _log.Debug("Follower", $"goto {player} (my map={_bot.Map?.Name})");
            _bot.Player.Goto(player);
            return true;
        }
        catch (Exception ex)
        {
            _log.Debug("Follower", $"goto failed: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ parking (desktop follower)

    private void ParkOnce(string reason)
    {
        if (_parked)
            return;
        _parked = true;
        _lastParkedTry = DateTime.UtcNow;
        Park(reason);
        Report(ButlerFollowState.Parked, reason);
    }

    private void Park(string reason)
    {
        try
        {
            _log.Event(reason, ButlerLogLevel.Warning);
            ForceParkingAggroOff();
            StopSkills();
            StopAttacking(deaggroJump: true);

            ButlerParkSpot spot = _options().Park;
            if (spot == ButlerParkSpot.Stay)
                return;
            string already = GetMyMapRoom();
            if (spot == ButlerParkSpot.House && IsInOwnHouse(already))
                return;
            if (spot == ButlerParkSpot.Whitemap && already.StartsWith("whitemap-", StringComparison.OrdinalIgnoreCase))
                return;

            for (int i = 0; i < 12 && _bot.Player?.InCombat == true; i++)
                ParkingWait(250);

            try
            {
                if (spot == ButlerParkSpot.House && _bot.Player?.LoggedIn == true && _bot.House?.Items?.Any(h => h.Equipped) == true)
                {
                    // The server's "too soon after combat" cooldown on house joins outlives the combat flag.
                    ParkingWait(3000);
                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        _bot.Send.Packet($"%xt%zm%house%1%{_bot.Player.Username}%");
                        for (int i = 0; i < 24; i++)
                        {
                            ParkingWait(250);
                            if (IsInOwnHouse(GetMyMapRoom()))
                            {
                                _log.Debug("Follower", $"parked in own house (attempt {attempt})");
                                return;
                            }
                        }
                        if (attempt < 3)
                            ParkingWait(5000);
                    }
                    _log.Debug("Follower", "own house join didn't land - falling back to whitemap");
                }
            }
            catch (Exception ex)
            {
                _log.Debug("Follower", $"house park failed: {ex.Message}");
            }

            ForceParkingAggroOff();
            _bot.Map.Join(ParkMap);
        }
        catch (Exception ex)
        {
            _log.Debug("Follower", $"park failed: {ex.Message}", ButlerLogLevel.Error);
        }
        finally
        {
            ForceParkingAggroOff();
        }
    }

    private void ForceParkingAggroOff()
    {
        try
        {
            _bot.Options.AggroMonsters = false;
            _bot.Options.AggroAllMonsters = false;
        }
        catch
        {
        }
    }

    private void ParkingWait(int milliseconds)
    {
        int remaining = Math.Max(0, milliseconds);
        CancellationToken token = _cts?.Token ?? CancellationToken.None;
        while (remaining > 0 && !token.IsCancellationRequested)
        {
            ForceParkingAggroOff();
            int slice = Math.Min(100, remaining);
            Thread.Sleep(slice);
            remaining -= slice;
        }
    }

    private string GetMyMapRoom()
    {
        try
        {
            if (_bot.Map?.Loaded != true)
                return string.Empty;
            string name = _bot.Map.Name?.ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrEmpty(name) || name == "-1")
                return string.Empty;
            string fullName = _bot.Map.FullName ?? string.Empty;
            int dash = fullName.LastIndexOf('-');
            if (dash > 0 && dash < fullName.Length - 1)
            {
                string room = fullName[(dash + 1)..];
                if (room.All(char.IsDigit))
                    return $"{name}-{room}";
            }
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>AQW uses the owner's CharID as the house room number (Player.ID is the session uid).</summary>
    private bool IsInOwnHouse(string room)
    {
        try
        {
            string? username = _bot.Player?.Username;
            if (string.IsNullOrWhiteSpace(username))
                return false;
            if (!string.Equals(username, _ownHouseUsername, StringComparison.OrdinalIgnoreCase))
            {
                _ownHouseUsername = username;
                _ownHouseRoom = null;
            }
            if (_ownHouseRoom is null)
            {
                int charId = _bot.Flash.GetGameObject<int>("world.myAvatar.objData.CharID");
                if (charId <= 0)
                    return false;
                _ownHouseRoom = $"house-{charId}";
            }
            return string.Equals(room, _ownHouseRoom, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void Wait(CancellationToken token, int ms)
    {
        if (!_parked)
        {
            token.WaitHandle.WaitOne(ms);
            return;
        }
        int remaining = Math.Max(0, ms);
        while (remaining > 0 && !token.IsCancellationRequested)
        {
            ForceParkingAggroOff();
            int slice = Math.Min(100, remaining);
            if (token.WaitHandle.WaitOne(slice))
                break;
            remaining -= slice;
        }
    }

    // ------------------------------------------------------------------ warnings (desktop follower)

    /// <summary>The raw packet stream (raised headless for every server packet, as skua.swf's "pext").</summary>
    private void OnFlashCall(string function, object[] args)
    {
        try
        {
            if (!_options().InstantWarnings || !string.Equals(function, "pext", StringComparison.OrdinalIgnoreCase) || args is not { Length: > 0 })
                return;
            Classify(string.Join(" ", args.Select(a => a?.ToString() ?? string.Empty)), "instant warning");
        }
        catch
        {
        }
    }

    /// <summary>Skua's ExtensionPacketReceived (same warnings; the desktop follower listens to both).</summary>
    private void PacketListener(dynamic packet)
    {
        try
        {
            if (packet is null)
                return;
            dynamic? p = packet["params"];
            if (p is null || p!.type != "str")
                return;
            dynamic? data = p!.dataObj;
            string? cmd = data?[0];
            if (cmd == "server")
            {
                string? text = data![2]?.ToString();
                if (!string.IsNullOrWhiteSpace(text) && text!.Contains("is ignoring goto requests", StringComparison.OrdinalIgnoreCase))
                    _gotoOff = true;
            }
            else if (cmd == "warning")
            {
                Classify(Convert.ToString(packet) ?? string.Empty, "warning packet");
            }
        }
        catch
        {
        }
    }

    private void Classify(string text, string source)
    {
        if (text.Length == 0)
            return;
        if (text.Contains("a Locked zone.", StringComparison.OrdinalIgnoreCase) || text.Contains("is not available.", StringComparison.OrdinalIgnoreCase))
        {
            _lockedZone = true;
            _log.Debug("Follower", $"{source}: locked zone / not available");
        }
        if (text.Contains("is full", StringComparison.OrdinalIgnoreCase))
        {
            _roomFull = true;
            _log.Debug("Follower", $"{source}: room full");
        }
        if (text.Contains("ignoring goto", StringComparison.OrdinalIgnoreCase))
        {
            _gotoOff = true;
            _log.Debug("Follower", $"{source}: ignoring goto");
        }
    }
}
