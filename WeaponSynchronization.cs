using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;
using Newtonsoft.Json.Linq;

namespace WeaponPaints;

internal class WeaponSynchronization
{
	private readonly WeaponPaintsConfig _config;
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
	// The live-refresh long poll holds a request open for LiveRefreshWaitSeconds; each
	// request gets its own deadline.
	private static readonly HttpClient LongPoll = new() { Timeout = Timeout.InfiniteTimeSpan };
	private const int LiveDebounceMs = 1500;
	private CancellationTokenSource _wake = new();
	// ponytail: debounce writebacks per steamid; ceiling = one request / 5s under spray
	private static readonly ConcurrentDictionary<string, long> LastStatTrakSyncMs = new();
	private const int StatTrakDebounceMs = 5000;

	internal WeaponSynchronization(WeaponPaintsConfig config)
	{
		_config = config;
	}

	/// <summary>
	/// Fetch the player's loadout and replace what's in memory (an item removed on the
	/// website disappears). Records the API's `rev` for live refresh when it sends one.
	/// </summary>
	internal async Task<bool> GetPlayerData(PlayerInfo? player)
	{
		try
		{
			if (player == null || string.IsNullOrEmpty(player.SteamId))
				return false;

			if (string.IsNullOrWhiteSpace(_config.ApiUrl) || string.IsNullOrWhiteSpace(_config.ApiKey))
			{
				Utility.Log("ApiUrl/ApiKey not configured — skipping skin load");
				return false;
			}

			var baseUrl = _config.ApiUrl.TrimEnd('/');
			var url = $"{baseUrl}/api/game/skins/{player.SteamId}";

			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

			using var response = await Http.SendAsync(request).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				Utility.Log($"Skins API HTTP {(int)response.StatusCode} for {player.SteamId}");
				return false;
			}

			var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
			var root = JObject.Parse(body);
			var data = root["data"] as JObject;
			if (data == null)
			{
				Utility.Log($"Skins API missing data for {player.SteamId}");
				return false;
			}

			// Disconnected (or the slot changed hands) while we were fetching.
			if (!WeaponPaints.IsTracked(player))
				return false;

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

			if (root["rev"] is JValue { Type: JTokenType.Integer } rev)
				WeaponPaints.RecordRev(player, rev.Value<long>());
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"An error occurred: {ex.Message}");
			return false;
		}
	}

	/// <summary>Cut the current long poll short so it restarts with the new player set.</summary>
	internal void WakeLiveLoop() => Interlocked.Exchange(ref _wake, new CancellationTokenSource()).Cancel();

	/// <summary>
	/// Live refresh loop (thread pool). Sends the revs of the connected players to
	/// POST /api/game/skins/changes, which answers as soon as one of their loadouts changes
	/// (or after the wait, empty). Changed players are refetched after a short debounce and
	/// re-equipped on the game thread. Idle cost: one request per wait per server.
	/// </summary>
	internal async Task RunLiveUpdates(CancellationToken stop)
	{
		var url = $"{_config.ApiUrl.TrimEnd('/')}/api/game/skins/changes";
		var waitSeconds = Math.Clamp(_config.LiveRefreshWaitSeconds, 5, 30);
		var failures = 0;

		while (!stop.IsCancellationRequested)
		{
			var wake = _wake.Token;
			var known = WeaponPaints.LoadoutRevs.ToArray();
			try
			{
				if (known.Length == 0)
				{
					// Nobody to watch (or the API sends no revs): sleep until a player's rev is recorded.
					await Sleep(Timeout.InfiniteTimeSpan, stop, wake).ConfigureAwait(false);
					continue;
				}

				using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop, wake);
				deadline.CancelAfter(TimeSpan.FromSeconds(waitSeconds + 15));

				var payload = new JObject
				{
					["players"] = new JObject(known.Select(kv => new JProperty(kv.Key, kv.Value))),
					["wait"] = waitSeconds,
				};
				using var request = new HttpRequestMessage(HttpMethod.Post, url)
				{
					Content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"),
				};
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

				using var response = await LongPoll.SendAsync(request, deadline.Token).ConfigureAwait(false);
				if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
				{
					Utility.Log("Live refresh: this skins API has no /api/game/skins/changes; checking again in 10 min");
					await Sleep(TimeSpan.FromMinutes(10), stop, CancellationToken.None).ConfigureAwait(false);
					continue;
				}
				if (!response.IsSuccessStatusCode)
					throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

				var body = JObject.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
				failures = 0;
				if (body["changed"] is not JObject changed || !changed.HasValues)
					continue;

				// Let a burst of edits settle (several saves, sticker tweaks): one refresh per burst.
				await Sleep(TimeSpan.FromMilliseconds(LiveDebounceMs), stop, CancellationToken.None).ConfigureAwait(false);

				var allFetched = true;
				foreach (var steamId in changed.Properties().Select(p => p.Name))
				{
					if (!WeaponPaints.LivePlayers.TryGetValue(steamId, out var info)) continue;
					if (await GetPlayerData(info).ConfigureAwait(false))
						Server.NextFrame(() => WeaponPaints.Instance.ApplyLoadout(info, announce: true));
					else
						allFetched = false;
				}
				// A failed fetch leaves the old rev, so the next poll answers at once: pace it.
				if (!allFetched)
					await Sleep(TimeSpan.FromSeconds(5), stop, CancellationToken.None).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (stop.IsCancellationRequested)
			{
				break;
			}
			catch (OperationCanceledException) when (wake.IsCancellationRequested)
			{
				// The player set changed: poll again now.
			}
			catch (Exception ex)
			{
				failures++;
				var delay = TimeSpan.FromSeconds(Math.Min(60, 5 * failures));
				if (failures <= 3 || failures % 20 == 0)
					Utility.Log($"Live refresh: {ex.Message}; retrying in {delay.TotalSeconds:0}s");
				await Sleep(delay, stop, CancellationToken.None).ConfigureAwait(false);
			}
		}
	}

	private static async Task Sleep(TimeSpan delay, CancellationToken stop, CancellationToken wake)
	{
		using var either = CancellationTokenSource.CreateLinkedTokenSource(stop, wake);
		try
		{
			await Task.Delay(delay, either.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
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

	// Swap a player's entry for a freshly built one (or drop it when empty), so a refresh
	// replaces the loadout instead of merging into the old one.
	private static void Replace<TKey, TValue>(
		ConcurrentDictionary<int, ConcurrentDictionary<TKey, TValue>> map, int slot,
		ConcurrentDictionary<TKey, TValue> fresh) where TKey : notnull
	{
		if (fresh.IsEmpty)
			map.TryRemove(slot, out _);
		else
			map[slot] = fresh;
	}

	private void ApplyKnives(PlayerInfo player, JToken? token)
	{
		var playerKnives = new ConcurrentDictionary<CsTeam, string>();
		foreach (var row in AsObjectList(token))
		{
			var knife = row.Value<string>("knife");
			if (string.IsNullOrEmpty(knife)) continue;

			var weaponTeam = TeamFromInt(row.Value<int?>("weapon_team") ?? 0);

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
		Replace(WeaponPaints.GPlayersKnife, player.Slot, playerKnives);
	}

	private void ApplyGloves(PlayerInfo player, JToken? token)
	{
		var playerGloves = new ConcurrentDictionary<CsTeam, ushort>();
		foreach (var row in AsObjectList(token))
		{
			var def = row.Value<int?>("weapon_defindex");
			if (def == null) continue;

			var weaponTeam = TeamFromInt(row.Value<int?>("weapon_team") ?? 0);

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
		Replace(WeaponPaints.GPlayersGlove, player.Slot, playerGloves);
	}

	private void ApplyAgents(PlayerInfo player, JToken? token)
	{
		var agents = token as JObject;
		var agentCt = agents?.Value<string>("agent_ct");
		var agentT = agents?.Value<string>("agent_t");
		if (!string.IsNullOrEmpty(agentCt) || !string.IsNullOrEmpty(agentT))
			WeaponPaints.GPlayersAgent[player.Slot] = (agentCt, agentT);
		else
			WeaponPaints.GPlayersAgent.TryRemove(player.Slot, out _);
	}

	private void ApplyMusic(PlayerInfo player, JToken? token)
	{
		var playerMusic = new ConcurrentDictionary<CsTeam, ushort>();
		foreach (var row in AsObjectList(token))
		{
			var musicId = row.Value<int?>("music_id");
			if (musicId == null) continue;

			var weaponTeam = TeamFromInt(row.Value<int?>("weapon_team") ?? 0);

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
		Replace(WeaponPaints.GPlayersMusic, player.Slot, playerMusic);
	}

	private void ApplyPins(PlayerInfo player, JToken? token)
	{
		var playerPins = new ConcurrentDictionary<CsTeam, ushort>();
		foreach (var row in AsObjectList(token))
		{
			var id = row.Value<int?>("id") ?? row.Value<int?>("pin_id");
			if (id == null) continue;

			var weaponTeam = TeamFromInt(row.Value<int?>("weapon_team") ?? 0);

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
		Replace(WeaponPaints.GPlayersPin, player.Slot, playerPins);
	}

	private void ApplySkins(PlayerInfo player, JArray? skins)
	{
		// Built fresh and swapped in at the end. StatTrak kills counted in game since the
		// last (debounced) sync are kept: the API's count can lag behind memory.
		WeaponPaints.GPlayerWeaponsInfo.TryGetValue(player.Slot, out var previous);
		int KeptCount(CsTeam team, int defindex, int fromApi) =>
			previous != null && previous.TryGetValue(team, out var byDef) && byDef.TryGetValue(defindex, out var old)
				? Math.Max(old.StatTrakCount, fromApi)
				: fromApi;

		var playerWeapons = new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>();

		foreach (var token in skins ?? [])
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
				StatTrakCount = KeptCount(weaponTeam == CsTeam.None ? CsTeam.Terrorist : weaponTeam, weaponDefIndex, weaponStatTrakCount),
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

		if (playerWeapons.IsEmpty)
			WeaponPaints.GPlayerWeaponsInfo.TryRemove(player.Slot, out _);
		else
			WeaponPaints.GPlayerWeaponsInfo[player.Slot] = playerWeapons;
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
