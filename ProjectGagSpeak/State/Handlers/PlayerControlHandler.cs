using CkCommons;
using Dalamud.Interface.ImGuiNotification;
using GagSpeak.GameInternals;
using GagSpeak.GameInternals.Addons;
using GagSpeak.GameInternals.Detours;
using GagSpeak.Interop;
using GagSpeak.Interop.Helpers;
using GagSpeak.Kinksters;
using GagSpeak.PlayerClient;
using GagSpeak.PlayerControl;
using GagSpeak.Services.Controller;
using GagSpeak.Services.Mediator;
using GagSpeak.Utils;
using GagSpeak.WebAPI;
using GagspeakAPI.Attributes;
using GagspeakAPI.Data;

namespace GagSpeak.State.Handlers;

/// <summary>
///     Handles the enabling and disabling of various hardcore changes.
/// </summary>
public class PlayerCtrlHandler
{
    private readonly ILogger<PlayerCtrlHandler> _logger;
    private readonly GagspeakMediator _mediator;
    private readonly MainConfig _config;
    private readonly IpcCallerLifestream _ipc;
    private readonly MovementController _movement;
    private readonly OverlayHandler _overlay;
    private readonly HcTaskManager _hcTasks;
    private readonly KinksterManager _kinksters;
    
    public const string ConfinementTaskName = "Travel To Location";

    // How long Lifestream may sit idle mid-trip before we treat the travel as failed.
    private const int TravelStallTimeoutMs = 25000;
    private long _travelStallTick;

    // Stores the players's movement mode, useful for when we change it.
    private MovementMode _cachedPlayerMoveMode = MovementMode.NotSet;
    public PlayerCtrlHandler(ILogger<PlayerCtrlHandler> logger, GagspeakMediator mediator,
        MainConfig config, IpcCallerLifestream ipc, MovementController movement, 
        OverlayHandler overlay, HcTaskManager hcTasks, KinksterManager kinksters)
    {
        _logger = logger;
        _mediator = mediator;
        _config = config;
        _ipc = ipc;
        _movement = movement;
        _overlay = overlay;
        _hcTasks = hcTasks;
        _kinksters = kinksters;
    }

    public async void ApplyHypnoEffect(UserData enactor, HypnoticEffect effect, DateTimeOffset expireTimeUTC, string? image)
    {
        if (!_kinksters.TryGetKinkster(enactor, out var kinkster))
            throw new Bagagwa($"Failed to get Kinkster for UID: {enactor.UID} for Hypnosis!");
        
        try
        {
            await _overlay.ApplyKinkstersHypnoEffect(enactor, effect, expireTimeUTC, image);
        }
        catch (Bagagwa)
        {
            _logger.LogWarning($"Error while attempting to apply hypnotic effect! Flagging ValidEffect was flagged as false.");
        }
        _logger.LogInformation($"[{kinkster.GetNickAliasOrUid()}] Enabled your Hypnotic Effect!");
    }

    /// <summary>
    ///     It is possible that the removal effect is triggered on a safeword or natural timer falloff. <para />
    /// 
    ///     If the server it down or the change cannot be processed, we want to still remove the player controls 
    ///     client-side, but not invoke the achievements for the change. <para />
    ///     
    ///     This way, on reconnection, the hardcore state will be reapplied, timer will immediately expire, and
    ///     they will get the achievement then. It also ensures they are not 'stuck' in restricted controls, so
    ///     a safeword is still effective.
    /// </summary>
    public void RemoveHypnoEffect(UserData enactor, bool giveAchievements, bool fromPluginDisposal = false)
    {
        _overlay.RemoveHypnoEffect(enactor.UID, giveAchievements, fromPluginDisposal);
        _mediator.Publish(new HcStateCacheChanged());
        _logger.LogInformation($"[{enactor.AliasOrUID}] Removed your Hypnotic Effect!");
    }

