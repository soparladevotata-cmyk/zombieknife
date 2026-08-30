using Microsoft.Extensions.Logging;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Menu;
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
}

[MinimumApiVersion(80)]
public sealed class ZombieKnifeMenuPlugin : BasePlugin
{
    public override string ModuleName => "Zombie Knife Menu";
    public override string ModuleVersion => "1.5.0";
    public override string ModuleAuthor => "OpenAI";
    public override string ModuleDescription =>
        "CS 1.6-style Knife Menu with custom CS2 weapon models for Zombie:Reborn";

    private KnifeSettings _settings = new();
    private readonly Dictionary<string, KnifeType> _selections = new();

    private string SettingsPath => Path.Combine(ModuleDirectory, "config.json");
    private string SelectionsPath => Path.Combine(ModuleDirectory, "knife_selections.json");

    public override void Load(bool hotReload)
    {
        Directory.CreateDirectory(ModuleDirectory);
        LoadSettings();
        LoadSelections();

        AddCommand("css_knives", "Open Knife Menu", OnKnifeCommand);

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterEventHandler<EventItemEquip>(OnItemEquip);

        RegisterListener<Listeners.OnEntityTakeDamagePre>(OnEntityTakeDamagePre);
        RegisterListener<Listeners.OnEntityTakeDamagePost>(OnEntityTakeDamagePost);

        var ticks = Math.Max(1, _settings.RefreshEveryTicks);
        AddTickTimer(ticks, ApplyMovementEffects, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        Logger.LogInformation("[ZombieKnifeMenu] v1.5.0 loaded.");
    }

    public override void Unload(bool hotReload)
    {
        SaveSelections();

        foreach (var player in Utilities.GetPlayers())
        {
            ResetMovement(player);
            ResetKnifeSubclass(player);
        }
    }

    [ConsoleCommand("css_knife", "Open Knife Menu")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnKnifeCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsRealPlayer(player))
            return;

        OpenKnifeMenu(player!);
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
        var vip = HasVip(player);

        // Native CounterStrikeSharp menu: no external menu DLL, so it stays
        // compatible with the exact CounterStrikeSharp version running the server.
        var menu = new ChatMenu("Knife Menu")
        {
            ExitButton = true
        };

        menu.AddMenuOption(
            BuildLabel(player, KnifeType.Speed, "Speed Knife"),
            (p, _) => SelectKnife(p, KnifeType.Speed));

        menu.AddMenuOption(
            BuildLabel(player, KnifeType.Gravity, "Gravity Knife"),
            (p, _) => SelectKnife(p, KnifeType.Gravity));

        menu.AddMenuOption(
            BuildLabel(player, KnifeType.Knockback, "Knockback Knife"),
            (p, _) => SelectKnife(p, KnifeType.Knockback));

        menu.AddMenuOption(
            BuildLabel(player, KnifeType.Damage, "Damage Knife"),
            (p, _) => SelectKnife(p, KnifeType.Damage));

        menu.AddMenuOption(
            BuildLabel(player, KnifeType.Vip, vip ? "VIP Knife" : "VIP Knife [VIP ONLY]"),
            (p, _) => SelectKnife(p, KnifeType.Vip),
            disabled: !vip);

        MenuManager.OpenChatMenu(player, menu);
    }

    private string BuildLabel(CCSPlayerController player, KnifeType type, string name)
    {
        return GetSelection(player) == type ? $"{name} [SELECTED]" : name;
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

        if (IsAliveHuman(player))
        {
            Server.NextFrame(() =>
            {
                ApplySelectedKnifeSubclass(player);
                ApplyMovementToPlayer(player);
            });
        }
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

            ResetMovement(player);

            if (IsAliveHuman(player))
            {
                ValidateVipSelection(player);
                ApplySelectedKnifeSubclass(player);
                ApplyMovementToPlayer(player);
            }
            else
            {
                ResetKnifeSubclass(player);
            }
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
            return;

        var subclass = GetSelectedSubclass(player);

        // Current CS2 custom weapon flow: change the weapon's VData subclass.
        // The subclass itself points m_szModel_AG2 at the custom .vmdl.
        knife.AcceptInput("ChangeSubclass", value: subclass);
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
            Logger.LogError(ex,
                "[ZombieKnifeMenu] Failed to load config.json; using defaults.");
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
            Logger.LogError(ex,
                "[ZombieKnifeMenu] Failed to load knife selections.");
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
            Logger.LogError(ex,
                "[ZombieKnifeMenu] Failed to save knife selections.");
        }
    }
}
