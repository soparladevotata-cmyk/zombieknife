using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace ZombieKnifeMenu;

public enum KnifeType
{
    Speed = 1,
    Gravity = 2,
    Knockback = 3,
    Damage = 4,
    Vip = 5
}

public sealed class KnifeSettings
{
    public float SpeedMultiplier { get; set; } = 1.15f;
    public float GravityScale { get; set; } = 0.72f;
    public float KnockbackHorizontal { get; set; } = 420.0f;
    public float KnockbackVertical { get; set; } = 170.0f;
    public float DamageMultiplier { get; set; } = 1.50f;

    // Zombie:Reborn defaults: humans are CT, zombies are T.
    public byte HumanTeam { get; set; } = 3;
    public byte ZombieTeam { get; set; } = 2;

    public int RefreshEveryTicks { get; set; } = 4;

    // VIP Knife requires this permission. @css/root is accepted too.
    public string VipPermission { get; set; } = "@css/vip";

    // Modern CS2 / AnimGraph2 uses weapon VData subclasses.
    // These names must exist in scripts/weapons.vdata inside the mounted addon.
    public string SpeedKnifeSubclass { get; set; } = "weapon_knife_zk_speed";
    public string GravityKnifeSubclass { get; set; } = "weapon_knife_zk_gravity";
    public string KnockbackKnifeSubclass { get; set; } = "weapon_knife_zk_knockback";
    public string DamageKnifeSubclass { get; set; } = "weapon_knife_zk_damage";
    public string VipKnifeSubclass { get; set; } = "weapon_knife_zk_vip";

    // Base CT knife subclass used when reverting.
    public string DefaultKnifeSubclass { get; set; } = "weapon_knife";

    // On-screen Knife Menu (CS2-GameHUD)
    public byte MenuHudChannel { get; set; } = 241;
    public float MenuHudX { get; set; } = -2.8f;
    public float MenuHudY { get; set; } = 1.45f;
    public float MenuHudDistance { get; set; } = 7.0f;
    public int MenuHudFontSize { get; set; } = 30;
    public float MenuHudWorldUnitsPerPixel { get; set; } = 0.0105f;
    public float MenuHudBackgroundHeight { get; set; } = 0.35f;
    public float MenuHudBackgroundWidth { get; set; } = 0.55f;
}


[MinimumApiVersion(80)]
public sealed class ZombieKnifeMenuPlugin : BasePlugin
{
    public override string ModuleName => "Zombie Knife Menu";
    public override string ModuleVersion => "1.9.2";
    public override string ModuleAuthor => "OpenAI";
    public override string ModuleDescription =>
        "CS 1.6-style Knife Menu with custom CS2 weapon models for Zombie:Reborn";

    private KnifeSettings _settings = new();
    private readonly Dictionary<string, KnifeType> _selections = new();
    private readonly HashSet<ulong> _openKnifeMenus = new();

    private string SettingsPath => Path.Combine(ModuleDirectory, "config.json");
    private string SelectionsPath => Path.Combine(ModuleDirectory, "knife_selections.json");

    public override void Load(bool hotReload)
    {
        Directory.CreateDirectory(ModuleDirectory);
        LoadSettings();
        LoadSelections();

        AddCommand("css_knives", "Open Knife Menu", OnKnifeCommand);

        // Intercept the normal CS2 number-key slot commands only while the menu is open.
        AddCommandListener("slot1", (p, c) => HandleMenuKey(p, 1));
        AddCommandListener("slot2", (p, c) => HandleMenuKey(p, 2));
        AddCommandListener("slot3", (p, c) => HandleMenuKey(p, 3));
        AddCommandListener("slot4", (p, c) => HandleMenuKey(p, 4));
        AddCommandListener("slot5", (p, c) => HandleMenuKey(p, 5));
        AddCommandListener("slot9", (p, c) => HandleMenuKey(p, 9));

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterEventHandler<EventItemEquip>(OnItemEquip);

        RegisterListener<Listeners.OnEntityTakeDamagePre>(OnEntityTakeDamagePre);
        RegisterListener<Listeners.OnEntityTakeDamagePost>(OnEntityTakeDamagePost);

        var ticks = Math.Max(1, _settings.RefreshEveryTicks);
        AddTickTimer(ticks, ApplyMovementEffects, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        Console.WriteLine("[ZombieKnifeMenu] v1.9.2 loaded.");
    }

    public override void Unload(bool hotReload)
    {
        SaveSelections();

        foreach (var player in Utilities.GetPlayers())
        {
            CloseKnifeMenu(player);
            ResetMovement(player);
            ResetKnifeSubclass(player);
        }

        _openKnifeMenus.Clear();
    }

    [ConsoleCommand("css_knife", "Open Knife Menu")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnKnifeCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsRealPlayer(player))
            return;

        OpenKnifeMenu(player!);
    }

