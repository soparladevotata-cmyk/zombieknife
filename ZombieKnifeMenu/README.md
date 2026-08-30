# ZombieKnifeMenu

CS 1.6-style knife bonus system for a CS2 Zombie:Reborn server.

## What it adds

Players use:

- `!knife`
- `!knives`

Menu:

1. Classic Knife — no bonus
2. Speed Knife — +15% speed while the knife is in hand
3. Gravity Knife — lower gravity while the knife is in hand
4. Knockback Knife — pushes zombies away harder on knife hit
5. Damage Knife — +50% knife damage versus zombies

The selected knife is saved by SteamID64.

By default the plugin assumes Zombie:Reborn uses:
- CT / Team 3 = humans
- T / Team 2 = zombies

## Build

Requires .NET 10 SDK.

```bash
dotnet restore
dotnet publish -c Release
```

The DLL will be in:

```text
bin/Release/net10.0/publish/ZombieKnifeMenu.dll
```

## Install on the CS2 server

Create:

```text
game/csgo/addons/counterstrikesharp/plugins/ZombieKnifeMenu/
```

Upload:

```text
ZombieKnifeMenu.dll
```

into that folder and restart the server.

After first load the plugin creates:

```text
game/csgo/addons/counterstrikesharp/plugins/ZombieKnifeMenu/config.json
game/csgo/addons/counterstrikesharp/plugins/ZombieKnifeMenu/knife_selections.json
```

Edit `config.json` to change speed, gravity, knockback and damage without recompiling.

## Test

Join as a human and type:

```text
!knife
```

Choose Speed/Gravity etc. For Speed and Gravity, take your knife out before testing the bonus.

Knockback and Damage only apply to human (CT) -> zombie (T) knife hits.