    public void EnableLockedFollow(UserData enactor)
    {
        if (!_kinksters.TryGetKinkster(enactor, out var kinkster))
            throw new Exception($"Failed to get Kinkster for UID: {enactor.UID} for Locked Follow!");

        _logger.LogInformation($"[{kinkster.GetNickAliasOrUid()}] Enabled your LockedFollowing state!", LoggerType.HardcoreMovement);
        // Cache the movement mode.
        _cachedPlayerMoveMode = GameConfig.UiControl.GetBool("MoveMode") ? MovementMode.Legacy : MovementMode.Standard;
        _logger.LogDebug($"Cached Player Movement Mode: {_cachedPlayerMoveMode}", LoggerType.HardcoreMovement);
        // perform the task collection for initialization.
        _hcTasks.CreateCollection("Locked Follow Startup", new(HcTaskControl.MustFollow | HcTaskControl.BlockAllKeys))
            .Add(new HardcoreTask(() => GameConfig.UiControl.Set("MoveMode", (uint)MovementMode.Legacy)))
            .Add(new HardcoreTask(_movement.RestartTimeoutTracker))
            .Add(new HardcoreTask(() => HcCommonTaskFuncs.TargetNode(() => kinkster.PlayerAddress)))
            .Add(new HardcoreTask(HcTaskUtils.FollowTarget))
            .Add(new HardcoreTask(() => _mediator.Publish(new HcStateCacheChanged())))
            .Enqueue();
        GagspeakEventManager.AchievementEvent(UnlocksEvent.HardcoreAction, HcAttribute.Follow, true, enactor, MainHub.UID);
    }

    public void DisableLockedFollow(UserData enactor, bool giveAchievements)
    {
        _logger.LogInformation($"[{enactor.AliasOrUID}] Disabled your LockedFollowing state.", LoggerType.HardcoreMovement);

        // Reset movement mode and timeout trackers, and update the cache.
        _hcTasks.RemoveIfPresent("Locked Follow Startup");
        _movement.ResetTimeoutTracker();
        if (_cachedPlayerMoveMode != MovementMode.NotSet)
        {
            GameConfig.UiControl.Set("MoveMode", (uint)_cachedPlayerMoveMode); //This will revert control mode to standard if it ever gets MovementMode.NotSet.
            _logger.LogDebug($"Restored Player Movement Mode: {_cachedPlayerMoveMode}", LoggerType.HardcoreMovement);
        }
        _cachedPlayerMoveMode = MovementMode.NotSet;
        _mediator.Publish(new HcStateCacheChanged());

        if (giveAchievements)
            GagspeakEventManager.AchievementEvent(UnlocksEvent.HardcoreAction, HcAttribute.Follow, false, enactor, MainHub.UID);
    }

    public void EnableLockedEmote(UserData enactor)
    {
        if (!_kinksters.TryGetKinkster(enactor, out var kinkster))
            throw new Exception($"Failed to get Kinkster for UID: {enactor.UID} for Locked Emote!");

        _logger.LogInformation($"[{enactor.AliasOrUID}] Enabled your LockedFollowing state!", LoggerType.HardcoreMovement);
        _hcTasks.CreateCollection("Perform LockedEmote", new(HcTaskControl.BlockAllKeys | HcTaskControl.InRequiredTurnTask))
            .Add(new HardcoreTask(GagspeakEx.IsPlayerFullyLoaded))
            .Add(_hcTasks.CreateBranch(() => kinkster.IsTargetable, "TargetIfVisible")
                .SetTrueTask(new HardcoreTask(() => HcCommonTaskFuncs.TargetNode(() => kinkster.PlayerAddress)))
                .AsBranch())
            .Add(new HardcoreTask(() => HcCommonTaskFuncs.PerformExpectedEmote(ClientData.Hardcore!.EmoteId, ClientData.Hardcore.EmoteCyclePose)))
            .Add(new HardcoreTask(() => _mediator.Publish(new HcStateCacheChanged())))
            .Enqueue();

        GagspeakEventManager.AchievementEvent(UnlocksEvent.HardcoreAction, HcAttribute.EmoteState, true, enactor, MainHub.UID);
    }

