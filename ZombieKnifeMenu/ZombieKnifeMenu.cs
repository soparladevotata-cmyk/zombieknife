using Microsoft.Extensions.Logging;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace ZombieKnifeMenu;

public enum KnifeType
{
    Classic = 0,
    Speed = 1,
    Gravity = 2,
    Knockback = 3,
    Damage = 4
}

public sealed class KnifeSettings
{
    public float SpeedMultiplier { get; set; } = 1.15f;
    public float GravityScale { get; set; } = 0.72f;
    public float KnockbackHorizontal { get; set; } = 420.0f;
    public float KnockbackVertical { get; set; } = 170.0f;
    public float DamageMultiplier { get; set; } = 1.50f;

    // Zombie:Reborn default layout: Humans = CT (3), Zombies = T (2)
    public byte HumanTeam { get; set; } = 3;
    public byte ZombieTeam { get; set; } = 2;

    // How often movement effects are refreshed. 4 = every 4 server ticks.
    public int RefreshEveryTicks { get; set; } = 4;
}

[MinimumApiVersion(80)]
public sealed class ZombieKnifeMenuPlugin : BasePlugin
{
    public override string ModuleName => "Zombie Knife Menu";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "OpenAI";
    public override string ModuleDescription => "CS 1.6-style knife bonuses for Zombie:Reborn / CounterStrikeSharp";

    private KnifeSettings _settings = new();
    private readonly Dictionary<string, KnifeType> _selections = new();

    private string SettingsPath => Path.Combine(ModuleDirectory, "config.json");
    private string SelectionsPath => Path.Combine(ModuleDirectory, "knife_selections.json");

    public override void Load(bool hotReload)
    {
        Directory.CreateDirectory(ModuleDirectory);
        LoadSettings();
        LoadSelections();

        AddCommand("css_knives", "Open the zombie knife menu", OnKnifeCommand);

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterListener<Listeners.OnEntityTakeDamagePre>(OnEntityTakeDamagePre);
        RegisterListener<Listeners.OnEntityTakeDamagePost>(OnEntityTakeDamagePost);

        var tickRate = Math.Max(1, _settings.RefreshEveryTicks);
        AddTickTimer(tickRate, ApplyMovementEffects, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        Logger.LogInformation("[ZombieKnifeMenu] Loaded. Use !knife or !knives.");
    }

    public override void Unload(bool hotReload)
    {
        SaveSelections();

        // Clean up movement modifiers on unload.
        foreach (var player in Utilities.GetPlayers())
        {
            ResetMovement(player);
        }
    }

    [ConsoleCommand("css_knife", "Open the zombie knife menu")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnKnifeCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsRealPlayer(player))
            return;

        OpenKnifeMenu(player!);
    }

    private void OpenKnifeMenu(CCSPlayerController player)
    {
        var selected = GetSelection(player);

        var menu = new ChatMenu("★ ZOMBIE KNIFE MENU ★")
        {
            ExitButton = true
        };

        menu.AddMenuOption(
            FormatOption(selected, KnifeType.Classic, "Classic Knife", "fara bonus"),
            (p, _) => SelectKnife(p, KnifeType.Classic));

        menu.AddMenuOption(
            FormatOption(selected, KnifeType.Speed, "Speed Knife", $"+{(_settings.SpeedMultiplier - 1f) * 100f:0}% viteza"),
            (p, _) => SelectKnife(p, KnifeType.Speed));

        menu.AddMenuOption(
            FormatOption(selected, KnifeType.Gravity, "Gravity Knife", $"gravity {_settings.GravityScale:0.00}"),
            (p, _) => SelectKnife(p, KnifeType.Gravity));

        menu.AddMenuOption(
            FormatOption(selected, KnifeType.Knockback, "Knockback Knife", "impinge zombie-ul mai tare"),
            (p, _) => SelectKnife(p, KnifeType.Knockback));

        menu.AddMenuOption(
            FormatOption(selected, KnifeType.Damage, "Damage Knife", $"+{(_settings.DamageMultiplier - 1f) * 100f:0}% damage"),
            (p, _) => SelectKnife(p, KnifeType.Damage));

        MenuManager.OpenChatMenu(player, menu);
    }

    private static string FormatOption(KnifeType selected, KnifeType type, string name, string bonus)
        => selected == type ? $"✓ {name} [{bonus}]" : $"{name} [{bonus}]";

    private void SelectKnife(CCSPlayerController player, KnifeType type)
    {
        if (!IsRealPlayer(player))
            return;

        _selections[SteamKey(player)] = type;
        SaveSelections();

        player.PrintToChat($" \x04[KNIFE]\x01 Ai ales: \x10{KnifeName(type)}\x01.");
        player.PrintToChat(" \x04[KNIFE]\x01 Bonusul se aplica doar cand esti HUMAN si ai cutitul in mana.");

        // Apply immediately if possible.
        ApplyMovementToPlayer(player);
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!IsRealPlayer(player))
            return HookResult.Continue;