    [ConsoleCommand("css_kniferefresh", "Re-apply selected custom knife model")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnKnifeRefreshCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsAliveHuman(player))
            return;

        ApplySelectedKnifeSubclass(player!);

        try
        {
            player!.ExecuteClientCommand("slot3");
        }
        catch
        {
        }

        player!.PrintToChat(" \x04[Knife Menu]\x01 Modelul selectat a fost reaplicat.");
    }

    [ConsoleCommand("css_cutite", "Open Knife Menu")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnCutiteCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsRealPlayer(player))
            return;

        OpenKnifeMenu(player!);
    }

    private void OpenKnifeMenu(CCSPlayerController player)
    {
        _openKnifeMenus.Add(player.SteamID);

        // CounterStrikeSharp native on-screen HTML HUD.
        // No CS2ScreenMenuAPI / CS2-GameHUD dependency.
        player.PrintToCenterHtml(BuildKnifeMenuHtml(player), 30);
    }

    private string BuildKnifeMenuHtml(CCSPlayerController player)
    {
        var selected = GetSelection(player);
        var vip = HasVip(player);

        string Sel(KnifeType type) =>
            selected == type ? " <font color='#55FF55'>[SELECTED]</font>" : "";

        string vipLine = vip
            ? $"5. VIP Knife{Sel(KnifeType.Vip)}"
            : "5. <font color='#777777'>VIP Knife [VIP ONLY]</font>";

        return
            "<font color='#FFD36A' size='24'>Knife Menu</font><br><br>" +
            $"<font color='#FFFFFF'>1. Speed Knife{Sel(KnifeType.Speed)}<br>" +
            $"2. Gravity Knife{Sel(KnifeType.Gravity)}<br>" +
            $"3. Knockback Knife{Sel(KnifeType.Knockback)}<br>" +
            $"4. Damage Knife{Sel(KnifeType.Damage)}<br>" +
            $"{vipLine}<br><br>" +
            "<font color='#FF7777'>9. Close</font></font>";
    }

    private HookResult HandleMenuKey(CCSPlayerController? player, int key)
    {
        if (!IsRealPlayer(player) || !_openKnifeMenus.Contains(player!.SteamID))
            return HookResult.Continue;

        var validPlayer = player!;

        if (key == 9)
        {
            CloseKnifeMenu(validPlayer);
            return HookResult.Handled;
        }

        KnifeType type = key switch
        {
            1 => KnifeType.Speed,
            2 => KnifeType.Gravity,
            3 => KnifeType.Knockback,
            4 => KnifeType.Damage,
            5 => KnifeType.Vip,
            _ => 0
        };

        if (type == 0)
            return HookResult.Handled;

        if (type == KnifeType.Vip && !HasVip(validPlayer))
        {
            validPlayer.PrintToChat(" \x02[Knife Menu]\x01 VIP Knife este doar pentru VIP.");
            validPlayer.PrintToCenterHtml(BuildKnifeMenuHtml(validPlayer), 30);
            return HookResult.Handled;
        }

        // Close/remove menu state BEFORE SelectKnife.
        // SelectKnife executes slot3 to pull the knife out; if menu were still
        // marked open, our slot3 listener would incorrectly select option 3.
        CloseKnifeMenu(validPlayer);

        SelectKnife(validPlayer, type);
        return HookResult.Handled;
    }

    private void CloseKnifeMenu(CCSPlayerController? player)
    {
        if (!IsRealPlayer(player))
            return;

        _openKnifeMenus.Remove(player!.SteamID);

        // Clear the native center HTML quickly.
        try
        {
            player.PrintToCenterHtml(" ", 1);
        }
        catch
        {
        }
    }

    private void SelectKnife(CCSPlayerController player, KnifeType type)
    {
        if (!IsRealPlayer(player))
            return;

        if (type == KnifeType.Vip && !HasVip(player))
        {
            player.PrintToChat(" \x02[Knife Menu]\x01 VIP Knife este doar pentru VIP.");
            return;
        }

        _selections[SteamKey(player)] = type;
        SaveSelections();

        player.PrintToChat($" \x04[Knife Menu]\x01 Ai ales \x10{KnifeName(type)}\x01.");

        if (!IsAliveHuman(player))
            return;

        Server.NextFrame(() =>
        {
            if (!IsAliveHuman(player))
                return;

            ApplySelectedKnifeSubclass(player);
            ApplyMovementToPlayer(player);

            // Menu state is already closed before SelectKnife() is called,
            // so slot3 can safely refresh/pull out the knife.
            try
            {
                player.ExecuteClientCommand("slot3");
            }
            catch
            {
            }
        });

        // Zombie:Reborn/loadout code may touch the weapon for a few frames,
        // so re-apply the chosen subclass twice.
        AddTimer(0.20f, () =>
        {
            if (IsAliveHuman(player))
                ApplySelectedKnifeSubclass(player);
        }, TimerFlags.STOP_ON_MAPCHANGE);

        AddTimer(0.60f, () =>
        {
            if (IsAliveHuman(player))
                ApplySelectedKnifeSubclass(player);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null)
            _openKnifeMenus.Remove(player.SteamID);

        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!IsRealPlayer(player))
            return HookResult.Continue;

        AddTimer(0.35f, () =>
        {
            if (!IsRealPlayer(player))
                return;

            var validPlayer = player!;

            ResetMovement(validPlayer);

            if (IsAliveHuman(validPlayer))
            {
                ValidateVipSelection(validPlayer);
                ApplySelectedKnifeSubclass(validPlayer);
                ApplyMovementToPlayer(validPlayer);
            }
            else
            {
                ResetKnifeSubclass(validPlayer);
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);

        AddTimer(1.00f, () =>
        {
            if (IsAliveHuman(player))
                ApplySelectedKnifeSubclass(player!);
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!IsRealPlayer(player))
            return HookResult.Continue;

        Server.NextFrame(() =>
        {
            if (!IsRealPlayer(player))
                return;

            var validPlayer = player!;

            if (validPlayer.TeamNum == _settings.HumanTeam)
            {
                ValidateVipSelection(validPlayer);
                ApplySelectedKnifeSubclass(validPlayer);
            }
            else
            {
                ResetMovement(validPlayer);
                ResetKnifeSubclass(validPlayer);
            }
        });

        return HookResult.Continue;
    }

    private HookResult OnItemEquip(EventItemEquip @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!IsAliveHuman(player))
            return HookResult.Continue;

        // Let CS2 finish switching the active viewmodel, then replace it.
        Server.NextFrame(() =>
        {
            if (!IsAliveHuman(player))
                return;

            var active = player!.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
            if (active != null && active.IsValid && IsKnife(active))
                ApplySelectedKnifeSubclass(player);
        });

        return HookResult.Continue;
    }

    private void ApplySelectedKnifeSubclass(CCSPlayerController player)
    {
        if (!IsAliveHuman(player))
            return;

        ValidateVipSelection(player);

        var knife = GetKnife(player);
        if (knife == null || !knife.IsValid)
        {
            Console.WriteLine($"[ZombieKnifeMenu] Knife entity not ready for {player.PlayerName}.");
            return;
        }

        var subclass = GetSelectedSubclass(player);

        try
        {
            // The Workshop addon defines these subclasses in scripts/weapons.vdata_c.
            // ChangeSubclass makes the existing weapon_knife use the selected custom VData.
            knife.AcceptInput(
                "ChangeSubclass",
                activator: player.PlayerPawn.Value,
                caller: player.PlayerPawn.Value,
                value: subclass);

            Console.WriteLine(
                $"[ZombieKnifeMenu] Applied {subclass} to {player.PlayerName}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[ZombieKnifeMenu] Failed to apply subclass {subclass} to {player.PlayerName}: {ex}");
        }
    }

    private void ResetKnifeSubclass(CCSPlayerController? player)
    {
        if (!IsRealPlayer(player))
            return;

        var knife = GetKnife(player!);
        if (knife == null || !knife.IsValid)
            return;

        knife.AcceptInput("ChangeSubclass", value: _settings.DefaultKnifeSubclass);
    }

    private void ApplyMovementEffects()
    {
        foreach (var player in Utilities.GetPlayers())
            ApplyMovementToPlayer(player);
    }

    private void ApplyMovementToPlayer(CCSPlayerController? player)
    {
        if (!IsAliveHuman(player))
            return;

        var pawn = player!.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return;

        var selected = GetSelection(player);
        var knifeInHand = IsKnifeInHand(pawn);

        float speed = 1.0f;
        float gravity = 1.0f;

        if (knifeInHand)
        {
            if (selected is KnifeType.Speed or KnifeType.Vip)
                speed = _settings.SpeedMultiplier;

            if (selected is KnifeType.Gravity or KnifeType.Vip)
                gravity = _settings.GravityScale;
        }

        pawn.VelocityModifier = speed;
        pawn.GravityScale = gravity;

        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_flGravityScale");
    }

    private void ResetMovement(CCSPlayerController? player)
    {
        if (!IsAlivePlayer(player))
            return;

        var pawn = player!.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return;

        pawn.VelocityModifier = 1.0f;
        pawn.GravityScale = 1.0f;

        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_flGravityScale");
    }

    private HookResult OnEntityTakeDamagePre(CBaseEntity entity, CTakeDamageInfo info)
    {
        if (!TryGetHumanKnifeHit(entity, info, out var attacker, out _, out _, out _))
            return HookResult.Continue;

        var selected = GetSelection(attacker);
        if (selected is KnifeType.Damage or KnifeType.Vip)
            info.Damage *= _settings.DamageMultiplier;

        return HookResult.Continue;
    }

    private void OnEntityTakeDamagePost(CBaseEntity entity, CTakeDamageInfo info, CTakeDamageResult result)
    {
        if (!TryGetHumanKnifeHit(entity, info, out var attacker, out _, out var attackerPawn, out var victimPawn))
            return;

        var selected = GetSelection(attacker);
        if (selected is not (KnifeType.Knockback or KnifeType.Vip))
            return;

        var aPos = attackerPawn.AbsOrigin;
        var vPos = victimPawn.AbsOrigin;
        if (aPos == null || vPos == null)
            return;

        float dx = vPos.X - aPos.X;
        float dy = vPos.Y - aPos.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);

        if (len < 0.001f)
        {
            dx = 1.0f;
            dy = 0.0f;
            len = 1.0f;
        }

        dx /= len;
        dy /= len;

        var oldVel = victimPawn.AbsVelocity;
        var newVel = new Vector(
            oldVel.X + dx * _settings.KnockbackHorizontal,
            oldVel.Y + dy * _settings.KnockbackHorizontal,
            MathF.Max(oldVel.Z, 0f) + _settings.KnockbackVertical
        );

        victimPawn.Teleport(null, null, newVel);
    }

    private bool TryGetHumanKnifeHit(
        CBaseEntity victimEntity,
        CTakeDamageInfo info,
        out CCSPlayerController attacker,
        out CCSPlayerController victim,
        out CCSPlayerPawn attackerPawn,
        out CCSPlayerPawn victimPawn)
    {
        attacker = null!;
        victim = null!;
        attackerPawn = null!;
        victimPawn = null!;

        if (victimEntity == null || !victimEntity.IsValid || !victimEntity.IsPlayerPawn())
            return false;

        if ((info.BitsDamageType & DamageTypes_t.DMG_SLASH) == 0)
            return false;

        var attackerEntity = info.Attacker.Value;
        if (attackerEntity == null || !attackerEntity.IsValid || !attackerEntity.IsPlayerPawn())
            return false;

        attackerPawn = attackerEntity.As<CCSPlayerPawn>();
        victimPawn = victimEntity.As<CCSPlayerPawn>();

        if (attackerPawn == null || victimPawn == null ||
            !attackerPawn.IsValid || !victimPawn.IsValid)
            return false;

        var attackerController = attackerPawn.Controller.Value;
        var victimController = victimPawn.Controller.Value;

        if (attackerController == null || victimController == null ||
            !attackerController.IsValid || !victimController.IsValid)
            return false;

        attacker = attackerController.As<CCSPlayerController>();
        victim = victimController.As<CCSPlayerController>();

        if (!IsRealPlayer(attacker) || !IsRealPlayer(victim) || attacker == victim)
            return false;

        return attacker.TeamNum == _settings.HumanTeam &&
               victim.TeamNum == _settings.ZombieTeam;
    }

    private bool HasVip(CCSPlayerController player)
    {
        if (string.IsNullOrWhiteSpace(_settings.VipPermission))
            return true;

        return AdminManager.PlayerHasPermissions(player, _settings.VipPermission) ||
               AdminManager.PlayerHasPermissions(player, "@css/root");
    }

    private void ValidateVipSelection(CCSPlayerController player)
    {
        if (GetSelection(player) == KnifeType.Vip && !HasVip(player))
        {
            _selections[SteamKey(player)] = KnifeType.Speed;
            SaveSelections();
        }
    }

    private KnifeType GetSelection(CCSPlayerController player)
    {
        var key = SteamKey(player);
        return _selections.TryGetValue(key, out var type) ? type : KnifeType.Speed;
    }

    private string GetSelectedSubclass(CCSPlayerController player)
    {
        return GetSelection(player) switch
        {
            KnifeType.Speed => _settings.SpeedKnifeSubclass,
            KnifeType.Gravity => _settings.GravityKnifeSubclass,
            KnifeType.Knockback => _settings.KnockbackKnifeSubclass,
            KnifeType.Damage => _settings.DamageKnifeSubclass,
            KnifeType.Vip => _settings.VipKnifeSubclass,
            _ => _settings.SpeedKnifeSubclass
        };
    }

    private static string KnifeName(KnifeType type) => type switch
    {
        KnifeType.Speed => "Speed Knife",
        KnifeType.Gravity => "Gravity Knife",
        KnifeType.Knockback => "Knockback Knife",
        KnifeType.Damage => "Damage Knife",
        KnifeType.Vip => "VIP Knife",
        _ => "Speed Knife"
    };

    private static CBasePlayerWeapon? GetKnife(CCSPlayerController player)
    {
        var weaponServices = player.PlayerPawn.Value?.WeaponServices;
        if (weaponServices == null)
            return null;

        foreach (var handle in weaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon != null && weapon.IsValid && IsKnife(weapon))
                return weapon;
        }

        return null;
    }

    private static bool IsKnife(CBasePlayerWeapon weapon)
    {
        var name = weapon.DesignerName ?? string.Empty;
        return name.Contains("knife", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("bayonet", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnifeInHand(CCSPlayerPawn pawn)
    {
        var weapon = pawn.WeaponServices?.ActiveWeapon.Value;
        return weapon != null && weapon.IsValid && IsKnife(weapon);
    }

    private static string SteamKey(CCSPlayerController player)
        => player.SteamID.ToString();

    private bool IsAliveHuman(CCSPlayerController? player)
        => IsAlivePlayer(player) && player!.TeamNum == _settings.HumanTeam;

    private static bool IsRealPlayer(CCSPlayerController? player)
        => player != null && player.IsValid && !player.IsBot && !player.IsHLTV;

    private static bool IsAlivePlayer(CCSPlayerController? player)
    {
        if (!IsRealPlayer(player))
            return false;

        var pawn = player!.PlayerPawn.Value;
        return pawn != null && pawn.IsValid && pawn.Health > 0;
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                File.WriteAllText(SettingsPath,
                    JsonSerializer.Serialize(_settings,
                        new JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            var loaded = JsonSerializer.Deserialize<KnifeSettings>(
                File.ReadAllText(SettingsPath));

            if (loaded != null)
                _settings = loaded;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[ZombieKnifeMenu] Failed to load config.json; using defaults: {ex}");
        }
    }

    private void LoadSelections()
    {
        try
        {
            if (!File.Exists(SelectionsPath))
                return;

            var raw = JsonSerializer.Deserialize<Dictionary<string, int>>(
                File.ReadAllText(SelectionsPath));

            if (raw == null)
                return;

            _selections.Clear();

            foreach (var pair in raw)
            {
                if (Enum.IsDefined(typeof(KnifeType), pair.Value))
                    _selections[pair.Key] = (KnifeType)pair.Value;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[ZombieKnifeMenu] Failed to load knife selections: {ex}");
        }
    }

    private void SaveSelections()
    {
        try
        {
            var raw = _selections.ToDictionary(x => x.Key, x => (int)x.Value);

            File.WriteAllText(SelectionsPath,
                JsonSerializer.Serialize(raw,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[ZombieKnifeMenu] Failed to save knife selections: {ex}");
        }
    }
}