    public void UpdateLockedEmote(UserData enactor)
    {
        if (!_kinksters.TryGetKinkster(enactor, out var kinkster))
            throw new Exception($"Failed to get Kinkster for UID: {enactor.UID} for Locked Emote Update!");

        _logger.LogInformation($"[{kinkster.GetNickAliasOrUid()}] Updated your LockedFollowing state!", LoggerType.HardcoreMovement);
        _hcTasks.CreateCollection("ForcePerformInitialEmote", new(HcTaskControl.BlockAllKeys | HcTaskControl.InRequiredTurnTask))
            .Add(new HardcoreTask(GagspeakEx.IsPlayerFullyLoaded))
            .Add(_hcTasks.CreateBranch(() => kinkster.IsTargetable, "TargetIfVisible")
                .SetTrueTask(new HardcoreTask(() => HcCommonTaskFuncs.TargetNode(() => kinkster.PlayerAddress)))
                .AsBranch())
            .Add(new HardcoreTask(() => HcCommonTaskFuncs.PerformExpectedEmote(ClientData.Hardcore!.EmoteId, ClientData.Hardcore.EmoteCyclePose)))
            .Add(new HardcoreTask(() => _mediator.Publish(new HcStateCacheChanged())))
            .Enqueue();
    }

    public void DisableLockedEmote(UserData enactor, bool giveAchievements)
    {
        _logger.LogInformation($"[{enactor.AliasOrUID}] Disabled your LockedEmote state!", LoggerType.HardcoreMovement);
        // abort the task if running still, or remove it from the queue.
        _hcTasks.RemoveIfPresent("Perform LockedEmote");
        _mediator.Publish(new HcStateCacheChanged());

        if (giveAchievements)
            GagspeakEventManager.AchievementEvent(UnlocksEvent.HardcoreAction, HcAttribute.EmoteState, false, enactor, MainHub.UID);
    }

    public void EnableConfinement(UserData enactor, AddressBookEntry? address = null)
    {
        _logger.LogInformation($"[{enactor.AliasOrUID}] Enabled your IndoorConfinement!", LoggerType.HardcoreMovement);

        // FromHardcoreStatus never returns null, so only validity separates "addressed" from nearest-node.
        var useLifestream = address is { IsUsable: true } && IpcCallerLifestream.APIAvailable;
        var taskCtrlFlags = HcTaskControl.LockThirdPerson | HcTaskControl.BlockAllKeys | HcTaskControl.DoConfinementPrompts;
        if (useLifestream) taskCtrlFlags |= HcTaskControl.InLifestreamTask;

        var roomNumber = address is { PropertyType: PropertyType.Apartment } ? address.Apartment : int.MaxValue;
        _travelStallTick = Environment.TickCount64;

        _logger.LogDebug($"Confining to {(address is null ? "<none>" : HcConfinement.DescribeTarget(address))} from " +
            $"{PlayerData.CurrentWorldName} | usable={address?.IsUsable} lifestream={useLifestream} " +
            $"atProperty={HcConfinement.AtConfinedProperty(address)}", LoggerType.HardcoreMovement);

        // enqueue the task collection based on if we are doing lifestream of not.
        Svc.Framework.RunOnFrameworkThread(() =>
        {
            _hcTasks.CreateCollection(ConfinementTaskName, HcTaskConfiguration.Branch with {  Flags = taskCtrlFlags })
                .Add(_hcTasks.CreateBranch(() => useLifestream, "LifestreamTravelTask", HcTaskConfiguration.Branch)
                    // A cross-DC hop is a lobby round trip, so the stall watchdog does the real bailing.
                    .SetTrueTask(_hcTasks.CreateGroup("TravelTaskGroup", HcTaskConfiguration.Default with { TimeoutAt = 900000 })
                        .Add(GagspeakEx.IsPlayerFullyLoaded)
                        .Add(() => BeginConfinementTravel(address!))
                        .Add(() => AwaitConfinementArrival(address!))
                        .AsGroup())
                    .AsBranch())
                // Need to find a way to delay this or it skips to the movement operations before we begin zoning.
                // It still works, but is just something of note.
                .Add(new HardcoreTask(GagspeakEx.IsPlayerFullyLoaded))
                // Everything past here walks us through a door. Inner timeouts mean a failed travel
                // ADVANCES here rather than aborting, so without this check it enters a stranger's house.
                .Add(_hcTasks.CreateBranch(() => HcConfinement.CanApproachHousing(address), "ConfinementWorldGate", HcTaskConfiguration.Branch)
                    .SetTrueTask(_hcTasks.CreateCollection("ArriveAtConfinement", HcTaskConfiguration.Branch)
                        .Add(_hcTasks.CreateBranch(() => useLifestream && HcApproachNearestHousing.AtHouseButMustBeCloser(), "Close Gap For Arrival")
                            .SetTrueTask(new HardcoreTask(HcApproachNearestHousing.MoveToAcceptableRange, HcTaskConfiguration.Rapid with { OnEnd = () => StaticDetours.MoveOverrides.Disable() }))
                            .AsBranch())
                        .Add(_hcTasks.CreateBranch(HcTaskUtils.IsOutside, "AppraochNearestNode", HcTaskConfiguration.Short)
                            .SetTrueTask(HcApproachNearestHousing.GetTaskCollection(_hcTasks, roomNumber))
                            .AsBranch())
                        .AsCollection())
                    .SetFalseTask(new HardcoreTask(() => WarnWrongWorld(address), "WarnWrongWorld", HcTaskConfiguration.Quick))
                    .AsBranch())
                .Add(new HardcoreTask(() => _mediator.Publish(new HcStateCacheChanged()), HcTaskConfiguration.Quick))
                .Enqueue();
        });
        _logger.LogDebug($"Enqueued Hardcore Task Stack for Indoor Confinement!", LoggerType.HardcoreMovement);
        GagspeakEventManager.AchievementEvent(UnlocksEvent.HardcoreAction, HcAttribute.Confinement, true, enactor, MainHub.UID);
    }