        Server.NextFrame(() =>
        {
            if (!IsRealPlayer(player))
                return;

            ResetMovement(player!);
            ApplyMovementToPlayer(player!);
        });

        return HookResult.Continue;
    }

    private void ApplyMovementEffects()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            ApplyMovementToPlayer(player);
        }
    }

    private void ApplyMovementToPlayer(CCSPlayerController? player)
    {
        if (!IsAlivePlayer(player))
            return;

        var pawn = player!.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return;

        // Only affect humans. This avoids fighting Zombie:Reborn's zombie class movement settings.
        if (player.TeamNum != _settings.HumanTeam)
            return;

        var selected = GetSelection(player);
        var knifeInHand = IsKnifeInHand(pawn);

        // Defaults while not holding the selected special knife.
        float speed = 1.0f;
        float gravity = 1.0f;

        if (knifeInHand)
        {
            if (selected == KnifeType.Speed)
                speed = _settings.SpeedMultiplier;

            if (selected == KnifeType.Gravity)
                gravity = _settings.GravityScale;
        }

        // VelocityModifier is the CS2 pawn speed modifier.
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
        if (!TryGetHumanKnifeHit(entity, info, out var attacker, out var victim, out _, out _))
            return HookResult.Continue;

        if (GetSelection(attacker) == KnifeType.Damage)
        {
            info.Damage *= _settings.DamageMultiplier;
        }

        return HookResult.Continue;
    }

    private void OnEntityTakeDamagePost(CBaseEntity entity, CTakeDamageInfo info, CTakeDamageResult result)
    {
        if (!TryGetHumanKnifeHit(entity, info, out var attacker, out var victim, out var attackerPawn, out var victimPawn))
            return;

        if (GetSelection(attacker) != KnifeType.Knockback)
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

        // No-position teleport: only applies velocity.
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

        // Knife damage is slash damage. This catches normal knife, bayonet, karambit, etc.
        if ((info.BitsDamageType & DamageTypes_t.DMG_SLASH) == 0)
            return false;

        var attackerEntity = info.Attacker.Value;
        if (attackerEntity == null || !attackerEntity.IsValid || !attackerEntity.IsPlayerPawn())
            return false;

        attackerPawn = attackerEntity.As<CCSPlayerPawn>();
        victimPawn = victimEntity.As<CCSPlayerPawn>();

        if (attackerPawn == null || victimPawn == null || !attackerPawn.IsValid || !victimPawn.IsValid)
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

        // Humans stab zombies only.
        if (attacker.TeamNum != _settings.HumanTeam || victim.TeamNum != _settings.ZombieTeam)
            return false;

        return true;
    }

    private static bool IsKnifeInHand(CCSPlayerPawn pawn)
    {
        var weapon = pawn.WeaponServices?.ActiveWeapon.Value;
        if (weapon == null || !weapon.IsValid)
            return false;

        var name = weapon.DesignerName ?? string.Empty;
        return name.Contains("knife", StringComparison.OrdinalIgnoreCase)
            || name.Contains("bayonet", StringComparison.OrdinalIgnoreCase);
    }

    private KnifeType GetSelection(CCSPlayerController player)
    {
        var key = SteamKey(player);
        return _selections.TryGetValue(key, out var type) ? type : KnifeType.Classic;
    }

    private static string SteamKey(CCSPlayerController player)
        => player.SteamID.ToString();

    private static string KnifeName(KnifeType type) => type switch
    {
        KnifeType.Speed => "Speed Knife",
        KnifeType.Gravity => "Gravity Knife",
        KnifeType.Knockback => "Knockback Knife",
        KnifeType.Damage => "Damage Knife",
        _ => "Classic Knife"
    };

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
                    JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            var loaded = JsonSerializer.Deserialize<KnifeSettings>(File.ReadAllText(SettingsPath));
            if (loaded != null)
                _settings = loaded;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ZombieKnifeMenu] Failed to load config.json; using defaults.");
        }
    }

    private void LoadSelections()
    {
        try
        {
            if (!File.Exists(SelectionsPath))
                return;

            var raw = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(SelectionsPath));
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
            Logger.LogError(ex, "[ZombieKnifeMenu] Failed to load knife selections.");
        }
    }

    private void SaveSelections()
    {
        try
        {
            var raw = _selections.ToDictionary(x => x.Key, x => (int)x.Value);
            File.WriteAllText(SelectionsPath,
                JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ZombieKnifeMenu] Failed to save knife selections.");
        }
    }
}
