using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Newtonsoft.Json.Linq;

namespace WeaponPaints;

public partial class WeaponPaints
{
	private void OnCommandRefresh(CCSPlayerController? player, CommandInfo command)
	{
		if (!Config.Additional.CommandWpEnabled || !Config.Additional.SkinEnabled || !_gBCommandsAllowed) return;
		if (!Utility.IsPlayerValid(player)) return;

		if (player == null || !player.IsValid || player.UserId == null || player.IsBot) return;

		PlayerInfo? playerInfo = new PlayerInfo
		{
			UserId = player.UserId,
			Slot = player.Slot,
			Index = (int)player.Index,
			SteamId = player?.SteamID.ToString(),
			Name = player?.PlayerName,
			IpAddress = player?.IpAddress?.Split(":")[0]
		};

		try
		{
			if (player != null && !CommandsCooldown.TryGetValue(player.Slot, out var cooldownEndTime) ||
			    player != null && DateTime.UtcNow >= (CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime) ? cooldownEndTime : DateTime.UtcNow))
			{
				CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);

				if (WeaponSync != null)
				{
					_ = Task.Run(async () => await WeaponSync.GetPlayerData(playerInfo));

					GivePlayerGloves(player);
					RefreshWeapons(player);
					GivePlayerAgent(player);
					GivePlayerMusicKit(player);
					AddTimer(0.15f, () => GivePlayerPin(player));
				}

				if (!string.IsNullOrEmpty(Localizer["wp_command_refresh_done"]))
				{
					player.Print(Localizer["wp_command_refresh_done"]);
				}
				return;
			}
			if (!string.IsNullOrEmpty(Localizer["wp_command_cooldown"]))
			{
				player!.Print(Localizer["wp_command_cooldown"]);
			}
		}
		catch (Exception) { }
	}

	private void OnCommandWS(CCSPlayerController? player, CommandInfo command)
	{
		if (!Config.Additional.SkinEnabled) return;
		if (!Utility.IsPlayerValid(player)) return;

		if (!string.IsNullOrEmpty(Localizer["wp_info_website"]))
		{
			player!.Print(Localizer["wp_info_website", Config.Website]);
		}
		if (!string.IsNullOrEmpty(Localizer["wp_info_refresh"]))
		{
			player!.Print(Localizer["wp_info_refresh"]);
		}

		if (Config.Additional.GloveEnabled)
			if (!string.IsNullOrEmpty(Localizer["wp_info_glove"]))
			{
				player!.Print(Localizer["wp_info_glove"]);
			}

		if (Config.Additional.AgentEnabled)
			if (!string.IsNullOrEmpty(Localizer["wp_info_agent"]))
			{
				player!.Print(Localizer["wp_info_agent"]);
			}

		if (Config.Additional.MusicEnabled)
			if (!string.IsNullOrEmpty(Localizer["wp_info_music"]))
			{
				player!.Print(Localizer["wp_info_music"]);
			}

		if (Config.Additional.PinsEnabled)
			if (!string.IsNullOrEmpty(Localizer["wp_info_pin"]))
			{
				player!.Print(Localizer["wp_info_pin"]);
			}

		if (!Config.Additional.KnifeEnabled) return;
		if (!string.IsNullOrEmpty(Localizer["wp_info_knife"]))
		{
			player!.Print(Localizer["wp_info_knife"]);
		}
	}

	private void RegisterCommands()
	{
		// Keep !wp command (refresh)
		_config.Additional.CommandRefresh.ForEach(c =>
		{
			AddCommand($"css_{c}", "Skins refresh", (player, info) =>
			{
				if (!Utility.IsPlayerValid(player)) return;
				OnCommandRefresh(player, info);
			});
		});

		// Keep stattrak toggle command
		_config.Additional.CommandStattrak.ForEach(c =>
		{
			AddCommand($"css_{c}", "Stattrak toggle", (player, info) =>
			{
				if (!Utility.IsPlayerValid(player)) return;
				OnCommandStattrak(player, info);
			});
		});
	}

	private void OnCommandSkinRefresh(CCSPlayerController? player, CommandInfo command)
	{
		if (!Config.Additional.CommandWpEnabled || !Config.Additional.SkinEnabled || !_gBCommandsAllowed) return;
		if (player != null)
		{
			return;
		}

		var args = command.GetArg(1);

		if (string.IsNullOrEmpty(args))
		{
			Console.WriteLine("[WeaponPaints] Usage: wp_refresh <steamid64|all>");
			Console.WriteLine("[WeaponPaints] Examples:");
			Console.WriteLine("[WeaponPaints]   wp_refresh all - Refresh skins for all players");
			Console.WriteLine("[WeaponPaints]   wp_refresh 76561198012345678 - Refresh skins by SteamID64");
			return;
		}

		var targetPlayers = new List<CCSPlayerController>();

		if (args.Equals("all", StringComparison.OrdinalIgnoreCase))
		{
			targetPlayers = Utilities.GetPlayers().Where(p =>
				p != null && p.IsValid && !p.IsBot && p.UserId != null).ToList();

			if (targetPlayers.Count == 0)
			{
				Console.WriteLine("[WeaponPaints] No players connected to refresh.");
				return;
			}

			Console.WriteLine($"[WeaponPaints] Refreshing skins for {targetPlayers.Count} players...");
		}
		else
		{
			var foundPlayer = Utilities.GetPlayers().FirstOrDefault(p =>
				p != null && p.IsValid && !p.IsBot && p.UserId != null &&
				 p.SteamID.ToString() == args);

			if (foundPlayer == null)
			{
				Console.WriteLine($"[WeaponPaints] Player with SteamID64 '{args}' not found.");
				return;
			}

			targetPlayers.Add(foundPlayer);
			Console.WriteLine($"[WeaponPaints] Refreshing skins for {foundPlayer.PlayerName}...");
		}

		foreach (var targetPlayer in targetPlayers)
		{
			try
			{
				PlayerInfo? playerInfo = new PlayerInfo
				{
					UserId = targetPlayer.UserId,
					Slot = targetPlayer.Slot,
					Index = (int)targetPlayer.Index,
					SteamId = targetPlayer.SteamID.ToString(),
					Name = targetPlayer.PlayerName,
					IpAddress = targetPlayer.IpAddress?.Split(":")[0]
				};

				if (WeaponSync != null)
				{
					_ = Task.Run(async () => await WeaponSync.GetPlayerData(playerInfo));
				}

				GivePlayerGloves(targetPlayer);
				RefreshWeapons(targetPlayer);
				GivePlayerAgent(targetPlayer);
				GivePlayerMusicKit(targetPlayer);
				AddTimer(0.15f, () => GivePlayerPin(targetPlayer));

				if (!string.IsNullOrEmpty(Localizer["wp_command_refresh_done"]))
				{
					targetPlayer.Print(Localizer["wp_command_refresh_done"]);
				}

				Console.WriteLine($"[WeaponPaints] Skins refreshed for {targetPlayer.PlayerName}");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[WeaponPaints] Error refreshing skins for {targetPlayer.PlayerName}: {ex.Message}");
			}
		}

		Console.WriteLine("[WeaponPaints] Refresh process completed.");
	}


	private void OnCommandStattrak(CCSPlayerController? player, CommandInfo commandInfo)
	{
		if (player == null || !player.IsValid) return;

		var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;

		if (weapon == null || !weapon.IsValid)
			return;

		if (!HasChangedPaint(player, weapon.AttributeManager.Item.ItemDefinitionIndex, out var weaponInfo) || weaponInfo == null)
			return;

		weaponInfo.StatTrak = !weaponInfo.StatTrak;
		RefreshWeapons(player);

		if (!string.IsNullOrEmpty(Localizer["wp_stattrak_action"]))
		{
			player.Print(Localizer["wp_stattrak_action"]);
		}
	}


}
