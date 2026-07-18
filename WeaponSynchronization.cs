using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using CounterStrikeSharp.API.Modules.Utils;
using Newtonsoft.Json.Linq;

namespace WeaponPaints;

internal class WeaponSynchronization
{
	private readonly WeaponPaintsConfig _config;
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

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

	// ponytail: StatTrak writeback dropped — website owns counts (v1)
	internal Task SyncStatTrakToDatabase(PlayerInfo player) => Task.CompletedTask;

	private static CsTeam TeamFromInt(int team) => team switch
	{
		2 => CsTeam.Terrorist,
		3 => CsTeam.CounterTerrorist,
		_ => CsTeam.None,
	};

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
			bool weaponStatTrak = row.Value<bool?>("weapon_stattrak") ?? false;
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
				StatTrak = weaponStatTrak,
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
