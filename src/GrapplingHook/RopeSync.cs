using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// One rope event between machines: a player attached (with the anchor, the
/// flight direction for the hook's pose, the hook tier and the rope length),
/// the length changed, or the rope let go. The grappling player's game sends it
/// to the server; the server keeps the current ropes and passes each event on
/// to every other player. The game finds this class by reflection, and a
/// joining client takes its id from the server, so every machine must run the
/// same GrapplingHook.dll.
/// </summary>
public class NetPackageGrappleRope : NetPackage
{
    internal const byte Attach = 1, Length = 2, Detach = 3;

    internal byte kind;
    internal int entityId;
    internal byte tier;
    internal Vector3 anchor;
    internal Vector3 direction;
    internal float length;

    public NetPackageGrappleRope Setup(byte _kind, int _entityId, int _tier, Vector3 _anchor, Vector3 _direction, float _length)
    {
        kind = _kind;
        entityId = _entityId;
        tier = (byte)Mathf.Clamp(_tier, 0, HookMod.TierNames.Length - 1);
        anchor = _anchor;
        direction = _direction;
        length = _length;
        return this;
    }

    public override void read(PooledBinaryReader _br)
    {
        kind = _br.ReadByte();
        entityId = _br.ReadInt32();
        tier = _br.ReadByte();
        anchor = new Vector3(_br.ReadSingle(), _br.ReadSingle(), _br.ReadSingle());
        direction = new Vector3(_br.ReadSingle(), _br.ReadSingle(), _br.ReadSingle());
        length = _br.ReadSingle();
    }

    public override void write(PooledBinaryWriter _bw)
    {
        base.write(_bw);
        // ReadWrite(x) is Write(x); calling Write directly does not compile
        // against net48, whose mscorlib lacks the ReadOnlySpan overloads'
        // parameter type, and ReadWrite has no such overloads.
        _bw.ReadWrite(kind);
        _bw.ReadWrite(entityId);
        _bw.ReadWrite(tier);
        _bw.ReadWrite(anchor.x); _bw.ReadWrite(anchor.y); _bw.ReadWrite(anchor.z);
        _bw.ReadWrite(direction.x); _bw.ReadWrite(direction.y); _bw.ReadWrite(direction.z);
        _bw.ReadWrite(length);
    }

    public override void ProcessPackage(World _world, GameManager _callbacks)
    {
        if (_world == null) return;
        try
        {
            if (!RopeSync.IsValid(this)) return;
            ConnectionManager cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
            if (cm != null && cm.IsServer)
            {
                // From a client: it may only speak for its own player.
                if (!ValidEntityIdForSender(entityId, false)) return;
                RopeSync.ServerReceived(this);
            }
            else RopeSync.Show(this);
        }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Rope sync: message failed: " + ex.Message); }
    }
}

/// <summary>
/// Multiplayer rope sharing (PLAN.md section 10, Stage 2). Three roles, any of
/// which a machine can have: the grappling player's game reports its own rope;
/// the server (dedicated, or the host's game) keeps every current rope and
/// relays the events; every game with a screen draws the other players' ropes.
/// In single-player nothing is sent.
/// </summary>
internal static class RopeSync
{
    private struct RopeInfo
    {
        internal int Tier;
        internal Vector3 Anchor;
        internal Vector3 Direction;
        internal float Length;
    }

    private const float LengthSendStep = 0.1f;
    private const float LengthSendInterval = 0.2f;

    // Server: the current rope of every attached player, the host's included.
    private static readonly Dictionary<int, RopeInfo> serverRopes = new Dictionary<int, RopeInfo>();
    // Any game with a screen: the other players' ropes being drawn.
    private static readonly Dictionary<int, RemoteRope> shown = new Dictionary<int, RemoteRope>();
    private static GameObject driver;
    private static float lastPrune;

    // The local player's rope as last reported.
    private static float sentLength = -1f;
    private static float sentTime;

    private static ConnectionManager Connection { get { return SingletonMonoBehaviour<ConnectionManager>.Instance; } }

    private static bool InWorld
    {
        get { return Connection != null && GameManager.Instance != null && GameManager.Instance.World != null; }
    }

    internal static bool IsValid(NetPackageGrappleRope p)
    {
        if (p.kind < NetPackageGrappleRope.Attach || p.kind > NetPackageGrappleRope.Detach) return false;
        if (p.kind == NetPackageGrappleRope.Detach) return true;
        return Finite(p.anchor) && Finite(p.direction) && !float.IsNaN(p.length) && !float.IsInfinity(p.length) &&
            p.length >= 0f && p.length < 10000f;
    }

