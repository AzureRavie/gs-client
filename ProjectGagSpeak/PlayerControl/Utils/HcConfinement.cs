using CkCommons;
using FFXIVClientStructs.FFXIV.Client.Game;
using GagSpeak.Interop.Helpers;
using Penumbra.GameData.Structs;

namespace GagSpeak;

/// <summary>
///     Location checks for Indoor Confinement. Lifestream's exposed IsHere compares ward and plot
///     but not the world, so plot 12 of Mist on the wrong server passes it. We need our own.
/// </summary>
public static class HcConfinement
{
    /// <summary> Testable form, so it does not need a live LocalPlayer. </summary>
    public static bool OnConfinedWorld(AddressBookEntry? addr, ushort currentWorldId)
        => addr is { HasWorld: true } && addr.World == currentWorldId;

    public static bool OnConfinedWorld(AddressBookEntry? addr)
        => PlayerData.Available && OnConfinedWorld(addr, PlayerData.CurrentWorldId);

    /// <summary> Nearest-node mode has no address to disagree with, so anywhere counts. </summary>
    public static bool CanApproachHousing(AddressBookEntry? addr)
        => addr is not { IsUsable: true } || OnConfinedWorld(addr);

    /// <summary>
    ///     Whether we are on the property confinement names. The right world is not enough, since
    ///     the wrong house on the right world is still the wrong house.
    /// </summary>
    public static unsafe bool AtConfinedProperty(AddressBookEntry? addr)
    {
        if (addr is not { IsUsable: true })
            return true;

        if (!OnConfinedWorld(addr))
            return false;

        var housing = HousingManager.Instance();
        if (housing is null)
            return false;

        // Game reports these zero-based, Lifestream stores them one-based. Outside a housing area
        // they all read negative, which can never match a one-based value minus one.
        if (housing->GetCurrentWard() != addr.Ward - 1)
            return false;

        return addr.PropertyType is PropertyType.Apartment
            ? housing->GetCurrentRoom() == addr.Apartment
            : housing->GetCurrentPlot() == addr.Plot - 1;
    }

    public static string WorldName(ushort worldId)
        => ItemSvc.WorldData.TryGetValue(new WorldId(worldId), out var name) ? name : $"World#{worldId}";

    public static string DescribeTarget(AddressBookEntry addr)
        => addr.PropertyType is PropertyType.Apartment
            ? $"Apartment {addr.Apartment}, Ward {addr.Ward}, {addr.City} on {WorldName(addr.World)}"
            : $"Plot {addr.Plot}, Ward {addr.Ward}, {addr.City} on {WorldName(addr.World)}";
}
