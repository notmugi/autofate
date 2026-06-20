using System.Linq;
using AutoFates.IPC;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace AutoFates.Core;

/// <summary>
/// Gets the player into a target territory. Prefers Lifestream (handles aethernet, world travel,
/// housing, etc.) when available; otherwise falls back to the game's Telepo to the nearest
/// aetheryte in the destination territory.
/// </summary>
public static unsafe class Teleporter
{
    public static bool IsInTerritory(uint territoryId) => Svc.ClientState.TerritoryType == territoryId;

    public static bool IsBusy()
    {
        if (LifestreamIPC.IsInstalled && LifestreamIPC.IsBusy()) return true;
        return Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
            || Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51]
            || Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting];
    }

    /// <summary>Find the primary aetheryte row id for a territory (the main one, lowest order).</summary>
    public static uint FindAetheryteForTerritory(uint territoryId)
    {
        try
        {
            var sheet = Svc.Data.GetExcelSheet<Aetheryte>();
            var match = sheet
                .Where(a => a.RowId != 0 && a.IsAetheryte && a.Territory.RowId == territoryId)
                .OrderBy(a => a.Order)
                .FirstOrDefault();
            return match.RowId;
        }
        catch { return 0; }
    }

    public static string? FindAetheryteName(uint territoryId)
    {
        try
        {
            var sheet = Svc.Data.GetExcelSheet<Aetheryte>();
            var match = sheet
                .Where(a => a.RowId != 0 && a.IsAetheryte && a.Territory.RowId == territoryId)
                .OrderBy(a => a.Order)
                .FirstOrDefault();
            return match.PlaceName.ValueNullable?.Name.ToString();
        }
        catch { return null; }
    }

    /// <summary>
    /// Initiate travel to a territory. Returns true if we are already there. Otherwise issues a
    /// teleport (Lifestream or Telepo) and returns false; call again until <see cref="IsInTerritory"/>.
    /// </summary>
    public static bool TravelToTerritory(Configuration c, uint territoryId)
    {
        if (IsInTerritory(territoryId)) return true;
        if (IsBusy()) return false;
        if (!EzThrottler.Throttle("AF_Teleport", 8000)) return false;

        // Prefer Lifestream when enabled + installed.
        if (c.UseLifestream && LifestreamIPC.IsInstalled)
        {
            var name = FindAetheryteName(territoryId);
            if (!string.IsNullOrEmpty(name))
            {
                Svc.Log.Debug($"[Teleporter] Lifestream -> {name}");
                LifestreamIPC.TeleportToAetheryte(name!);
                return false;
            }
        }

        // Fallback: Telepo to the aetheryte id.
        var aetheryteId = FindAetheryteForTerritory(territoryId);
        if (aetheryteId == 0)
        {
            if (EzThrottler.Throttle("AF_NoAetheryte", 30000))
                Svc.Log.Warning($"[Teleporter] No aetheryte found for territory {territoryId}.");
            return false;
        }

        try
        {
            var telepo = Telepo.Instance();
            if (telepo != null)
            {
                Svc.Log.Debug($"[Teleporter] Telepo -> aetheryte {aetheryteId}");
                telepo->Teleport(aetheryteId, 0);
            }
        }
        catch (Exception e) { Svc.Log.Verbose($"[Teleporter] Teleport failed: {e.Message}"); }
        return false;
    }

    /// <summary>Run an arbitrary Lifestream command (e.g. go home for chocobo stabling).</summary>
    public static void LifestreamCommand(string command)
    {
        if (LifestreamIPC.IsInstalled)
            LifestreamIPC.ExecuteCommand(command);
        else
            Svc.Log.Warning("[Teleporter] Lifestream not installed; cannot run command: " + command);
    }
}