    private static bool Finite(Vector3 v)
    {
        return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
            !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
    }

    private static NetPackageGrappleRope Package(byte kind, int entityId, int tier, Vector3 anchor, Vector3 direction, float length)
    {
        return NetPackageManager.GetPackage<NetPackageGrappleRope>().Setup(kind, entityId, tier, anchor, direction, length);
    }

    // ---- The grappling player's own rope ----------------------------------

    internal static void LocalAttached(GrappleState s)
    {
        if (s == null || s.Player == null) return;
        sentLength = s.Length;
        sentTime = Time.time;
        SendLocal(NetPackageGrappleRope.Attach, s);
    }

    /// <summary>Called each player update while attached; reports reeling a few times a second.</summary>
    internal static void LocalTick(GrappleState s)
    {
        if (s == null || s.Player == null || sentLength < 0f) return;
        if (Mathf.Abs(s.Length - sentLength) < LengthSendStep || Time.time - sentTime < LengthSendInterval) return;
        sentLength = s.Length;
        sentTime = Time.time;
        SendLocal(NetPackageGrappleRope.Length, s);
    }

    internal static void LocalDetached(GrappleState s)
    {
        sentLength = -1f;
        if (s == null || s.Player == null) return;
        SendLocal(NetPackageGrappleRope.Detach, s);
    }

    private static void SendLocal(byte kind, GrappleState s)
    {
        try
        {
            if (!InWorld) return;
            int id = s.Player.entityId;
            ConnectionManager cm = Connection;
            if (cm.IsServer)
            {
                // Single-player or the host: this game is the server. Always
                // record the rope, so a player who joins while it is out gets
                // it; send only when someone is connected (IsSinglePlayer means
                // "server with no clients", which includes a host alone).
                ServerApply(kind, id, s.Tier, s.Anchor, s.AnchorDirection, s.Length);
                if (cm.IsSinglePlayer) return;
                cm.SendPackage(Package(kind, id, s.Tier, s.Anchor, s.AnchorDirection, s.Length),
                    false, -1, -1, -1, null, 192, false);
            }
            else cm.SendToServer(Package(kind, id, s.Tier, s.Anchor, s.AnchorDirection, s.Length), false);
            if (kind != NetPackageGrappleRope.Length)
                HookMod.Verbose("[GrapplingHook] Rope sync: sent " + (kind == NetPackageGrappleRope.Attach ? "attach" : "detach") +
                    (cm.IsServer ? " to the other players." : " to the server."));
        }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Rope sync: send failed: " + ex.Message); }
    }

    // ---- The server -------------------------------------------------------

    internal static void ServerReceived(NetPackageGrappleRope p)
    {
        ServerApply(p.kind, p.entityId, p.tier, p.anchor, p.direction, p.length);
        // Everyone but the sender.
        Connection.SendPackage(Package(p.kind, p.entityId, p.tier, p.anchor, p.direction, p.length),
            false, -1, p.entityId, -1, null, 192, false);
        if (p.kind != NetPackageGrappleRope.Length)
            HookMod.Verbose("[GrapplingHook] Rope sync: player " + p.entityId + " " +
                (p.kind == NetPackageGrappleRope.Attach ? "attached a " + HookMod.TierNames[p.tier] + " hook" : "let go") +
                "; passed on to the other players.");
        // The host also draws it.
        if (!GameManager.IsDedicatedServer) Show(p);
    }

    private static void ServerApply(byte kind, int id, int tier, Vector3 anchor, Vector3 direction, float length)
    {
        RopeInfo info;
        if (kind == NetPackageGrappleRope.Attach)
            serverRopes[id] = new RopeInfo { Tier = tier, Anchor = anchor, Direction = direction, Length = length };
        else if (kind == NetPackageGrappleRope.Length && serverRopes.TryGetValue(id, out info))
        {
            info.Length = length;
            serverRopes[id] = info;
        }
        else if (kind == NetPackageGrappleRope.Detach) serverRopes.Remove(id);
    }