    /// <summary>
    ///     Travels only when needed. A cross-DC hop logs us out, so the reconnect replays confinement
    ///     and re-enters here mid-trip; asking twice makes Lifestream quick-travel away and walk back.
    /// </summary>
    private bool? BeginConfinementTravel(AddressBookEntry addr)
    {
        // Already travelling, including a trip we re-attached to after a cross-DC relog.
        if (_ipc.IsCurrentlyBusy())
            return true;

        // Right world alone is not enough; the wrong house on the right world still needs a trip.
        if (HcConfinement.AtConfinedProperty(addr))
            return true;

        // Lifestream builds its world lists on login, so an early miss just means "not ready yet".
        if (!_ipc.CanTravelToWorld(addr.World))
        {
            if (NodeThrottler.Throttle("Confinement.UnreachableWorld", 10000))
                _logger.LogWarning($"Lifestream cannot reach {HcConfinement.WorldName(addr.World)} right now " +
                    $"(api={IpcCallerLifestream.APIAvailable}, worldTravelApi={IpcCallerLifestream.WorldTravelApi}). Retrying.");
            return false;
        }

        if (!NodeThrottler.Throttle("Confinement.GoToAddress", 3000))
            return false;

        _logger.LogInformation($"Requesting Lifestream travel to {HcConfinement.DescribeTarget(addr)}", LoggerType.HardcoreMovement);
        _ipc.GoToAddress(addr.AsTuple());
        _travelStallTick = Environment.TickCount64;
        // Never completes here; the IsCurrentlyBusy check above is what ends this step.
        return false;
    }

    /// <summary> Waits on arrival, not idleness: GoTo silently no-ops for an unreachable world. </summary>
    private bool? AwaitConfinementArrival(AddressBookEntry addr)
    {
        if (_ipc.IsCurrentlyBusy() || !GagspeakEx.IsPlayerFullyLoaded())
        {
            _travelStallTick = Environment.TickCount64;
            return false;
        }

        if (HcConfinement.OnConfinedWorld(addr))
            return true;

        // Idle, loaded, still on the wrong world. Fail the group rather than grind on it.
        if (Environment.TickCount64 - _travelStallTick > TravelStallTimeoutMs)
        {
            _logger.LogWarning($"Lifestream idled without reaching {HcConfinement.WorldName(addr.World)}. Giving up.");
            return null;
        }

        return false;
    }

