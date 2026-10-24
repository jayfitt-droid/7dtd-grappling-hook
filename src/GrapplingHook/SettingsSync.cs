using System;
using UnityEngine;

/// <summary>
/// The server's settings, sent to each player's game (PLAN.md section 10,
/// Stage 3). Every setting takes effect on the grappling player's own game:
/// the range check, reeling, stamina, the hook's block damage and the fall
/// after a release all run there. So on a server the server's config file
/// decides, and a player's own file applies again once they leave.
/// </summary>
public class NetPackageGrappleSettings : NetPackage
{
    internal float scrapRange, ironRange, steelRange, reelIn, reelOut, blockDamage, staminaUse;
    internal bool fallDamageOnRelease;

    public override NetPackageDirection PackageDirection { get { return NetPackageDirection.ToClient; } }

    internal NetPackageGrappleSettings Setup(HookSettings s)
    {
        scrapRange = s.MaxDistance[0];
        ironRange = s.MaxDistance[1];
        steelRange = s.MaxDistance[2];
        reelIn = s.ReelInSpeed;
        reelOut = s.ReelOutSpeed;
        blockDamage = s.BlockDamage;
        fallDamageOnRelease = s.FallDamageOnRelease;
        staminaUse = s.StaminaUse;
        return this;
    }

    public override void read(PooledBinaryReader _br)
    {
        scrapRange = _br.ReadSingle();
        ironRange = _br.ReadSingle();
        steelRange = _br.ReadSingle();
        reelIn = _br.ReadSingle();
        reelOut = _br.ReadSingle();
        blockDamage = _br.ReadSingle();
        fallDamageOnRelease = _br.ReadBoolean();
        staminaUse = _br.ReadSingle();
    }

    public override void write(PooledBinaryWriter _bw)
    {
        base.write(_bw);
        // ReadWrite(x) is Write(x); see NetPackageGrappleRope.write.
        _bw.ReadWrite(scrapRange);
        _bw.ReadWrite(ironRange);
        _bw.ReadWrite(steelRange);
        _bw.ReadWrite(reelIn);
        _bw.ReadWrite(reelOut);
        _bw.ReadWrite(blockDamage);
        _bw.ReadWrite(fallDamageOnRelease);
        _bw.ReadWrite(staminaUse);
    }

    public override void ProcessPackage(World _world, GameManager _callbacks)
    {
        try
        {
            ConnectionManager cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
            if (cm == null || cm.IsServer) return; // the server's own file already applies
            HookMod.ApplyServerSettings(HookSettings.FromNetwork(new[] { scrapRange, ironRange, steelRange },
                reelIn, reelOut, blockDamage, fallDamageOnRelease, staminaUse));
        }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Server settings: message failed: " + ex.Message); }
    }
}

internal static class SettingsSync
{
    private static ConnectionManager Connection { get { return SingletonMonoBehaviour<ConnectionManager>.Instance; } }

    /// <summary>A player joined or respawned: send them the server's settings.</summary>
    internal static void SendTo(ClientInfo client)
    {
        ConnectionManager cm = Connection;
        if (client == null || cm == null || !cm.IsServer) return;
        client.SendPackage(NetPackageManager.GetPackage<NetPackageGrappleSettings>().Setup(HookMod.Settings));
        HookMod.Verbose("[GrapplingHook] Server settings sent to " + client.playerName + ".");
    }

    /// <summary>After `gh reload` on the server: send the new settings to everyone.</summary>
    internal static void SendToAll()
    {
        ConnectionManager cm = Connection;
        if (cm == null || !cm.IsServer || cm.IsSinglePlayer) return;
        cm.SendPackage(NetPackageManager.GetPackage<NetPackageGrappleSettings>().Setup(HookMod.Settings),
            false, -1, -1, -1, null, 192, false);
        HookMod.Verbose("[GrapplingHook] Server settings sent to every player.");
    }

    /// <summary>True on a client connected to a server (not the host, not single-player).</summary>
    internal static bool IsRemoteClient
    {
        get
        {
            ConnectionManager cm = Connection;
            return cm != null && !cm.IsServer && GameManager.Instance != null && GameManager.Instance.World != null;
        }
    }
}
