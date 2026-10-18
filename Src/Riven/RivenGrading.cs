using System;
using System.Collections.Generic;
using System.Linq;

namespace framenion.Src.Riven;

/// <summary>
/// Inverts the game's riven roll formula to recover how good each rolled value is, then grades it.
/// Forward (buff):  base * (1.5 * disp * 10) * 1.25^curses * lerp(0.9, 1.1, roll) * buffsAtten * (rank + 1)
/// Forward (curse): -base * (1.5 * disp * 10) * lerp(0.9, 1.1, roll) * curseAtten * buffsAtten * (rank + 1)
/// </summary>
public static class RivenGrading
{
	public const int MaxRivenModRank = 8;
	private const int maxBuffs = 3;
	private const int maxCurses = 1;

	private static readonly double[] numBuffsAtten = [0, 1, 0.66000003, 0.5, 0.40000001, 0.34999999];

	/// <summary>Curse-specific attenuation, indexed by the number of buffs (not curses).</summary>
	private static readonly double[] numBuffsCurseAtten = [0, 1, 0.33000001, 0.5, 1.25, 1.5];

	private const double specificFitAtten = 1.5;
	private const double baseDrain = 10;

	// Display values round to 0.1%, so a legitimate min/max roll can sit a hair outside [0,1].
	private const double fitTolerance = 0.02;

	private static readonly (double Min, string Grade)[] gradeThresholds = [
		(9.5, "S"), (7.5, "A+"), (5.5, "A"), (3.5, "A-"), (1.5, "B+"),
		(-1.5, "B"), (-3.5, "B-"), (-5.5, "C+"), (-7.5, "C"), (-9.5, "C-"),
	];

	private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

	private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

	public static string FloatToGrade(double rollFloat, bool isCurse)
	{
		double f = isCurse ? 1 - rollFloat : rollFloat;
		double score = Lerp(-10, 10, f);
		foreach (var (min, grade) in gradeThresholds) {
			if (score >= min) return grade;
		}
		return "F";
	}

	public static double UnparseBuff(double displayedValue, double baseValue, double disposition, int numBuffs, int numCurses, string? tag, int rank)
	{
		double buffsAtten = numBuffsAtten[Math.Min(numBuffs, numBuffsAtten.Length - 1)];
		double curseAtten = Math.Pow(1.25, numCurses);
		double attenuation = specificFitAtten * disposition * baseDrain;

		double value = tag != null && RivenStatTables.NonPercentageTags.Contains(tag)
			? displayedValue
			: displayedValue / 100;

		if (baseValue == 0 || attenuation == 0 || buffsAtten == 0 || curseAtten == 0) return 0.5;

		value /= rank + 1;
		value /= buffsAtten;
		value /= curseAtten;
		value /= attenuation;
		// OCR values are unsigned; abs handles negative-base stats such as recoil.
		value /= Math.Abs(baseValue);

		return (value - 0.9) / 0.2;
	}

	public static double UnparseCurse(double displayedValue, double baseValue, double disposition, int numBuffs, int numCurses, string? tag, int rank)
	{
		double attenuation = specificFitAtten * disposition * baseDrain;
		// Note the swapped indexing: the buff table by curse count, the curse table by buff count.
		double cursesInBuffTable = numBuffsAtten[Math.Min(numCurses, numBuffsAtten.Length - 1)];
		double buffsInCurseTable = numBuffsCurseAtten[Math.Min(numBuffs, numBuffsCurseAtten.Length - 1)];

		double value = tag != null && RivenStatTables.NonPercentageTags.Contains(tag)
			? Math.Abs(displayedValue)
			: Math.Abs(displayedValue) / 100;

		if (baseValue == 0 || attenuation == 0 || cursesInBuffTable == 0 || buffsInCurseTable == 0) return 0.5;

		value /= rank + 1;
		value /= cursesInBuffTable;
		value /= buffsInCurseTable;
		value /= attenuation;
		value /= Math.Abs(baseValue);

		return (value - 0.9) / 0.2;
	}

	/// <summary>
	/// Forward direction of <see cref="UnparseBuff"/>: the value the card prints for a roll.
	/// Inventory fingerprints store the roll position, so the displayed value has to be
	/// computed rather than read.
	/// </summary>
	public static double ParseBuff(double rollFloat, double baseValue, double disposition, int numBuffs, int numCurses, string? tag, int rank)
	{
		double buffsAtten = numBuffsAtten[Math.Min(numBuffs, numBuffsAtten.Length - 1)];
		double curseAtten = Math.Pow(1.25, numCurses);
		double attenuation = specificFitAtten * disposition * baseDrain;

		// Magnitude only, like the scanned values: the sign lives in the stat's polarity.
		double value = Math.Abs(baseValue) * attenuation * curseAtten * buffsAtten * (rank + 1) * Lerp(0.9, 1.1, rollFloat);
		return tag != null && RivenStatTables.NonPercentageTags.Contains(tag) ? value : value * 100;
	}

