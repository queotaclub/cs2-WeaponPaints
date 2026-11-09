using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace WeaponPaints;

// Live refresh: loadout changes made on the website reach players in game without !wp.
// WeaponSynchronization.RunLiveUpdates long-polls the API; this side tracks who is
// connected and applies a fetched loadout on the game thread, like !wp does.
public partial class WeaponPaints
{
	internal static void TrackPlayer(PlayerInfo info)
	{
		if (!string.IsNullOrEmpty(info.SteamId))
			LivePlayers[info.SteamId] = info;
	}

	internal static void UntrackPlayer(string? steamId)
	{
		if (string.IsNullOrEmpty(steamId)) return;
		LivePlayers.TryRemove(steamId, out _);
		LoadoutRevs.TryRemove(steamId, out _);
	}

	// Still connected in the same slot: a fetch that finishes after a disconnect must not
	// write into a slot someone else may already have.
	internal static bool IsTracked(PlayerInfo info) =>
		info.SteamId != null && LivePlayers.TryGetValue(info.SteamId, out var current) && current.Slot == info.Slot;

	internal static void RecordRev(PlayerInfo info, long rev)
	{
		if (info.SteamId == null || !IsTracked(info)) return;
		var isNew = !LoadoutRevs.ContainsKey(info.SteamId);
		LoadoutRevs[info.SteamId] = rev;
		// A new player joins the long poll right away instead of after the current wait.
		if (isNew) WeaponSync?.WakeLiveLoop();
	}

	/// <summary>Fetch the player's loadout, then apply it on the game thread (data first, so
	/// the weapons are re-given with the new finishes, not the old ones).</summary>
	internal void RefreshFromApi(PlayerInfo info, bool announce)
	{
		var sync = WeaponSync;
		if (sync == null) return;
		TrackPlayer(info);
		_ = Task.Run(async () =>
		{
			if (await sync.GetPlayerData(info))
				Server.NextFrame(() => ApplyLoadout(info, announce));
		});
	}

	/// <summary>Game thread. Re-gives gloves, weapons, agent, music kit and pin from the
	/// loadout already in memory. A dead player gets it on respawn anyway.</summary>
	internal void ApplyLoadout(PlayerInfo info, bool announce)
	{
		var player = Utilities.GetPlayerFromSlot(info.Slot);
		if (player is not { IsValid: true, IsBot: false } || player.SteamID.ToString() != info.SteamId)
			return;

		// Mid-round nothing is re-given: the weapons would vanish and come back (with a
		// full magazine when the ammo restore misses, so editing the loadout was a free
		// reload), and the glove refresh switches weapons. A player alive in a live round
		// gets the change on their next spawn (the next round in a match, the respawn in
		// DM); OnPlayerSpawn already re-applies gloves, agent, music kit and pin.
		if (player.PawnIsAlive && RoundIsLive())
		{
			PendingWeaponRefresh[player.Slot] = true;
			if (announce) Announce(player, "wp_live_refresh_next");
			return;
		}

		GivePlayerGloves(player);
		RefreshWeapons(player);
		GivePlayerAgent(player);
		GivePlayerMusicKit(player);
		AddTimer(0.15f, () => GivePlayerPin(player));

		if (announce) Announce(player, "wp_live_refresh");
	}

	private void Announce(CCSPlayerController player, string key)
	{
		var message = Localizer[key];
		if (!message.ResourceNotFound && !string.IsNullOrEmpty(message.Value))
			player.Print(message.Value);
	}

	/// <summary>A round is being played: not warmup, not freeze time. Without a game rules
	/// entity it counts as not live (loadouts apply at once, as before).</summary>
	internal static bool RoundIsLive()
	{
		var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
			.FirstOrDefault()?.GameRules;
		return rules is { WarmupPeriod: false, FreezePeriod: false };
	}

	private void StartLiveRefresh()
	{
		_liveCts?.Cancel();
		_liveCts = null;
		var sync = WeaponSync;
		if (!Config.LiveRefresh || sync == null) return;

		_liveCts = new CancellationTokenSource();
		var stop = _liveCts.Token;
		_ = Task.Run(() => sync.RunLiveUpdates(stop));
	}

	private void StopLiveRefresh()
	{
		_liveCts?.Cancel();
		_liveCts = null;
	}
}
