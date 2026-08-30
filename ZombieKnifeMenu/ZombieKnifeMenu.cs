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
    public override string ModuleVersion => "2.2.0";
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

        // CS2 does not send the normal client-side slot1..slot9 commands to the server.
        // Number keys are therefore bound to these server-visible commands once via !bindknife.
        AddCommand("css_k1", "Knife menu key 1", (p, c) => HandleBoundMenuKey(p, 1));
        AddCommand("css_k2", "Knife menu key 2", (p, c) => HandleBoundMenuKey(p, 2));
        AddCommand("css_k3", "Knife menu key 3", (p, c) => HandleBoundMenuKey(p, 3));
        AddCommand("css_k4", "Knife menu key 4", (p, c) => HandleBoundMenuKey(p, 4));
        AddCommand("css_k5", "Knife menu key 5", (p, c) => HandleBoundMenuKey(p, 5));
        AddCommand("css_k9", "Knife menu key 9", (p, c) => HandleBoundMenuKey(p, 9));

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterEventHandler<EventItemEquip>(OnItemEquip);

        RegisterListener<Listeners.OnEntityTakeDamagePre>(OnEntityTakeDamagePre);
        RegisterListener<Listeners.OnEntityTakeDamagePost>(OnEntityTakeDamagePost);

        var ticks = Math.Max(1, _settings.RefreshEveryTicks);
        AddTickTimer(ticks, ApplyMovementEffects, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        Console.WriteLine("[ZombieKnifeMenu] v2.2.0 loaded.");
    }

    public override void Unload(bool hotReload)
    {
        SaveSelections();

        foreach (var player in Utilities.GetPlayers())
        {
            if (IsRealPlayer(player))
                MenuManager.CloseActiveMenu(player);

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

    [ConsoleCommand("css_bindknife", "Bind 1-5/9 to the Knife Menu")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnBindKnifeCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsRealPlayer(player))
            return;

        var validPlayer = player!;

        try
        {
            // Keep the normal weapon-slot behavior and append our server-visible menu key.
            validPlayer.ExecuteClientCommand("bind 1 \"css_k1\"");
            validPlayer.ExecuteClientCommand("bind 2 \"css_k2\"");
            validPlayer.ExecuteClientCommand("bind 3 \"css_k3\"");
            validPlayer.ExecuteClientCommand("bind 4 \"css_k4\"");
            validPlayer.ExecuteClientCommand("bind 5 \"css_k5\"");
            validPlayer.ExecuteClientCommand("bind 9 \"css_k9\"");
            validPlayer.ExecuteClientCommand("host_writeconfig");

            validPlayer.PrintToChat(" \x04[Knife Menu]\x01 Am incercat sa leg tastele 1-5 si 9. Deschide \x10!knife\x01 si testeaza.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ZombieKnifeMenu] Auto-bind failed for {validPlayer.PlayerName}: {ex}");
            PrintManualBindHelp(validPlayer);
        }
    }

    [ConsoleCommand("css_unbindknife", "Restore normal 1-5/9 slot binds")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnUnbindKnifeCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsRealPlayer(player))
            return;

        var validPlayer = player!;

        try
        {
            validPlayer.ExecuteClientCommand("bind 1 slot1");
            validPlayer.ExecuteClientCommand("bind 2 slot2");
            validPlayer.ExecuteClientCommand("bind 3 slot3");
            validPlayer.ExecuteClientCommand("bind 4 slot4");
            validPlayer.ExecuteClientCommand("bind 5 slot5");
            validPlayer.ExecuteClientCommand("bind 9 slot9");
            validPlayer.ExecuteClientCommand("host_writeconfig");
            validPlayer.PrintToChat(" \x04[Knife Menu]\x01 Tastele 1-5/9 au fost restaurate la sloturile normale.");
        }
        catch
        {
            validPlayer.PrintToChat(" \x02[Knife Menu]\x01 CS2 a blocat restaurarea automata. Pune manual bind-urile normale in consola.");
        }
    }

    private static void PrintManualBindHelp(CCSPlayerController player)
    {
        player.PrintToChat(" \x04[Knife Menu]\x01 Daca auto-bind-ul este blocat de CS2, copiaza in consola:");
        player.PrintToConsole("bind 1 \"css_k1\"");
        player.PrintToConsole("bind 2 \"css_k2\"");
        player.PrintToConsole("bind 3 \"css_k3\"");
        player.PrintToConsole("bind 4 \"css_k4\"");
        player.PrintToConsole("bind 5 \"css_k5\"");
        player.PrintToConsole("bind 9 \"css_k9\"");
        player.PrintToConsole("host_writeconfig");
    }

    [ConsoleCommand("css_kniferefresh", "Re-apply selected custom knife model")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnKnifeRefreshCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsAliveHuman(player))
            return;

        ApplySelectedKnifeSubclass(player!);

        AddTimer(0.08f, () =>
        {
            if (IsAliveHuman(player))
                ForceKnifeDrawAnimation(player!);
        }, TimerFlags.STOP_ON_MAPCHANGE);

        player!.PrintToChat(" \x04[Knife Menu]\x01 Modelul selectat a fost reaplicat cu animatia de draw.");
    }

    [ConsoleCommand("css_knifedebug", "Show current knife-menu/model state")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnKnifeDebugCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsRealPlayer(player))
            return;

        var validPlayer = player!;
        var selected = GetSelection(validPlayer);
        var subclass = GetSelectedSubclass(validPlayer);
        var knife = GetKnife(validPlayer);
        var entityName = knife != null && knife.IsValid ? knife.DesignerName : "NONE";
        var human = IsAliveHuman(validPlayer);

        var msg =
            $"selected={KnifeName(selected)} | subclass={subclass} | " +
            $"team={validPlayer.TeamNum} | human={human} | knifeEntity={entityName}";

        validPlayer.PrintToChat($" \x04[Knife Debug]\x01 {msg}");
        Console.WriteLine($"[ZombieKnifeMenu DEBUG] {validPlayer.PlayerName}: {msg}");
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
        if (!IsRealPlayer(player))
            return;

        // Official CounterStrikeSharp CenterHtmlMenu.
        // Its callbacks are driven by MenuManager.OnKeyPress.
        var selected = GetSelection(player);
        var hasVip = HasVip(player);

        var menu = new CenterHtmlMenu("Knife Menu", this)
        {
            ExitButton = true,
            PostSelectAction = PostSelectAction.Close,
            TitleColor = "gold",
            EnabledColor = "white",
            DisabledColor = "gray",
            CloseColor = "tomato"
        };

        menu.AddMenuOption(
            $"Speed Knife{SelectedSuffix(selected, KnifeType.Speed)}",
            (p, _) => SelectKnife(p, KnifeType.Speed));

        menu.AddMenuOption(
            $"Gravity Knife{SelectedSuffix(selected, KnifeType.Gravity)}",
            (p, _) => SelectKnife(p, KnifeType.Gravity));

        menu.AddMenuOption(
            $"Knockback Knife{SelectedSuffix(selected, KnifeType.Knockback)}",
            (p, _) => SelectKnife(p, KnifeType.Knockback));

        menu.AddMenuOption(
            $"Damage Knife{SelectedSuffix(selected, KnifeType.Damage)}",
            (p, _) => SelectKnife(p, KnifeType.Damage));

        menu.AddMenuOption(
            hasVip
                ? $"VIP Knife{SelectedSuffix(selected, KnifeType.Vip)}"
                : "VIP Knife [VIP ONLY]",
            (p, _) => SelectKnife(p, KnifeType.Vip),
            disabled: !hasVip);

        _openKnifeMenus.Add(player.SteamID);
        menu.Open(player);
    }

    private static string SelectedSuffix(KnifeType selected, KnifeType type)
        => selected == type ? " [SELECTED]" : "";

    private void HandleBoundMenuKey(CCSPlayerController? player, int key)
    {
        if (!IsRealPlayer(player))
            return;

        var validPlayer = player!;

        // Outside Knife Menu, number keys keep their normal weapon-slot behavior.
        if (!_openKnifeMenus.Contains(validPlayer.SteamID))
        {
            try
            {
                validPlayer.ExecuteClientCommand($"slot{key}");
            }
            catch
            {
            }

            return;
        }

        // Exact CS 1.6 / radio-style mapping requested:
        // 1-5 = choose knife, 9 = close.
        if (key == 9)
        {
            _openKnifeMenus.Remove(validPlayer.SteamID);

            try
            {
                MenuManager.CloseActiveMenu(validPlayer);
            }
            catch
            {
            }

            return;
        }

        KnifeType selected = key switch
        {
            1 => KnifeType.Speed,
            2 => KnifeType.Gravity,
            3 => KnifeType.Knockback,
            4 => KnifeType.Damage,
            5 => KnifeType.Vip,
            _ => 0
        };

        if (selected == 0)
            return;

        // Close the visual menu immediately, then apply the chosen knife.
        _openKnifeMenus.Remove(validPlayer.SteamID);

        try
        {
            MenuManager.CloseActiveMenu(validPlayer);
        }
        catch
        {
        }

        SelectKnife(validPlayer, selected);
    }

    private void SelectKnife(CCSPlayerController player, KnifeType type)
    {
        if (!IsRealPlayer(player))
            return;

        _openKnifeMenus.Remove(player.SteamID);

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
        });

        // Very fast switch away-and-back so CS2 plays the knife deploy/draw
        // animation without the transition feeling slow.
        AddTimer(0.03f, () =>
        {
            if (IsAliveHuman(player))
                ForceKnifeDrawAnimation(player);
        }, TimerFlags.STOP_ON_MAPCHANGE);

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

    private void ForceKnifeDrawAnimation(CCSPlayerController player)
    {
        if (!IsAliveHuman(player))
            return;

        var active = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        var knifeWasAlreadyOut = active != null && active.IsValid && IsKnife(active);

        try
        {
            if (knifeWasAlreadyOut)
            {
                // Fast weapon-change pulse: previous weapon -> knife.
                // This triggers the knife deploy animation while keeping it snappy.
                player.ExecuteClientCommand("lastinv");

                AddTimer(0.035f, () =>
                {
                    if (!IsAliveHuman(player))
                        return;

                    ApplySelectedKnifeSubclass(player);

                    AddTimer(0.015f, () =>
                    {
                        if (!IsAliveHuman(player))
                            return;

                        try
                        {
                            player.ExecuteClientCommand("slot3");
                        }
                        catch
                        {
                        }
                    }, TimerFlags.STOP_ON_MAPCHANGE);
                }, TimerFlags.STOP_ON_MAPCHANGE);
            }
            else
            {
                // Knife is not currently equipped: applying the subclass first and
                // selecting slot3 naturally plays the draw/deploy animation.
                ApplySelectedKnifeSubclass(player);

                AddTimer(0.015f, () =>
                {
                    if (!IsAliveHuman(player))
                        return;

                    try
                    {
                        player.ExecuteClientCommand("slot3");
                    }
                    catch
                    {
                    }
                }, TimerFlags.STOP_ON_MAPCHANGE);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[ZombieKnifeMenu] Knife draw refresh failed for {player.PlayerName}: {ex}");
        }
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
            // Work like a Source1 "deploy" refresh: reset the existing knife to its base
            // VData first, then apply the selected subclass on the next frame.
            knife.AcceptInput(
                "ChangeSubclass",
                activator: player.PlayerPawn.Value,
                caller: player.PlayerPawn.Value,
                value: _settings.DefaultKnifeSubclass);

            Server.NextFrame(() =>
            {
                if (!IsAliveHuman(player))
                    return;

                var refreshedKnife = GetKnife(player);
                if (refreshedKnife == null || !refreshedKnife.IsValid)
                    return;

                refreshedKnife.AcceptInput(
                    "ChangeSubclass",
                    activator: player.PlayerPawn.Value,
                    caller: player.PlayerPawn.Value,
                    value: subclass);

                Console.WriteLine(
                    $"[ZombieKnifeMenu] Applied {subclass} to {player.PlayerName}.");
            });
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
                    System.Text.Json.JsonSerializer.Serialize(_settings,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            var loaded = System.Text.Json.JsonSerializer.Deserialize<KnifeSettings>(
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

            var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(
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
                System.Text.Json.JsonSerializer.Serialize(raw,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[ZombieKnifeMenu] Failed to save knife selections: {ex}");
        }
    }
}