	/// <summary>Forward direction of <see cref="UnparseCurse"/>, with the same swapped indexing.</summary>
	public static double ParseCurse(double rollFloat, double baseValue, double disposition, int numBuffs, int numCurses, string? tag, int rank)
	{
		double attenuation = specificFitAtten * disposition * baseDrain;
		double cursesInBuffTable = numBuffsAtten[Math.Min(numCurses, numBuffsAtten.Length - 1)];
		double buffsInCurseTable = numBuffsCurseAtten[Math.Min(numBuffs, numBuffsCurseAtten.Length - 1)];

		double value = Math.Abs(baseValue) * attenuation * cursesInBuffTable * buffsInCurseTable * (rank + 1) * Lerp(0.9, 1.1, rollFloat);
		return tag != null && RivenStatTables.NonPercentageTags.Contains(tag) ? value : value * 100;
	}

	/// <summary>
	/// Grades a riven whose pool, weapon and roll positions are all known exactly, which is the
	/// case for anything read out of the inventory. <see cref="Grade"/> brute-forces all three
	/// because OCR only ever sees the rendered values.
	/// </summary>
	public static RivenGradeResult GradeKnown(string rivenTypeKey, double disposition, bool melee, int rank,
		IReadOnlyList<(string Tag, double RollFloat)> buffs, IReadOnlyList<(string Tag, double RollFloat)> curses)
	{
		var graded = new List<GradedRivenStat>(buffs.Count + curses.Count);
		double scoreSum = 0;
		double rollSum = 0;
		int scored = 0;

		// Buffs first, then the curse, which is the order the card prints them in.
		foreach (var (rawTag, roll, positive) in buffs.Select(b => (b.Tag, b.RollFloat, true))
			.Concat(curses.Select(c => (c.Tag, c.RollFloat, false)))) {
			var tag = RivenStatTables.WeaponDamageTag(rawTag, melee);
			var entry = RivenData.FindUpgradeEntry(rivenTypeKey, tag);
			double rollFloat = Clamp01(roll);
			string display = RivenStatTables.TagToDisplayName(tag, melee);

			double? value = null;
			bool multiplier = RivenStatTables.IsMultiplierTag(tag);
			if (entry != null) {
				double magnitude = positive
					? ParseBuff(rollFloat, entry.BaseValue, disposition, buffs.Count, curses.Count, tag, rank)
					: ParseCurse(rollFloat, entry.BaseValue, disposition, buffs.Count, curses.Count, tag, rank);
				// A faction-damage line prints as a factor, so the card shows 1 ± the roll.
				value = multiplier ? (positive ? 1 + magnitude : 1 - magnitude) : magnitude;
			}

			graded.Add(new GradedRivenStat {
				Name = display,
				Positive = positive,
				// Weapon Recoil is the one buff the card prints with a minus sign.
				DisplayPositive = RivenStatTables.InvertedPolarityStats.Contains(display.ToLowerInvariant()) ? !positive : null,
				Value = value,
				Multiplier = multiplier,
				Grade = entry != null ? FloatToGrade(rollFloat, !positive) : "?",
				RollFloat = rollFloat,
			});

			if (entry == null) {
				Log.Debug($"[Rivens] No base value for '{tag}' in {rivenTypeKey}; left ungraded.");
				continue;
			}

			scoreSum += Lerp(-10, 10, positive ? rollFloat : 1 - rollFloat);
			rollSum += rollFloat;
			scored++;
		}

		double overallScore = scored > 0 ? scoreSum / scored : 0;
		string overall = "F";
		foreach (var (min, grade) in gradeThresholds) {
			if (overallScore >= min) { overall = grade; break; }
		}

		return new RivenGradeResult {
			Stats = graded,
			OverallGrade = overall,
			StatPerfectness = scored > 0 ? rollSum / scored : 0,
			Disposition = disposition,
			AssumedRank = rank,
			RivenTypeName = RivenData.GetRivenType(rivenTypeKey)?.Name ?? "",
		};
	}

	private sealed class Prepared
	{
		public required RivenStat Stat { get; init; }
		public string? Tag { get; init; }
		public RivenUpgradeEntry? Entry { get; set; }
		public double? DisplayedValue { get; init; }
		public double HalfStep { get; init; }
		public bool Gradeable => Tag != null && Entry != null && DisplayedValue != null;
	}