    private bool WarnWrongWorld(AddressBookEntry? addr)
    {
        if (addr is null)
            return true;

        var target = HcConfinement.WorldName(addr.World);
        _logger.LogWarning($"Confinement targets {HcConfinement.DescribeTarget(addr)} but we are on " +
            $"{PlayerData.CurrentWorldName}. Not approaching any housing entrance.");
        _mediator.Publish(new NotificationMessage("Indoor Confinement",
            $"Confinement engages once you are on {target}.", NotificationType.Warning));
        return true;
    }

    public void DisableConfinement(UserData enactor, bool giveAchievements)
    {
        _logger.LogInformation($"[{enactor.AliasOrUID}] Disabled your Indoor Confinement state!", LoggerType.HardcoreMovement);

        _hcTasks.RemoveIfPresent(ConfinementTaskName);
        _hcTasks.RemoveIfPresent(HcApproachNearestHousing.CollectionName);
        _mediator.Publish(new HcStateCacheChanged());

        if (giveAchievements)
            GagspeakEventManager.AchievementEvent(UnlocksEvent.HardcoreAction, HcAttribute.Confinement, false, enactor, MainHub.UID);
    }

    public void EnableImprisonment(UserData enactor)
    {
        // if the address is null, fallback to nearestNode behavior.
        _logger.LogInformation($"[{enactor.AliasOrUID}] Enabled your Imprisonment!", LoggerType.HardcoreMovement);
        // Calling this will begin the imprisonment process.
        _mediator.Publish(new HcStateCacheChanged());

        GagspeakEventManager.AchievementEvent(UnlocksEvent.HardcoreAction, HcAttribute.Imprisonment, true, enactor, MainHub.UID);
    }

    public void UpdateImprisonment(UserData enactor)
    {
        // if the address is null, fallback to nearestNode behavior.
        _logger.LogInformation($"[{enactor.AliasOrUID}] Updated your Imprisonment!", LoggerType.HardcoreMovement);
        // Calling this will begin the imprisonment process.
        _mediator.Publish(new HcStateCacheChanged());
    }

    public void DisableImprisonment(UserData enactor, bool giveAchievements)
    {
        _logger.LogInformation($"[{enactor.AliasOrUID}] Disabled your Imprisonment state!", LoggerType.HardcoreMovement);
        // nothing was really pushed out to the hardcore task manager, so nothing to disable.
        _mediator.Publish(new HcStateCacheChanged());
        if (giveAchievements)
            GagspeakEventManager.AchievementEvent(UnlocksEvent.HardcoreAction, HcAttribute.Imprisonment, false, enactor, MainHub.UID);
    }

    public void EnableHiddenChatBoxes(UserData enactor)
    {
        if (!_kinksters.TryGetKinkster(enactor, out var kinkster))
            throw new Bagagwa($"Failed to get Kinkster for UID: {enactor.UID} for Hidden Chat Boxes!");

        AddonChatLog.SetChatPanelVisibility(false);
        _logger.LogInformation($"[{kinkster.GetNickAliasOrUid()}] Enabled your HiddenChatBoxes state!", LoggerType.HardcoreActions);
        
        _mediator.Publish(new HcStateCacheChanged());
        GagspeakEventManager.AchievementEvent(UnlocksEvent.HardcoreAction, HcAttribute.HiddenChatBox, true, enactor, MainHub.UID);
    }