    /// <summary>A player joined or respawned: send them the ropes already out.</summary>
    internal static void ServerPlayerSpawned(ClientInfo client)
    {
        if (client == null || serverRopes.Count == 0) return;
        int sent = 0;
        foreach (KeyValuePair<int, RopeInfo> rope in serverRopes)
        {
            if (rope.Key == client.entityId) continue;
            RopeInfo r = rope.Value;
            client.SendPackage(Package(NetPackageGrappleRope.Attach, rope.Key, r.Tier, r.Anchor, r.Direction, r.Length));
            sent++;
        }
        if (sent > 0) HookMod.Verbose("[GrapplingHook] Rope sync: sent " + sent + " current rope(s) to " + client.playerName + ".");
    }

    /// <summary>A player's rope ends without word from their game (they left, or their entity is gone).</summary>
    private static void ServerDrop(int id, string why)
    {
        if (!serverRopes.Remove(id)) return;
        Connection.SendPackage(Package(NetPackageGrappleRope.Detach, id, 0, Vector3.zero, Vector3.zero, 0f),
            false, -1, -1, -1, null, 192, false);
        if (!GameManager.IsDedicatedServer) Hide(id);
        HookMod.Verbose("[GrapplingHook] Rope sync: dropped player " + id + "'s rope (" + why + ").");
    }

    internal static void ServerPlayerDisconnected(ClientInfo client)
    {
        if (client != null) ServerDrop(client.entityId, "disconnected");
    }

    /// <summary>Once a second on the server: drop ropes whose player no longer exists.</summary>
    internal static void ServerUpdate()
    {
        if (serverRopes.Count == 0 || Time.time - lastPrune < 1f) return;
        lastPrune = Time.time;
        ConnectionManager cm = Connection;
        World world = GameManager.Instance != null ? GameManager.Instance.World : null;
        if (cm == null || !cm.IsServer || world == null) return;
        List<int> gone = null;
        foreach (int id in serverRopes.Keys)
        {
            Entity entity = world.GetEntity(id);
            if (entity == null || entity.IsDead()) (gone ?? (gone = new List<int>())).Add(id);
        }
        if (gone != null) foreach (int id in gone) ServerDrop(id, "player gone");
    }

    // ---- Drawing other players' ropes -------------------------------------

    internal static void Show(NetPackageGrappleRope p)
    {
        if (GameManager.IsDedicatedServer) return;
        World world = GameManager.Instance != null ? GameManager.Instance.World : null;
        if (world == null || p.entityId == world.GetPrimaryPlayerId()) return;
        RemoteRope rope;
        switch (p.kind)
        {
            case NetPackageGrappleRope.Attach:
                Hide(p.entityId);
                shown[p.entityId] = new RemoteRope(p.entityId, p.tier, p.anchor, p.direction, p.length);
                EnsureDriver();
                HookMod.Verbose("[GrapplingHook] Rope sync: showing player " + p.entityId + "'s " + HookMod.TierNames[p.tier] +
                    " hook at " + p.anchor + ".");
                break;
            case NetPackageGrappleRope.Length:
                if (shown.TryGetValue(p.entityId, out rope)) rope.Length = p.length;
                break;
            case NetPackageGrappleRope.Detach:
                if (Hide(p.entityId)) HookMod.Verbose("[GrapplingHook] Rope sync: player " + p.entityId + " let go.");
                break;
        }
    }

    private static bool Hide(int id)
    {
        RemoteRope rope;
        if (!shown.TryGetValue(id, out rope)) return false;
        rope.Destroy();
        shown.Remove(id);
        return true;
    }

    private static void EnsureDriver()
    {
        if (driver != null) return;
        driver = new GameObject("GrapplingHookRemoteRopes");
        driver.AddComponent<RemoteRopeDriver>();
    }

    internal static void DrawAll()
    {
        if (shown.Count == 0) return;
        World world = GameManager.Instance != null ? GameManager.Instance.World : null;
        if (world == null) return;
        foreach (RemoteRope rope in shown.Values) rope.Draw(world);
    }

    /// <summary>For `gh ropes`: what this machine knows about other ropes.</summary>
    internal static string Describe()
    {
        var text = new System.Text.StringBuilder();
        ConnectionManager cm = Connection;
        text.Append("[GrapplingHook] Rope sync: ").Append(cm == null ? "no connection" :
            cm.IsServer ? (GameManager.IsDedicatedServer ? "dedicated server" : cm.IsSinglePlayer ? "single-player / host alone" : "host") : "client");
        text.Append("; server table ").Append(serverRopes.Count).Append(", drawing ").Append(shown.Count).Append('.');
        foreach (KeyValuePair<int, RopeInfo> rope in serverRopes)
            text.Append("\n  server: player ").Append(rope.Key).Append(' ').Append(HookMod.TierNames[rope.Value.Tier])
                .Append(" anchor ").Append(rope.Value.Anchor).Append(" length ").Append(rope.Value.Length.ToString("F1"));
        foreach (KeyValuePair<int, RemoteRope> rope in shown)
            text.Append("\n  drawing: player ").Append(rope.Key).Append(" length ").Append(rope.Value.Length.ToString("F1"));
        return text.ToString();
    }