	/// <summary>
	/// Grades a scanned card. The stat pool, disposition and rank are all inferred by which
	/// combination makes every rolled value land inside its legal range.
	/// </summary>
	public static RivenGradeResult? Grade(string weaponName, IReadOnlyList<RivenStat> stats, int? modRank = null)
	{
		if (stats.Count == 0) return null;

		var weapon = RivenData.FindWeapon(weaponName);
		if (weapon == null) {
			Log.Warn($"[Riven] Weapon not found: \"{weaponName}\"");
			return null;
		}

		var candidateTypes = RivenData.CandidateRivenTypes(weaponName);
		if (candidateTypes.Count == 0) {
			Log.Warn($"[Riven] No riven stat pool for weapon: \"{weaponName}\"");
			return null;
		}

		int numBuffs = stats.Count(s => s.Positive);
		int numCurses = stats.Count(s => !s.Positive);
		if (numBuffs > maxBuffs || numCurses > maxCurses) {
			Log.Warn($"[Riven] Impossible stat shape ({numBuffs} buffs / {numCurses} curses); skipping grade.");
			return null;
		}

		bool melee = RivenData.IsMelee(weaponName);
		int? statedRank = modRank is >= 0 and <= MaxRivenModRank ? modRank : null;

		var dispositions = new List<double> { weapon.Disposition };
		dispositions.AddRange(RivenData.GetFamilyVariants(weaponName).Select(v => v.Disposition));
		dispositions = [.. dispositions.Distinct()];

		// Both counts scale every displayed value, so a dropped line reads the survivors high:
		// the scanned counts are a lower bound.
		var statCounts = new List<(int Buffs, int Curses)>();
		for (int buffs = numBuffs; buffs <= Math.Max(numBuffs, maxBuffs); buffs++) {
			for (int curses = numCurses; curses <= Math.Max(numCurses, maxCurses); curses++) {
				statCounts.Add((buffs, curses));
			}
		}

		var ranks = statedRank != null
			? new[] { statedRank.Value }.Concat(Enumerable.Range(0, MaxRivenModRank + 1).Reverse().Where(r => r != statedRank.Value)).ToArray()
			: [.. Enumerable.Range(0, MaxRivenModRank + 1).Reverse()];

		// Search the whole space and keep whichever combination fits best.
		string? bestType = null;
		double bestDisposition = weapon.Disposition;
		int bestRank = statedRank ?? MaxRivenModRank;
		(int Buffs, int Curses) bestCounts = (numBuffs, numCurses);
		double bestViolation = double.PositiveInfinity;
		bool bestAllFit = false;

		foreach (var typeKey in candidateTypes) {
			var prepared = Prepare(stats, typeKey, melee);
			var gradeable = prepared.Where(p => p.Gradeable).ToList();
			if (gradeable.Count == 0) continue;

			foreach (var disposition in dispositions) {
				foreach (var rank in ranks) {
					foreach (var (buffs, curses) in statCounts) {
						bool allFit = gradeable.All(p => FitsAt(p, disposition, rank, buffs, curses));
						double violation = gradeable.Sum(p => {
							double f = RawFloat(p, disposition, rank, buffs, curses, p.DisplayedValue!.Value);
							return Math.Max(0, f - 1) + Math.Max(0, -f);
						});

						bool better = allFit switch {
							true when !bestAllFit => true,
							true when bestAllFit => violation < bestViolation - 1e-9,
							false when bestAllFit => false,
							_ => violation < bestViolation - 1e-9,
						};

						if (!better) continue;
						bestAllFit = allFit;
						bestViolation = violation;
						bestType = typeKey;
						bestDisposition = disposition;
						bestRank = rank;
						bestCounts = (buffs, curses);
					}
				}
			}
		}

		if (bestType == null) {
			Log.Warn($"[Riven] No stat in the read matched any pool for \"{weaponName}\"; skipping grade.");
			return null;
		}

		if (!bestAllFit) {
			Log.Info($"[Riven] \"{weaponName}\" values do not fit any disposition/rank cleanly (violation {bestViolation:F3}); grading on the closest.");
		}
		if (Math.Abs(bestDisposition - weapon.Disposition) > 1e-9) {
			Log.Info($"[Riven] \"{weaponName}\" dispo misfits the rolled values; grading at {bestDisposition:F2}.");
		}
		if (bestRank != (statedRank ?? MaxRivenModRank)) {
			Log.Info($"[Riven] \"{weaponName}\" values match rank {bestRank}.");
		}

		var finalPrepared = Prepare(stats, bestType, melee);
		var graded = new List<GradedRivenStat>(finalPrepared.Count);
		double scoreSum = 0;
		int scoredCount = 0;
		double rollSum = 0;
		int rollCount = 0;

		foreach (var p in finalPrepared) {
			if (p.Tag == null || p.Entry == null) {
				graded.Add(ToGraded(p.Stat, "B", 0.5));
				scoreSum += 0;
				scoredCount++;
				continue;
			}

			if (p.DisplayedValue == null) {
				graded.Add(ToGraded(p.Stat, "?", 0.5));
				continue;
			}

			double rollFloat = Clamp01(RawFloat(p, bestDisposition, bestRank, bestCounts.Buffs, bestCounts.Curses, p.DisplayedValue.Value));
			string grade = FloatToGrade(rollFloat, !p.Stat.Positive);
			graded.Add(ToGraded(p.Stat, grade, rollFloat));

			scoreSum += Lerp(-10, 10, p.Stat.Positive ? rollFloat : 1 - rollFloat);
			scoredCount++;
			rollSum += rollFloat;
			rollCount++;
		}

		double overallScore = scoredCount > 0 ? scoreSum / scoredCount : 0;
		string overall = "F";
		foreach (var (min, grade) in gradeThresholds) {
			if (overallScore >= min) { overall = grade; break; }
		}

		return new RivenGradeResult {
			Stats = graded,
			OverallGrade = overall,
			StatPerfectness = rollCount > 0 ? rollSum / rollCount : 0,
			Disposition = bestDisposition,
			AssumedRank = bestRank,
			RivenTypeName = RivenData.GetRivenType(bestType)?.Name ?? "",
		};
	}

