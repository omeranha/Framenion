using System;
using System.Collections.Generic;
using System.Linq;

namespace framenion.Src.Ocr;

public static class RewardPool
{
	/// <summary>
	/// Reward names that are not prime parts. They stay in the match pool so a card holding one
	/// absorbs the read instead of being fuzzy-matched onto a prime part, then get dropped
	/// before the overlay is shown.
	/// </summary>
	public static readonly string[] IgnoredRewards = [
		"Forma Blueprint",
		"2X Forma Blueprint",
		"Forma",
		"Kuva",
		"Exilus Warframe Adapter Blueprint",
		"Riven Sliver",
		"Ayatan Amber Star",
	];

	public static bool IsIgnored(string name) => IgnoredRewards.Contains(name, StringComparer.OrdinalIgnoreCase);

	public static List<string> Build(IEnumerable<string> itemNames)
	{
		return itemNames.Where(s => !string.IsNullOrWhiteSpace(s)).Concat(IgnoredRewards)
			.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}
}