    /// <summary>Leaving the world: forget every rope.</summary>
    internal static void Reset()
    {
        foreach (RemoteRope rope in shown.Values) rope.Destroy();
        shown.Clear();
        serverRopes.Clear();
        sentLength = -1f;
        if (driver != null) UnityEngine.Object.Destroy(driver);
        driver = null;
    }
}

/// <summary>Another player's rope and anchored hook, drawn from what their game reported.</summary>
internal sealed class RemoteRope
{
    private readonly int entityId;
    private readonly Vector3 anchor;
    private readonly RopeTube tube;
    private readonly GameObject hook;
    private readonly Vector3 hookWorld;
    private readonly Vector3 eyeOffset;
    private readonly bool hasEye;
    internal float Length;

    internal RemoteRope(int entityId, int tier, Vector3 anchor, Vector3 direction, float length)
    {
        this.entityId = entityId;
        this.anchor = anchor;
        Length = length;
        Material material = HookLine.SharedRopeMaterial();
        if (material != null) tube = new RopeTube("GrapplingHookRope_" + entityId, material);

        // The hook prefab's pivot is its tip and it points along +Z, the flight
        // direction; seat the tip just inside the block, as for the local hook.
        GameObject prefab = HookAssets.BoltPrefab(tier);
        Vector3 dir = direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.down;
        if (prefab == null) return;
        hook = UnityEngine.Object.Instantiate(prefab);
        hook.name = "GrapplingHookAnchor_" + entityId;
        foreach (Collider c in hook.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(c);
        hookWorld = anchor + dir * HookLine.HookTipPenetration;
        hook.transform.SetPositionAndRotation(hookWorld - Origin.position, Quaternion.LookRotation(dir));
        Transform eye = FindChild(hook.transform, "RopeAttach");
        hasEye = eye != null;
        if (hasEye) eyeOffset = eye.position - hook.transform.position;
    }

    internal void Draw(World world)
    {
        Vector3 origin = Origin.position;
        if (hook != null) hook.transform.position = hookWorld - origin;
        if (tube == null) return;
        EntityAlive player = world.GetEntity(entityId) as EntityAlive;
        // Out of view range or dead: keep the hook, hide the rope.
        if (player == null || player.IsDead() || player.transform == null) { tube.SetVisible(false); return; }
        tube.SetVisible(true);
        Vector3 start = player.transform.position + Grapple.AttachOffset(player);
        Vector3 end = (hasEye ? hookWorld + eyeOffset : anchor) - origin;
        tube.Draw(start, end, Length - Vector3.Distance(start + origin, anchor));
    }

    internal void Destroy()
    {
        if (tube != null) tube.Destroy();
        if (hook != null) UnityEngine.Object.Destroy(hook);
    }

    private static Transform FindChild(Transform root, string name)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            if (child.name == name) return child;
        return null;
    }
}

internal sealed class RemoteRopeDriver : MonoBehaviour
{
    private void LateUpdate()
    {
        try { RopeSync.DrawAll(); }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Rope sync: drawing failed: " + ex.Message); }
    }
}

[HarmonyPatch(typeof(GameManager), "PlayerSpawnedInWorld")]
internal static class RopeSyncSpawnPatch
{
    private static void Postfix(ClientInfo __0)
    {
        try { SettingsSync.SendTo(__0); }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Server settings: send failed: " + ex.Message); }
        try { RopeSync.ServerPlayerSpawned(__0); }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Rope sync: join failed: " + ex.Message); }
    }
}

[HarmonyPatch(typeof(GameManager), "PlayerDisconnected")]
internal static class RopeSyncDisconnectPatch
{
    private static void Prefix(ClientInfo __0)
    {
        try { RopeSync.ServerPlayerDisconnected(__0); }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Rope sync: disconnect failed: " + ex.Message); }
    }
}

[HarmonyPatch(typeof(GameManager), "Update")]
internal static class RopeSyncServerUpdatePatch
{
    private static void Postfix()
    {
        try { RopeSync.ServerUpdate(); }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Rope sync: update failed: " + ex.Message); }
    }
}