	private static GradedRivenStat ToGraded(RivenStat stat, string grade, double rollFloat) => new() {
		Name = stat.Name,
		Positive = stat.Positive,
		DisplayPositive = stat.DisplayPositive,
		Value = stat.Value,
		Multiplier = stat.Multiplier,
		Grade = grade,
		RollFloat = rollFloat,
	};

	private static List<Prepared> Prepare(IReadOnlyList<RivenStat> stats, string rivenTypeKey, bool melee)
	{
		var prepared = new List<Prepared>(stats.Count);
		foreach (var stat in stats) {
			var tag = RivenStatTables.StatNameToTag(stat.Name);
			if (tag != null) tag = RivenStatTables.WeaponDamageTag(tag, melee);

			var entry = tag != null ? RivenData.FindUpgradeEntry(rivenTypeKey, tag) : null;
			bool isFraction = tag != null && RivenStatTables.NonPercentageTags.Contains(tag);

			double? displayed = null;
			// Half a display step: the card shows one decimal, two for an x-multiplier.
			double halfStep = 0.05;
			if (stat.Value != null && double.IsFinite(stat.Value.Value)) {
				if (stat.Multiplier) {
					// "x1.05" is a +0.05 multiplier; faction damage displays that fraction directly.
					double fraction = stat.Positive ? stat.Value.Value - 1 : 1 - stat.Value.Value;
					displayed = isFraction ? fraction : fraction * 100;
					halfStep = isFraction ? 0.005 : 0.5;
				} else {
					displayed = stat.Value.Value;
				}
			}

			prepared.Add(new Prepared {
				Stat = stat,
				Tag = tag,
				Entry = entry,
				DisplayedValue = displayed,
				HalfStep = halfStep,
			});
		}
		return prepared;
	}

	private static double RawFloat(Prepared p, double disposition, int rank, int buffs, int curses, double value)
		=> p.Stat.Positive
			? UnparseBuff(value, p.Entry!.BaseValue, disposition, buffs, curses, p.Tag, rank)
			: UnparseCurse(value, p.Entry!.BaseValue, disposition, buffs, curses, p.Tag, rank);

	/// <summary>
	/// Asks whether the rounding interval around the displayed value can reach [0, 1]: a short
	/// range like Wolf Sledge's at rank 0 spans less than one display step.
	/// </summary>
	private static bool FitsAt(Prepared p, double disposition, int rank, int buffs, int curses)
	{
		double a = RawFloat(p, disposition, rank, buffs, curses, p.DisplayedValue!.Value - p.HalfStep);
		double b = RawFloat(p, disposition, rank, buffs, curses, p.DisplayedValue!.Value + p.HalfStep);
		return Math.Min(a, b) <= 1 + fitTolerance && Math.Max(a, b) >= -fitTolerance;
	}
}
