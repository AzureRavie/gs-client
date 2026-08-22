using Dalamud.Plugin.Ipc;
using Penumbra.GameData.Structs;

namespace GagSpeak.Interop;

public sealed class IpcCallerLifestream : IIpcCaller
{
    // API Version

    // API Events
    private readonly ICallGateSubscriber<object> OnHouseEnterError;

    // API Getters
    private readonly ICallGateSubscriber<AddressBookEntryTuple, bool> GetIsAtAddress;
    private readonly ICallGateSubscriber<bool>                        GetIsBusy;
    private readonly ICallGateSubscriber<List<AddressBookEntryTuple>> GetAddressBookList;
    private readonly ICallGateSubscriber<string, bool>                GetCanVisitSameDC;
    private readonly ICallGateSubscriber<string, bool>                GetCanVisitCrossDC;

    // API Enactors
    // IPC Function Delegates (calls that instruct Lifestream to do something)
    private readonly ICallGateSubscriber<AddressBookEntryTuple, object> TravelToAddress;
    private readonly ICallGateSubscriber<object>                        AbortTask;

    public IpcCallerLifestream()
    {
        OnHouseEnterError = Svc.PluginInterface.GetIpcSubscriber<object>("Lifestream.OnHouseEnterError");

        GetIsBusy = Svc.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        GetIsAtAddress = Svc.PluginInterface.GetIpcSubscriber<AddressBookEntryTuple, bool>("Lifestream.IsHere");

        TravelToAddress = Svc.PluginInterface.GetIpcSubscriber<AddressBookEntryTuple, object>("Lifestream.GoToHousingAddress");
        AbortTask = Svc.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");

        GetAddressBookList = Svc.PluginInterface.GetIpcSubscriber<List<AddressBookEntryTuple>>("Lifestream.GetAddressBookEntries");

        GetCanVisitSameDC = Svc.PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitSameDC");
        GetCanVisitCrossDC = Svc.PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitCrossDC");

        // subscribe to event.
        OnHouseEnterError.Subscribe(OnErrorEnteringHouse);

        CheckAPI();
    }

    public static bool APIAvailable { get; private set; } = false;
    public static bool WorldTravelApi { get; private set; } = false;

    public void CheckAPI()
    {
        var lifestreamPlugin = Svc.PluginInterface.InstalledPlugins.FirstOrDefault(p => string.Equals(p.InternalName, "lifestream", StringComparison.OrdinalIgnoreCase));
        if (lifestreamPlugin is null)
        {
            APIAvailable = false;
            WorldTravelApi = false;
            return;
        }
        // lifestream is installed, so see if it is on.
        APIAvailable = lifestreamPlugin.IsLoaded ? true : false;
        if (!APIAvailable)
        {
            WorldTravelApi = false;
            return;
        }

        // Probe once. An empty name is safe; CanVisitSameDC is only a Contains() over a string[].
        if (!WorldTravelApi)
        {
            try { GetCanVisitSameDC.InvokeFunc(string.Empty); WorldTravelApi = true; }
            catch (Bagagwa) { WorldTravelApi = false; }
        }
    }

    public void Dispose() 
    { }

    private void OnErrorEnteringHouse()
    {
        Svc.Logger.Warning("Lifestream reported an error entering the house. You may be stuck outside your house.");
    }

    /// <summary> Checks if we are at the desired address. </summary>
    /// <remarks> Wraps Lifestream.IsHere, which compares ward/city/plot but NOT the world. </remarks>
    public bool IsAtAddress(AddressBookEntryTuple address)
    {
        if (!APIAvailable)
            return false;

        return GetIsAtAddress.InvokeFunc(address);
    }

    /// <summary> Checks if we are busy with a task. </summary>
    public bool IsCurrentlyBusy()
    {
        if (!APIAvailable)
            return false;

        return GetIsBusy.InvokeFunc();
    }

    /// <summary> Aborts the current task. </summary>
    public void AbortCurrentTask()
    {
        if (!APIAvailable)
            return;

        AbortTask.InvokeAction();
    }

    /// <summary> Attempts to go to the specified address. </summary>
    public void GoToAddress(AddressBookEntryTuple address)
    {
        if (!APIAvailable)
            return;

        // invoke the action for the address.
        TravelToAddress.InvokeAction(address);
    }

    /// <summary>
    ///     If Lifestream can reach this world, by same-DC visit or DC travel. Its lists build on
    ///     login, so an early false means "not ready yet" rather than "never".
    /// </summary>
    public bool CanTravelToWorld(ushort worldId)
    {
        if (!APIAvailable || !WorldTravelApi)
            return false;

        if (!ItemSvc.WorldData.TryGetValue(new WorldId(worldId), out var name) || string.IsNullOrEmpty(name))
            return false;

        try
        {
            return GetCanVisitCrossDC.InvokeFunc(name) || GetCanVisitSameDC.InvokeFunc(name);
        }
        catch (Bagagwa ex)
        {
            Svc.Logger.Warning($"Lifestream CanVisit* failed for [{name}]: {ex.Message}");
            WorldTravelApi = false;
            return false;
        }
    }

    /// <summary> Gets the address book list. </summary>
    public List<AddressBookEntryTuple>? GetAddressList()
    {
        if (!APIAvailable)
            return null;

        return GetAddressBookList.InvokeFunc();
    }
}