    /// <summary>
    ///     It is possible that the removal effect is triggered on a safeword or natural timer falloff. <para />
    /// 
    ///     If the server it down or the change cannot be processed, we want to still remove the player controls 
    ///     client-side, but not invoke the achievements for the change. <para />
    ///     
    ///     This way, on reconnection, the hardcore state will be reapplied, timer will immediately expire, and
    ///     they will get the achievement then. It also ensures they are not 'stuck' in restricted controls, so
    ///     a safeword is still effective.
    /// </summary>
    public void DisableHiddenChatBoxes(UserData enactor, bool giveAchievements)
    {
        _logger.LogInformation($"[{enactor.AliasOrUID}] Disabled your HiddenChatBoxes state!", LoggerType.HardcoreActions);
        AddonChatLog.SetChatPanelVisibility(true);

        _mediator.Publish(new HcStateCacheChanged());
        if (giveAchievements)
            GagspeakEventManager.AchievementEvent(UnlocksEvent.HardcoreAction, HcAttribute.HiddenChatBox, false, enactor, MainHub.UID);
    }

    public void HideChatInputVisibility(UserData enactor)
    {
        if (!_kinksters.TryGetKinkster(enactor, out var kinkster))
            throw new Bagagwa($"Failed to get Kinkster for UID: {enactor.UID} for Hidden Chat Input!");

        AddonChatLog.SetChatInputVisibility(false);
        _logger.LogInformation($"[{kinkster.GetNickAliasOrUid()}] concealed your ChatInput visibility!", LoggerType.HardcoreActions);

        _mediator.Publish(new HcStateCacheChanged());
        GagspeakEventManager.AchievementEvent(UnlocksEvent.HardcoreAction, HcAttribute.HiddenChatInput, true, enactor, MainHub.UID);
    }

    /// <summary>
    ///     It is possible that the removal effect is triggered on a safeword or natural timer falloff. <para />
    /// 
    ///     If the server it down or the change cannot be processed, we want to still remove the player controls 
    ///     client-side, but not invoke the achievements for the change. <para />
    ///     
    ///     This way, on reconnection, the hardcore state will be reapplied, timer will immediately expire, and
    ///     they will get the achievement then. It also ensures they are not 'stuck' in restricted controls, so
    ///     a safeword is still effective.
    /// </summary>
    public void RestoreChatInputVisibility(UserData enactor, bool giveAchievements)
    {
        AddonChatLog.SetChatInputVisibility(true);
        _logger.LogInformation($"[{enactor.AliasOrUID}] restored your ChatInput Visibility!", LoggerType.HardcoreActions);

        _mediator.Publish(new HcStateCacheChanged());
        if (giveAchievements)
            GagspeakEventManager.AchievementEvent(UnlocksEvent.HardcoreAction, HcAttribute.HiddenChatInput, false, enactor, MainHub.UID);
    }

    public void BlockChatInput(UserData enactor)
    {
        if (!_kinksters.TryGetKinkster(enactor, out var kinkster))
            throw new Bagagwa($"Failed to get Kinkster for UID: {enactor.UID} for Blocked Chat Input!");
        
        _logger.LogInformation($"[{kinkster.GetNickAliasOrUid()}] Enabled your BlockedChatInput state!", LoggerType.HardcoreActions);
        
        _mediator.Publish(new HcStateCacheChanged());
        GagspeakEventManager.AchievementEvent(UnlocksEvent.HardcoreAction, HcAttribute.BlockedChatInput, true, enactor, MainHub.UID);
    }

    /// <summary>
    ///     It is possible that the removal effect is triggered on a safeword or natural timer falloff. <para />
    /// 
    ///     If the server it down or the change cannot be processed, we want to still remove the player controls 
    ///     client-side, but not invoke the achievements for the change. <para />
    ///     
    ///     This way, on reconnection, the hardcore state will be reapplied, timer will immediately expire, and
    ///     they will get the achievement then. It also ensures they are not 'stuck' in restricted controls, so
    ///     a safeword is still effective.
    /// </summary>
    public void UnblockChatInput(UserData enactor, bool giveAchievements)
    {
        _logger.LogInformation($"[{enactor.AliasOrUID}] Disabled your BlockedChatInput state!", LoggerType.HardcoreActions);

        _mediator.Publish(new HcStateCacheChanged());
        if (giveAchievements)
            GagspeakEventManager.AchievementEvent(UnlocksEvent.HardcoreAction, HcAttribute.BlockedChatInput, false, enactor, MainHub.UID);
    }
}
