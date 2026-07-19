using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using CounterStrikeSharp.API.Modules.Utils;
using Newtonsoft.Json.Linq;

namespace WeaponPaints;

internal class WeaponSynchronization
{
	private readonly WeaponPaintsConfig _config;
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
	// ponytail: debounce writebacks per steamid; ceiling = one request / 5s under spray
	private static readonly ConcurrentDictionary<string, long> LastStatTrakSyncMs = new();
	private const int StatTrakDebounceMs = 5000;

	internal WeaponSynchronization(WeaponPaintsConfig config)
	{
		_config = config;
	}

	internal async Task GetPlayerData(PlayerInfo? player)
	{
		try
		{
			if (player == null || string.IsNullOrEmpty(player.SteamId))
				return;

			if (string.IsNullOrWhiteSpace(_config.ApiUrl) || string.IsNullOrWhiteSpace(_config.ApiKey))
			{
				Utility.Log("ApiUrl/ApiKey not configured — skipping skin load");
				return;
			}

			var baseUrl = _config.ApiUrl.TrimEnd('/');
			var url = $"{baseUrl}/api/game/skins/{player.SteamId}";

			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

			using var response = await Http.SendAsync(request).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				Utility.Log($"Skins API HTTP {(int)response.StatusCode} for {player.SteamId}");
				return;
			}

			var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
			var root = JObject.Parse(body);
			var data = root["data"] as JObject;
			if (data == null)
			{
				Utility.Log($"Skins API missing data for {player.SteamId}");
				return;
			}

			if (_config.Additional.KnifeEnabled)
				ApplyKnives(player, data["knife"]);
			if (_config.Additional.GloveEnabled)
				ApplyGloves(player, data["gloves"]);
			if (_config.Additional.AgentEnabled)
				ApplyAgents(player, data["agents"]);
			if (_config.Additional.MusicEnabled)
				ApplyMusic(player, data["music"]);
			if (_config.Additional.SkinEnabled)
				ApplySkins(player, data["skins"] as JArray);
			if (_config.Additional.PinsEnabled)
				ApplyPins(player, data["pins"]);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"An error occurred: {ex.Message}");
		}
	}

	/// <summary>
	/// Persist StatTrak flags + kill counts to Rails. Call on disconnect (force) and after kills (debounced).
	/// </summary>
	internal async Task SyncStatTrakToDatabase(PlayerInfo? player, bool force = false)
	{
		try
		{
			if (player == null || string.IsNullOrEmpty(player.SteamId))
				return;
			if (string.IsNullOrWhiteSpace(_config.ApiUrl) || string.IsNullOrWhiteSpace(_config.ApiKey))
				return;
			if (!WeaponPaints.GPlayerWeaponsInfo.TryGetValue(player.Slot, out var byTeam) || byTeam.IsEmpty)
				return;

			if (!force)
			{
				var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				var last = LastStatTrakSyncMs.GetOrAdd(player.SteamId, 0);
				if (now - last < StatTrakDebounceMs)
					return;
				LastStatTrakSyncMs[player.SteamId] = now;
			}

			var weapons = CollectStatTrakUpdates(byTeam, allPainted: force);
			if (weapons.Count == 0)
				return;

			var baseUrl = _config.ApiUrl.TrimEnd('/');
			var url = $"{baseUrl}/api/game/skins/{player.SteamId}";
			var payload = new JObject { ["weapons"] = weapons };

			using var request = new HttpRequestMessage(HttpMethod.Patch, url);
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
			request.Content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");

			using var response = await Http.SendAsync(request).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
				Utility.Log($"StatTrak sync HTTP {(int)response.StatusCode} for {player.SteamId}");
		}
		catch (Exception ex)
		{
			Utility.Log($"StatTrak sync error: {ex.Message}");
		}
	}

	private static JArray CollectStatTrakUpdates(
		ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>> byTeam,
		bool allPainted = false)
	{
		var updates = new JArray();
		var emitted = new HashSet<WeaponInfo>(ReferenceEqualityComparer.Instance);

		byTeam.TryGetValue(CsTeam.Terrorist, out var tWeapons);
		byTeam.TryGetValue(CsTeam.CounterTerrorist, out var ctWeapons);

		var defindexes = new HashSet<int>();
		if (tWeapons != null)
			foreach (var k in tWeapons.Keys) defindexes.Add(k);
		if (ctWeapons != null)
			foreach (var k in ctWeapons.Keys) defindexes.Add(k);

		foreach (var defindex in defindexes)
		{
			WeaponInfo? tInfo = null;
			WeaponInfo? ctInfo = null;
			tWeapons?.TryGetValue(defindex, out tInfo);
			ctWeapons?.TryGetValue(defindex, out ctInfo);

			if (tInfo != null && ctInfo != null && ReferenceEquals(tInfo, ctInfo))
			{
				TryAddUpdate(updates, emitted, defindex, 0, tInfo, allPainted);
				continue;
			}

			if (tInfo != null)
				TryAddUpdate(updates, emitted, defindex, 2, tInfo, allPainted);
			if (ctInfo != null)
				TryAddUpdate(updates, emitted, defindex, 3, ctInfo, allPainted);
		}

		return updates;
	}

	private static void TryAddUpdate(
		JArray updates,
		HashSet<WeaponInfo> emitted,
		int defindex,
		int team,
		WeaponInfo info,
		bool allPainted)
	{
		if (info.Paint <= 0)
			return;
		if (!IsStatTrakable(defindex))
			return;
		// Debounced kill sync: only enabled StatTrak (or weapons with kills).
		// Forced flush (disconnect): every painted ST-capable gun so counts persist.
		if (!allPainted && !info.StatTrak && info.StatTrakCount <= 0)
			return;
		if (!emitted.Add(info))
			return;

		updates.Add(new JObject
		{
			["weapon_defindex"] = defindex,
			["weapon_team"] = team,
			["weapon_stattrak"] = true,
			["weapon_stattrak_count"] = info.StatTrakCount
		});
	}

	private static CsTeam TeamFromInt(int team) => team switch
	{
		2 => CsTeam.Terrorist,
		3 => CsTeam.CounterTerrorist,
		_ => CsTeam.None,
	};

	/// <summary>
	/// CS2 StatTrak exists for Weapon + Melee paints. Not gloves / grenades / C4.
	/// Zeus (31) has StatTrak skins since Kilowatt Case (2024).
	/// </summary>
	private static bool IsStatTrakable(int defindex)
	{
		if (defindex >= 500 && defindex < 5000) return true; // knives
		if (defindex is 4725 or (>= 5027 and <= 5035)) return false; // gloves
		if (defindex is >= 43 and <= 49) return false; // nades + c4
		if (defindex is 37 or 41 or 42 or 59) return false; // shield / egg / default knives
		return defindex is (>= 1 and <= 40) or (>= 60 and <= 64);
	}

	private static IEnumerable<JObject> AsObjectList(JToken? token)
	{
		if (token == null || token.Type == JTokenType.Null)
			yield break;
		if (token is JArray arr)
		{
			foreach (var item in arr)
			{
				if (item is JObject o)
					yield return o;
			}
			yield break;
		}
		if (token is JObject single)
			yield return single;
	}

	private void ApplyKnives(PlayerInfo player, JToken? token)
	{
		foreach (var row in AsObjectList(token))
		{
			var knife = row.Value<string>("knife");
			if (string.IsNullOrEmpty(knife)) continue;

			var weaponTeam = TeamFromInt(row.Value<int?>("weapon_team") ?? 0);
			var playerKnives = WeaponPaints.GPlayersKnife.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, string>());

			if (weaponTeam == CsTeam.None)
			{
				playerKnives[CsTeam.Terrorist] = knife;
				playerKnives[CsTeam.CounterTerrorist] = knife;
			}
			else
			{
				playerKnives[weaponTeam] = knife;
			}
		}
	}

	private void ApplyGloves(PlayerInfo player, JToken? token)
	{
		foreach (var row in AsObjectList(token))
		{
			var def = row.Value<int?>("weapon_defindex");
			if (def == null) continue;

			var weaponTeam = TeamFromInt(row.Value<int?>("weapon_team") ?? 0);
			var playerGloves = WeaponPaints.GPlayersGlove.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, ushort>());

			if (weaponTeam == CsTeam.None)
			{
				playerGloves[CsTeam.Terrorist] = (ushort)def.Value;
				playerGloves[CsTeam.CounterTerrorist] = (ushort)def.Value;
			}
			else
			{
				playerGloves[weaponTeam] = (ushort)def.Value;
			}
		}
	}

	private void ApplyAgents(PlayerInfo player, JToken? token)
	{
		if (token is not JObject agents) return;
		var agentCt = agents.Value<string>("agent_ct");
		var agentT = agents.Value<string>("agent_t");
		if (!string.IsNullOrEmpty(agentCt) || !string.IsNullOrEmpty(agentT))
			WeaponPaints.GPlayersAgent[player.Slot] = (agentCt, agentT);
	}

	private void ApplyMusic(PlayerInfo player, JToken? token)
	{
		foreach (var row in AsObjectList(token))
		{
			var musicId = row.Value<int?>("music_id");
			if (musicId == null) continue;

			var weaponTeam = TeamFromInt(row.Value<int?>("weapon_team") ?? 0);
			var playerMusic = WeaponPaints.GPlayersMusic.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, ushort>());

			if (weaponTeam == CsTeam.None)
			{
				playerMusic[CsTeam.Terrorist] = (ushort)musicId.Value;
				playerMusic[CsTeam.CounterTerrorist] = (ushort)musicId.Value;
			}
			else
			{
				playerMusic[weaponTeam] = (ushort)musicId.Value;
			}
		}
	}

	private void ApplyPins(PlayerInfo player, JToken? token)
	{
		foreach (var row in AsObjectList(token))
		{
			var id = row.Value<int?>("id") ?? row.Value<int?>("pin_id");
			if (id == null) continue;

			var weaponTeam = TeamFromInt(row.Value<int?>("weapon_team") ?? 0);
			var playerPins = WeaponPaints.GPlayersPin.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, ushort>());

			if (weaponTeam == CsTeam.None)
			{
				playerPins[CsTeam.Terrorist] = (ushort)id.Value;
				playerPins[CsTeam.CounterTerrorist] = (ushort)id.Value;
			}
			else
			{
				playerPins[weaponTeam] = (ushort)id.Value;
			}
		}
	}

	private void ApplySkins(PlayerInfo player, JArray? skins)
	{
		if (skins == null) return;

		var playerWeapons = WeaponPaints.GPlayerWeaponsInfo.GetOrAdd(player.Slot,
			_ => new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());

		foreach (var token in skins)
		{
			if (token is not JObject row) continue;

			int weaponDefIndex = row.Value<int?>("weapon_defindex") ?? 0;
			int weaponPaintId = row.Value<int?>("weapon_paint_id") ?? 0;
			float weaponWear = row.Value<float?>("weapon_wear") ?? 0f;
			int weaponSeed = row.Value<int?>("weapon_seed") ?? 0;
			string weaponNameTag = row.Value<string>("weapon_nametag") ?? "";
			int weaponStatTrakCount = row.Value<int?>("weapon_stattrak_count") ?? 0;
			var weaponTeam = TeamFromInt(row.Value<int?>("weapon_team") ?? 0);

			var keyChainInfo = ParseKeyChain(row.Value<string>("weapon_keychain"));

			var weaponInfo = new WeaponInfo
			{
				Paint = weaponPaintId,
				Seed = weaponSeed,
				Wear = weaponWear,
				Nametag = weaponNameTag,
				KeyChain = keyChainInfo,
				// Guns (incl. Zeus) + knives only — gloves/utility have no StatTrak in CS2.
				StatTrak = weaponPaintId > 0 && IsStatTrakable(weaponDefIndex),
				StatTrakCount = weaponStatTrakCount,
			};

			for (int i = 0; i <= 4; i++)
			{
				var stickerData = row.Value<string>($"weapon_sticker_{i}");
				if (string.IsNullOrEmpty(stickerData)) continue;

				var parts = stickerData.Split(';');
				if (parts.Length != 7 ||
				    !uint.TryParse(parts[0], out uint stickerId) ||
				    !uint.TryParse(parts[1], out uint stickerSchema) ||
				    !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float stickerOffsetX) ||
				    !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float stickerOffsetY) ||
				    !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float stickerWear) ||
				    !float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float stickerScale) ||
				    !float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out float stickerRotation))
					continue;

				if (stickerId == 0) continue;

				weaponInfo.Stickers.Add(new StickerInfo
				{
					Id = stickerId,
					Schema = stickerSchema,
					OffsetX = stickerOffsetX,
					OffsetY = stickerOffsetY,
					Wear = stickerWear,
					Scale = stickerScale,
					Rotation = stickerRotation
				});
			}

			if (weaponTeam == CsTeam.None)
			{
				var terroristWeapons = playerWeapons.GetOrAdd(CsTeam.Terrorist, _ => new ConcurrentDictionary<int, WeaponInfo>());
				var counterTerroristWeapons = playerWeapons.GetOrAdd(CsTeam.CounterTerrorist, _ => new ConcurrentDictionary<int, WeaponInfo>());
				terroristWeapons[weaponDefIndex] = weaponInfo;
				counterTerroristWeapons[weaponDefIndex] = weaponInfo;
			}
			else
			{
				var teamWeapons = playerWeapons.GetOrAdd(weaponTeam, _ => new ConcurrentDictionary<int, WeaponInfo>());
				teamWeapons[weaponDefIndex] = weaponInfo;
			}
		}
	}

	private static KeyChainInfo ParseKeyChain(string? raw)
	{
		var keyChainInfo = new KeyChainInfo();
		var keyChainParts = raw?.Split(';');
		if (keyChainParts is { Length: 5 } &&
		    uint.TryParse(keyChainParts[0], out uint keyChainId) &&
		    float.TryParse(keyChainParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float keyChainOffsetX) &&
		    float.TryParse(keyChainParts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float keyChainOffsetY) &&
		    float.TryParse(keyChainParts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float keyChainOffsetZ) &&
		    uint.TryParse(keyChainParts[4], out uint keyChainSeed))
		{
			keyChainInfo.Id = keyChainId;
			keyChainInfo.OffsetX = keyChainOffsetX;
			keyChainInfo.OffsetY = keyChainOffsetY;
			keyChainInfo.OffsetZ = keyChainOffsetZ;
			keyChainInfo.Seed = keyChainSeed;
		}
		return keyChainInfo;
	}
}
